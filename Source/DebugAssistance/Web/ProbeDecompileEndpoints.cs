using System.Net;
using DebugAssistance.Decompilation;

namespace DebugAssistance.Web;

// POST /api/probes/{dedupeKey}/frames/{frameIndex}/decompile[-patched] and
// .../patches/{patchIndex}/decompile: the probe-hit equivalents of DecompileEndpoints' error
// routes, resolved through ProbeRouteResolver/ProbesEndpoints instead of
// ErrorRouteResolver/DebugAssistanceServer.Entries.
internal static class ProbeDecompileEndpoints
{
    internal static bool ServeDecompileFrame(
        HttpListenerContext ctx,
        string dedupeKey,
        string frameIndexPart,
        bool patched
    )
    {
        if (
            !ProbeRouteResolver.ResolveFrame(
                ProbesEndpoints.Entries(),
                dedupeKey,
                frameIndexPart,
                out _,
                out var frame,
                out var notFound
            )
        )
        {
            return ctx.Response.WriteJsonError(404, notFound!);
        }

        var result = patched
            ? FrameDecompiler.DecompilePatched(frame!)
            : FrameDecompiler.Decompile(frame!);
        DecompileEndpoints.WriteDecompileResult(ctx, result);
        return true;
    }

    internal static bool ServeDecompilePatch(
        HttpListenerContext ctx,
        string dedupeKey,
        string frameIndexPart,
        string patchIndexPart
    )
    {
        if (
            !ProbeRouteResolver.ResolveFrame(
                ProbesEndpoints.Entries(),
                dedupeKey,
                frameIndexPart,
                out _,
                out var frame,
                out var notFound
            )
        )
        {
            return ctx.Response.WriteJsonError(404, notFound!);
        }

        if (
            !int.TryParse(patchIndexPart, out var patchIndex)
            || patchIndex < 0
            || patchIndex >= frame!.Patches.Count
        )
        {
            return ctx.Response.WriteJsonError(404, "Patch not found");
        }

        DecompileEndpoints.WriteDecompileResult(
            ctx,
            FrameDecompiler.Decompile(frame.Patches[patchIndex])
        );
        return true;
    }
}
