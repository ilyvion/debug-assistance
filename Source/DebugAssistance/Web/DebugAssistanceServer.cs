using System.Net;
using DebugAssistance.Capture;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// HttpListener-based server for the web Error Inspector (hand-rolled JSON routing, no
// ASP.NET/Kestrel dependency): static files under siteFolder (the built web/ frontend) for
// anything under GET that isn't an API route, plus JSON routes under /api/ dispatched out to a
// static endpoint class per feature area (ErrorsEndpoints, DecompileEndpoints, SavesEndpoints,
// FileBrowserEndpoints, SettingsEndpoint, ProbesEndpoints, ProbeDecompileEndpoints) so each
// route's handling logic sits alongside the rest of that feature's code rather than all in one
// file.
[HotSwappable]
internal sealed class DebugAssistanceServer(
    int port,
    string siteFolder,
    bool allowExternalConnections
) : IDisposable
{
    private HttpListener? _listener;
    private readonly ManualResetEvent _stopSignal = new(initialState: false);
    private bool _disposed;

    public void Start()
    {
        var listener = new HttpListener();
        _listener = listener;
        // "localhost" binds loopback interfaces only; "+" binds all interfaces on the port,
        // which is what actually lets a request from another device reach the listener at all.
        listener.Prefixes.Add(
            allowExternalConnections ? $"http://+:{port}/" : $"http://localhost:{port}/"
        );
        listener.Start();

        HandleRequests(listener);

        listener.Close();
    }

    public void Stop() => _stopSignal.Set();

    // Each accepted connection is handed to the thread pool rather than handled inline, so a slow
    // request (decompiling a method can take a noticeable moment) doesn't stall every other
    // in-flight request behind it — e.g. the error list's background poll, or several frames'
    // decompile requests fired at once from a "decompile all" action.
    //
    // The loop races a pending accept's own wait handle against _stopSignal instead of blocking
    // on the listener alone. Stop() only ever sets _stopSignal, so it never has to unwind the
    // accept loop through an exception thrown out of a blocked accept call: EndGetContext is only
    // reached once a connection has actually arrived.
    private void HandleRequests(HttpListener listener)
    {
        while (true)
        {
            var pendingAccept = listener.BeginGetContext(callback: null, state: null);
            var signaledHandle = WaitHandle.WaitAny([pendingAccept.AsyncWaitHandle, _stopSignal]);

            if (signaledHandle == 1)
            {
                return;
            }

            HttpListenerContext ctx;
            try
            {
                ctx = listener.EndGetContext(pendingAccept);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                // An accept-level failure unrelated to Stop() (Stop() never reaches this call);
                // log it and keep serving rather than taking the whole server down over it.
                Log.Error($"DebugAssistance: HTTP listener accept failed: {e}");
                continue;
            }

            if (!IsConnectionAllowed(ctx.Request.RemoteEndPoint.Address, allowExternalConnections))
            {
                _ = ctx.Response.WriteJsonError(
                    403,
                    "Connections are only accepted from the local machine"
                );
                ctx.Response.Close();
                continue;
            }

            _ = ThreadPool.QueueUserWorkItem(_ =>
            {
                HandleRequest(ctx);
                ctx.Response.Close();
            });
        }
    }

    internal static bool IsConnectionAllowed(
        IPAddress remoteAddress,
        bool allowExternalConnections
    ) => allowExternalConnections || IPAddress.IsLoopback(remoteAddress);

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var parts = req.Url.PathParts();

        try
        {
            _ = req.HttpMethod switch
            {
                "GET" when parts.ElementAtOrDefault(0) == "api" => HandleApiGet(ctx, parts),
                "POST" when parts.ElementAtOrDefault(0) == "api" => HandleApiPost(ctx, parts),
                "DELETE" when parts.ElementAtOrDefault(0) == "api" => HandleApiDelete(ctx, parts),
                "GET" => ServeFile(ctx),
                _ => WriteNotFound(ctx),
            };
        }
        catch (Exception e)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.WriteJson(new ErrorDto { Error = e.ToString() });
        }
    }

    private static bool HandleApiGet(HttpListenerContext ctx, string[] parts) =>
        (parts.ElementAtOrDefault(1), parts.Length) switch
        {
            ("alive", 2) => true,
            ("translations", 2) => TranslationsEndpoint.ServeTranslations(ctx),
            ("settings", 2) => SettingsEndpoint.ServeSettings(ctx),
            ("errors", 2) => ErrorsEndpoints.ServeErrorList(ctx),
            ("errors", 3) => ErrorsEndpoints.ServeErrorDetail(ctx, parts[2]),
            ("saves", 2) => SavesEndpoints.ServeSavesList(ctx),
            ("files", 2) => FileBrowserEndpoints.ServeFileList(ctx),
            ("hotpatch", 3) => parts[2] switch
            {
                "methods" => HotPatchEndpoints.ServeMethodList(ctx),
                "assemblies" => HotPatchEndpoints.ServeAssemblyList(ctx),
                "namespaces" => HotPatchEndpoints.ServeNamespaceList(ctx),
                "types" => HotPatchEndpoints.ServeTypeList(ctx),
                "active" => HotPatchEndpoints.ServeActivePatches(ctx),
                "loaded-assemblies" => HotPatchEndpoints.ServeLoadedAssemblies(ctx),
                _ => WriteNotFound(ctx),
            },
            ("hotpatch", 4) when parts[2] == "scaffold" && parts[3] == "suggested-name" =>
                HotPatchEndpoints.ServeSuggestScaffoldName(ctx),
            ("probes", 2) => ProbesEndpoints.ServeProbeList(ctx),
            ("probes", 3) when parts[2] == "active" => ProbesEndpoints.ServeActiveProbes(ctx),
            ("probes", 3) => ProbesEndpoints.ServeProbeDetail(ctx, parts[2]),
            _ => WriteNotFound(ctx),
        };

    private static bool HandleApiPost(HttpListenerContext ctx, string[] parts) =>
        (
            parts.ElementAtOrDefault(1),
            parts.ElementAtOrDefault(3),
            parts.ElementAtOrDefault(5),
            parts.Length
        ) switch
        {
            ("errors", "frames", "decompile", 6) => DecompileEndpoints.ServeDecompileFrame(
                ctx,
                parts[2],
                parts[4],
                patched: false
            ),
            ("errors", "frames", "decompile-patched", 6) => DecompileEndpoints.ServeDecompileFrame(
                ctx,
                parts[2],
                parts[4],
                patched: true
            ),
            ("errors", "frames", "patches", 8) when parts[7] == "decompile" =>
                DecompileEndpoints.ServeDecompilePatch(ctx, parts[2], parts[4], parts[6]),
            ("errors", "causes", "frames", 8) when parts[7] == "decompile" =>
                DecompileEndpoints.ServeDecompileCauseFrame(
                    ctx,
                    parts[2],
                    parts[4],
                    parts[6],
                    patched: false
                ),
            ("errors", "causes", "frames", 8) when parts[7] == "decompile-patched" =>
                DecompileEndpoints.ServeDecompileCauseFrame(
                    ctx,
                    parts[2],
                    parts[4],
                    parts[6],
                    patched: true
                ),
            ("errors", "causes", "frames", 10)
                when parts[7] == "patches" && parts[9] == "decompile" =>
                DecompileEndpoints.ServeDecompileCausePatch(
                    ctx,
                    parts[2],
                    parts[4],
                    parts[6],
                    parts[8]
                ),
            ("errors", "ai-prompt", null, 4) => PromptEndpoints.ServeAiPrompt(ctx, parts[2]),
            ("settings", null, null, 3) when parts[2] == "error-capture-enabled" =>
                SettingsEndpoint.ServeSetErrorCaptureEnabled(ctx),
            ("saves", null, null, 2) => SavesEndpoints.ServeSaveCapture(ctx),
            ("saves", "load", null, 4) => SavesEndpoints.ServeLoadCapture(ctx, parts[2]),
            ("hotpatch", _, _, 3) when parts[2] == "assembly" =>
                HotPatchEndpoints.ServeLoadAssembly(ctx),
            ("hotpatch", _, _, 3) when parts[2] == "apply" => HotPatchEndpoints.ServeApplyPatch(
                ctx
            ),
            ("hotpatch", _, _, 4) when parts[2] == "remove" => HotPatchEndpoints.ServeRemovePatch(
                ctx,
                parts[3]
            ),
            ("hotpatch", _, _, 3) when parts[2] == "remove-many" =>
                HotPatchEndpoints.ServeRemoveManyPatches(ctx),
            ("hotpatch", _, _, 3) when parts[2] == "scaffold" => HotPatchEndpoints.ServeScaffold(
                ctx
            ),
            ("hotpatch", _, _, 3) when parts[2] == "debug-prompt" =>
                PromptEndpoints.ServeHotPatchDebugPrompt(ctx),
            ("probes", _, _, 3) when parts[2] == "active" => ProbesEndpoints.ServeAddProbe(ctx),
            ("probes", "frames", "decompile", 6) => ProbeDecompileEndpoints.ServeDecompileFrame(
                ctx,
                parts[2],
                parts[4],
                patched: false
            ),
            ("probes", "frames", "decompile-patched", 6) =>
                ProbeDecompileEndpoints.ServeDecompileFrame(ctx, parts[2], parts[4], patched: true),
            ("probes", "frames", "patches", 8) when parts[7] == "decompile" =>
                ProbeDecompileEndpoints.ServeDecompilePatch(ctx, parts[2], parts[4], parts[6]),
            ("probes", "ai-prompt", null, 4) => ProbesEndpoints.ServeProbeAiPrompt(ctx, parts[2]),
            _ => WriteNotFound(ctx),
        };

    private static bool HandleApiDelete(HttpListenerContext ctx, string[] parts) =>
        (parts.ElementAtOrDefault(1), parts.Length) switch
        {
            ("errors", 2) => ErrorsEndpoints.ServeClearErrors(ctx),
            ("errors", 3) => ErrorsEndpoints.ServeDeleteError(ctx, parts[2]),
            ("saves", 3) => SavesEndpoints.ServeDeleteSave(ctx, parts[2]),
            ("probes", 2) => ProbesEndpoints.ServeClearProbes(ctx),
            ("probes", 4) when parts[2] == "active" => ProbesEndpoints.ServeRemoveActiveProbe(
                ctx,
                parts[3]
            ),
            ("probes", 3) => ProbesEndpoints.ServeDeleteProbeHit(ctx, parts[2]),
            _ => WriteNotFound(ctx),
        };

    private bool ServeFile(HttpListenerContext ctx)
    {
        var reqPath = ctx.Request.Url.AbsolutePath;
        var path = siteFolder + (reqPath == "/" ? "/index.html" : reqPath);
        var fullSiteFolder = Path.GetFullPath(siteFolder) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(fullSiteFolder, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 404;
            return false;
        }

        // A route like /hotpatch has no file on disk; the SPA's own router handles it once
        // index.html loads, so any extensionless path that isn't a real file falls back to it
        // instead of 404ing. A missing asset (.js, .css, ...) still 404s normally.
        if (!File.Exists(fullPath) && !Path.HasExtension(fullPath))
        {
            fullPath = fullSiteFolder + "index.html";
        }

        if (!File.Exists(fullPath))
        {
            ctx.Response.StatusCode = 404;
            return false;
        }

        ctx.Response.WriteFile(fullPath, File.ReadAllBytes(fullPath));
        return true;
    }

    private static bool WriteNotFound(HttpListenerContext ctx) =>
        ctx.Response.WriteJsonError(404, "Not found");

    internal static IReadOnlyList<CapturedError> Entries() =>
        DebugAssistanceMod.CaptureStore.Snapshot();

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                ((IDisposable?)_listener)?.Dispose();
                _stopSignal.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
