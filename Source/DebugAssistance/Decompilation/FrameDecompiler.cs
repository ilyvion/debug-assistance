using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DebugAssistance.Capture;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using MonoMod.Utils;

namespace DebugAssistance.Decompilation;

// Lazily decompiles a single stack frame's method on explicit player request (never eagerly, to
// avoid the cost of decompiling every frame up front) and maps the frame's raw IL offset onto a
// line number in this mod's own decompiled output, generating and inspecting sequence points
// against the frame's real assembly on disk instead of a synthetic dummy one. A frame's
// PDB-reported file:line (CapturedStackFrame.FileName/LineNumber) is never consulted here — a
// shipped PDB's line numbers don't reliably match the IL actually running, so using them as a
// highlight target would actively mislead.
internal static class FrameDecompiler
{
    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private readonly record struct CacheKey(string AssemblyLocation, int MetadataToken);

    private sealed record CachedMethod(
        string Code,
        IReadOnlyList<(int Offset, int Line)> SequencePoints
    );

    private static readonly Dictionary<CacheKey, CachedMethod> Cache = [];
    private static readonly object CacheLock = new();

    // Incremented only when a cache miss actually runs the decompiler — lets tests assert a
    // second request for the same frame is served from cache rather than redecompiling.
    internal static int DecompileInvocationCountForTests { get; private set; }

    internal static void ResetCacheForTests()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
        DecompileInvocationCountForTests = 0;
    }

    // Called by HotPatchEndpoints.ServeLoadAssembly after a successful reload: a rebuilt assembly
    // commonly keeps the same metadata tokens for unchanged type/method layouts, so a cache entry
    // keyed only on (assemblyLocation, metadataToken) would otherwise keep serving the previous
    // build's decompiled source for a method the player just reloaded and applied.
    internal static void InvalidateCacheForAssemblyLocation(string assemblyLocation)
    {
        lock (CacheLock)
        {
            foreach (
                var key in Cache.Keys.Where(k => k.AssemblyLocation == assemblyLocation).ToList()
            )
            {
                _ = Cache.Remove(key);
            }
        }
    }

    internal static DecompiledMethod Decompile(CapturedStackFrame frame) =>
        DecompileCore(
            frame.Assembly,
            frame.ResolvedAssemblyShortName,
            frame.DeclaringTypeName,
            frame.MethodName,
            frame.IlOffset
        );

    // A patch entry never carries its own IL offset — it only records that Harmony applied this
    // method to the frame above it, not that the exception occurred inside it — so there is never
    // a highlight target to resolve.
    internal static DecompiledMethod Decompile(CapturedPatchFrame patch) =>
        DecompileCore(
            patch.Method?.DeclaringType?.Assembly,
            assemblyShortName: null,
            patch.DeclaringTypeName,
            patch.MethodName,
            ilOffset: null
        );

    internal static DecompiledMethod DecompileCore(
        Assembly? liveAssembly,
        string? assemblyShortName,
        string? declaringTypeName,
        string? methodName,
        int? ilOffset
    )
    {
        if (
            !TryResolveMethodForDecompile(
                liveAssembly,
                assemblyShortName,
                declaringTypeName,
                methodName,
                out var method,
                out _,
                out var assemblyLocation,
                out var resolveError
            )
        )
        {
            return DecompiledMethod.Failed(resolveError);
        }

        var cached = GetOrDecompile(assemblyLocation, method.MetadataToken, out var error);
        if (cached is null)
        {
            return DecompiledMethod.Failed(error!);
        }

        var highlightLine = ilOffset is { } offset
            ? FindHighlightLine(cached.SequencePoints, offset)
            : null;
        return DecompiledMethod.Ok(cached.Code, highlightLine, assemblyLocation);
    }

    // Decompiles the actual merged prefix/original/transpiler(s)/postfix/finalizer replacement
    // Harmony composes for frame's method (PatchedMethodBuilder), rather than the plain original
    // shown by Decompile(CapturedStackFrame) above — so the frame's IL offset, which was captured
    // from that same trampoline, maps onto the right line instead of an unrelated one in the
    // original's own, differently-laid-out IL. Only meaningful for a frame FrameModResolver found
    // to be Harmony-patched (frame.Patches is non-empty); fails gracefully if the method is no
    // longer patched by the time this runs (patches can be added/removed at runtime).
    internal static DecompiledMethod DecompilePatched(CapturedStackFrame frame)
    {
        if (
            !TryResolveMethodForDecompile(
                frame.Assembly,
                frame.ResolvedAssemblyShortName,
                frame.DeclaringTypeName,
                frame.MethodName,
                out var method,
                out _,
                out var assemblyLocation,
                out var resolveError
            )
        )
        {
            return DecompiledMethod.Failed(resolveError);
        }

        using var merged = PatchedMethodBuilder.Build(method);
        if (merged is null)
        {
            return DecompiledMethod.Failed("DebugAssistance.Decompile.NotPatched".Translate());
        }

        var (code, sequencePoints, decompileError) = DecompileMergedMethod(
            merged,
            assemblyLocation
        );
        if (decompileError is not null)
        {
            return DecompiledMethod.Failed(decompileError);
        }

        var highlightLine = frame.IlOffset is { } offset
            ? FindHighlightLine(sequencePoints!, offset)
            : null;
        return DecompiledMethod.Ok(code!, highlightLine, assemblyLocation);
    }

    // Shared by DecompileCore and DecompilePatched: resolves a frame's persisted identity
    // (assembly/type/method name) back to a live MethodBase, regardless of whether a transient
    // live reference is still available. FindTypeByName is a last-resort fallback for a patch
    // frame reloaded from a save (no persisted assembly short name at all) as well as a stack
    // frame whose resolved short name doesn't match any currently loaded assembly (stale after a
    // mod update/removal). Internal rather than private: ErrorsEndpoints.ResolvePatchTarget
    // reuses this same resolution so a frame's "Patch this method" entry point is offered under
    // exactly the same conditions decompiling that frame would succeed under.
    internal static bool TryResolveMethodForDecompile(
        Assembly? liveAssembly,
        string? assemblyShortName,
        string? declaringTypeName,
        string? methodName,
        // Decompilation/NotNullWhenAttribute.cs declares its own
        // System.Diagnostics.CodeAnalysis.NotNullWhenAttribute so nullable-flow-analysis
        // attributes work despite 0Harmony's own embedded copy of it also becoming public via
        // the whole-assembly Publicize below; CS0436 (source declaration shadowing an imported
        // type of the same name) is the expected, harmless result.
#pragma warning disable CS0436 // Type conflicts with imported type
        [NotNullWhen(true)] out MethodBase? method,
        [NotNullWhen(true)] out Assembly? assembly,
        [NotNullWhen(true)] out string? assemblyLocation,
        [NotNullWhen(false)] out string? error
#pragma warning restore CS0436 // Type conflicts with imported type
    )
    {
        if (string.IsNullOrEmpty(declaringTypeName) || string.IsNullOrEmpty(methodName))
        {
            method = null;
            assembly = null;
            assemblyLocation = null;
            error = "DebugAssistance.Decompile.NoFrameInfo".Translate();
            return false;
        }

        assembly =
            liveAssembly
            ?? ResolveAssemblyByShortName(assemblyShortName)
            ?? FrameModResolver.FindTypeByName(declaringTypeName)?.Assembly;

        // Assembly.Load(byte[]) — used by LiveAssemblyLoader for our own hot-patch assemblies so
        // the file on disk stays unlocked — leaves Assembly.Location empty even though the
        // assembly is very much loaded; fall back to the path LiveAssemblyLoader loaded it from
        // so a hot-patched frame decompiles like any other, instead of reporting a false "not
        // currently loaded". The fallback result is null (not empty) when unresolvable, and
        // assembly.Location is only used once already confirmed non-empty, so a plain null check
        // below is enough to know assemblyLocation is non-empty from here on.
        assemblyLocation =
            assembly is null ? null
            : string.IsNullOrEmpty(assembly.Location)
                ? DebugAssistanceMod.LiveAssemblyLoader.GetPathForAssembly(assembly)
            : assembly.Location;

        if (assembly is null || assemblyLocation is null)
        {
            method = null;
            assembly = null;
            assemblyLocation = null;
            error = "DebugAssistance.Decompile.NotCurrentlyLoaded".Translate();
            return false;
        }

        var type = assembly.GetType(declaringTypeName, throwOnError: false);
        method = type?.GetMember(methodName, AllMembers).OfType<MethodBase>().FirstOrDefault();
        if (method is null)
        {
            assembly = null;
            assemblyLocation = null;
            error = "DebugAssistance.Decompile.MethodNotFound".Translate(
                methodName,
                declaringTypeName
            );
            return false;
        }

        error = null;
        return true;
    }

    // Name-based, not path-based — assembly *names* are the stable, cross-machine identifier to
    // search by.
    internal static Assembly? ResolveAssemblyByShortName(string? assemblyShortName)
    {
        if (string.IsNullOrEmpty(assemblyShortName))
        {
            return null;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == assemblyShortName)
            {
                return assembly;
            }
        }
        return null;
    }

    // The greatest recorded (non-hidden) sequence point at or before the target offset — mirrors
    // debugger step-through semantics, where an offset strictly between two sequence points still
    // belongs to whichever statement most recently started. Null when the target offset precedes
    // every recorded sequence point (e.g. compiler-generated prologue IL). sequencePoints must
    // already be sorted ascending by Offset.
    internal static int? FindHighlightLine(
        IReadOnlyList<(int Offset, int Line)> sequencePoints,
        int targetOffset
    )
    {
        int? line = null;
        foreach (var (offset, lineNumber) in sequencePoints)
        {
            if (offset > targetOffset)
            {
                break;
            }
            line = lineNumber;
        }
        return line;
    }

    private static CachedMethod? GetOrDecompile(
        string assemblyLocation,
        int metadataToken,
        out string? error
    )
    {
        var key = new CacheKey(assemblyLocation, metadataToken);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                error = null;
                return hit;
            }
        }

        DecompileInvocationCountForTests++;
        var (code, sequencePoints, decompileError) = DecompileFromDisk(
            assemblyLocation,
            metadataToken
        );
        if (decompileError is not null)
        {
            error = decompileError;
            return null;
        }

        var result = new CachedMethod(code!, sequencePoints!);
        lock (CacheLock)
        {
            Cache[key] = result;
        }
        error = null;
        return result;
    }

    // Decompiles, then prints through a location-tracking token writer and derives sequence
    // points, against a real on-disk assembly and a single requested member, rather than a
    // synthetic single-method dummy assembly — so sequence points are additionally filtered down
    // to the requested method's own ILFunction, since a real type's other members would otherwise
    // collide on IL offset numbers (each method's IL offsets start over at 0).
    private static (
        string? Code,
        List<(int Offset, int Line)>? SequencePoints,
        string? Error
    ) DecompileFromDisk(string assemblyLocation, int metadataToken)
    {
        try
        {
            // PrefetchMetadata alone only loads the metadata tables, not the PE sections method
            // bodies live in — decompiling needs the actual IL bytes, so this must be
            // PrefetchEntireImage or CSharpDecompiler.Decompile throws "PE image not available".
            using var peFile = new PEFile(
                assemblyLocation,
                PEStreamOptions.PrefetchEntireImage,
                MetadataReaderOptions.Default,
                utf8Decoder: null
            );
            var assemblyResolver = new UniversalAssemblyResolver(
                assemblyLocation,
                throwOnError: false,
                peFile.DetectTargetFrameworkId(),
                peFile.DetectRuntimePack(),
                PEStreamOptions.PrefetchEntireImage,
                MetadataReaderOptions.Default
            );
            var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
            var decompiler = new CSharpDecompiler(peFile, assemblyResolver, settings);

            var targetHandle = MetadataTokens.EntityHandle(metadataToken);
            // An explicit EntityHandle[] avoids ambiguity between CSharpDecompiler's
            // Decompile(EntityHandle[]) and Decompile(IEnumerable) overloads.
            EntityHandle[] handles = [targetHandle];
            var syntaxTree = decompiler.Decompile(handles);

            using var writer = new StringWriter();
            // Tabs render at whatever width the game's font/GUI skin gives them, which for
            // RimWorld's own controls turns out to be many characters wide — spaces keep
            // indentation readable, and give Text.CalcHeight's word-wrap measurement a
            // consistent, unambiguous character width to work with. This is TextWriterTokenWriter's
            // own IndentationString, a separate setting from DecompilerSettings'
            // CSharpFormattingOptions.IndentationString (which WriteIndentation never reads).
            var textWriterTokenWriter = new TextWriterTokenWriter(writer)
            {
                IndentationString = "    ",
            };
            // CreateSequencePoints needs the syntax tree's nodes to carry line/column info, which
            // a plain TextWriterTokenWriter does not record as a side effect of writing.
            var tokenWriter = TokenWriter.WrapInWriterThatSetsLocationsInAST(textWriterTokenWriter);
            syntaxTree.AcceptVisitor(
                new CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions)
            );
            var code = writer.ToString();

            var sequencePoints = decompiler
                .CreateSequencePoints(syntaxTree)
                .Where(kv => kv.Key.Method is { } m && m.MetadataToken == targetHandle)
                .SelectMany(kv => kv.Value)
                .Where(sp => !sp.IsHidden)
                .Select(sp => (sp.Offset, sp.StartLine))
                .OrderBy(p => p.Offset)
                .ToList();

            return (code, sequencePoints, null);
        }
        catch (Exception ex)
        {
            return (null, null, "DebugAssistance.Decompile.Failed".Translate(ex.Message));
        }
    }

    // Same decompile -> print through a location-tracking token writer -> CreateSequencePoints
    // shape as DecompileFromDisk, but against PatchedMethodAssemblyWriter's synthetic in-memory
    // assembly instead of a real one on disk — there's no on-disk assembly for a Harmony-merged
    // DynamicMethod to read from directly. Not cached (unlike DecompileFromDisk): the merged
    // method's own IL layout can change from one call to the next if the mod's patch composition
    // changes at runtime, and this is only ever run on an explicit, infrequent player request.
    private static (
        string? Code,
        List<(int Offset, int Line)>? SequencePoints,
        string? Error
    ) DecompileMergedMethod(DynamicMethodDefinition merged, string originalAssemblyLocation)
    {
        try
        {
            using var stream = new MemoryStream();
            PatchedMethodAssemblyWriter.WriteAssembly(stream, merged);
            stream.Position = 0;

            using var peFile = new PEFile(PatchedMethodAssemblyWriter.DummyDll, stream);
            // The merged method's own DeclaringType is a throwaway Cecil-generated one, not the
            // real mod assembly, so referenced-type resolution needs the ORIGINAL method's
            // assembly location instead, same as DecompileFromDisk uses for the unpatched path.
            var assemblyResolver = new UniversalAssemblyResolver(
                originalAssemblyLocation,
                throwOnError: false,
                peFile.DetectTargetFrameworkId(),
                peFile.DetectRuntimePack(),
                PEStreamOptions.PrefetchEntireImage,
                MetadataReaderOptions.Default
            );
            var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
            var decompiler = new CSharpDecompiler(peFile, assemblyResolver, settings);

            // The synthetic assembly holds exactly one type with exactly one method, so decompiling
            // the whole type (rather than a specific EntityHandle, as DecompileFromDisk does) needs
            // no extra per-method sequence-point filtering — there's nothing else to collide with.
            var syntaxTree = decompiler.DecompileType(
                new FullTypeName(PatchedMethodAssemblyWriter.DummyType)
            );

            using var writer = new StringWriter();
            var textWriterTokenWriter = new TextWriterTokenWriter(writer)
            {
                IndentationString = "    ",
            };
            var tokenWriter = TokenWriter.WrapInWriterThatSetsLocationsInAST(textWriterTokenWriter);
            syntaxTree.AcceptVisitor(
                new CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions)
            );
            var code = writer.ToString();

            var sequencePoints = decompiler
                .CreateSequencePoints(syntaxTree)
                .Values.SelectMany(sp => sp)
                .Where(sp => !sp.IsHidden)
                .Select(sp => (sp.Offset, sp.StartLine))
                .OrderBy(p => p.Offset)
                .ToList();

            return (code, sequencePoints, null);
        }
        catch (Exception ex)
        {
            return (null, null, "DebugAssistance.Decompile.Failed".Translate(ex.Message));
        }
    }
}
