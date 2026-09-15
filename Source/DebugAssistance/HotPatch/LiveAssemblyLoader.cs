using Mono.Cecil;

namespace DebugAssistance.HotPatch;

// The result of a completed load, tracked per source path. Generation counts up separately for
// each path, starting at 1, so reloading the same path repeatedly produces 1, 2, 3, ... while a
// different path starts its own count fresh — this is what lets the UI and the active-patch list
// show which reload of a given assembly a patch came from (e.g. "MyPatches#2").
internal readonly record struct LoadedAssembly(Assembly Assembly, int Generation);

// Loads a player-built assembly from disk for on-the-fly patching. Assembly.Load(byte[]) is used
// instead of LoadFrom so the file on disk stays unlocked for the player to rebuild without
// restarting RimWorld.
internal sealed class LiveAssemblyLoader
{
    private readonly Dictionary<string, LoadedAssembly> _loadedByPath = [];
    private readonly Dictionary<string, int> _generationByPath = [];

    // Harmony/MonoMod's own reflection-resolution caches (ReflectionHelper's AssemblyCache,
    // AssembliesCache, and ResolveReflectionCache) are static, process-wide, and keyed on assembly
    // *identity* (name + version) rather than on the runtime Assembly instance. Since the classic
    // .NET/Mono runtime RimWorld embeds never unloads a superseded generation's Assembly, and a
    // rebuilt player DLL keeps the same AssemblyVersion across rebuilds by default, reloading the
    // same path repeatedly produces multiple live assemblies that are indistinguishable to those
    // caches. That lets a Prefix/Postfix/etc. Harmony resolves for a later generation get looked up
    // against an earlier generation's now-divergent types instead — surfacing as MonoMod's
    // DynamicMethodDefinition throwing "Unexpected null in ..." (or, less visibly, silently
    // patching against the wrong generation's method). Stamping each load with a revision number
    // that's never reused for the life of the process keeps every generation's identity distinct so
    // those caches never conflate them.
    private static int _identitySequence;

    // Every generation ever loaded, keyed by its own Assembly instance rather than by path —
    // unlike _loadedByPath (which only ever holds a path's *current* generation), this is never
    // overwritten by a later Load of the same path. The classic .NET/Mono runtime RimWorld
    // embeds never unloads an old generation's Assembly instance, so a CapturedPatchFrame from
    // before a reload can still hold a live MethodBase against one of these — GetPathForAssembly
    // must keep resolving it back to a path instead of reporting it as no longer loaded.
    private readonly Dictionary<Assembly, string> _pathByAssembly = [];

    internal LoadedAssembly? GetLoaded(string path) =>
        _loadedByPath.TryGetValue(path, out var loaded) ? loaded : null;

    // The reverse lookup: given a live Assembly instance (e.g. one found on a captured stack
    // frame or CapturedPatchFrame), the on-disk path it was loaded from. Assembly.Load(byte[])
    // leaves Assembly.Location empty, so this is the only way FrameDecompiler can find bytes to
    // decompile for a hot-patched frame — see FrameDecompiler.TryResolveMethodForDecompile.
    internal string? GetPathForAssembly(Assembly assembly) =>
        _pathByAssembly.GetValueOrDefault(assembly);

    // Every path currently loaded, each with its current generation — the pool the "already
    // loaded" picker offers the player to switch into instead of loading (and thus reloading)
    // from disk again.
    internal IReadOnlyList<(string Path, LoadedAssembly Loaded)> GetAllLoaded() =>
        [.. _loadedByPath.Select(entry => (entry.Key, entry.Value))];

    internal LoadedAssembly Load(string path)
    {
        var assembly = Assembly.Load(MakeIdentityUnique(File.ReadAllBytes(path)));
        var generation = _generationByPath[path] = _generationByPath.GetValueOrDefault(path) + 1;
        var loaded = new LoadedAssembly(assembly, generation);
        _loadedByPath[path] = loaded;
        _pathByAssembly[assembly] = path;
        return loaded;
    }

    // Rewrites just the assembly's version revision, in place, to a process-wide-unique value —
    // see the _identitySequence remarks above. The simple name (what's shown to the player as
    // "AssemblyName") is untouched; only the full identity Harmony/MonoMod key their caches on
    // changes.
    private static byte[] MakeIdentityUnique(byte[] assemblyBytes)
    {
        using var stream = new MemoryStream(assemblyBytes);
        using var module = ModuleDefinition.ReadModule(stream);
        var name = module.Assembly.Name;
        var revision = Interlocked.Increment(ref _identitySequence) % (ushort.MaxValue + 1);
        name.Version = new Version(
            name.Version.Major,
            name.Version.Minor,
            name.Version.Build,
            revision
        );

        using var output = new MemoryStream();
        module.Write(output);
        return output.ToArray();
    }
}
