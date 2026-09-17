namespace DebugAssistance.HotPatch;

// Caches ConveniencePatchScanner's results so the picker doesn't rescan every running mod's
// assemblies on every request -- populated once at startup (DebugAssistanceMod.Construct) and
// refreshed on demand via the hot-patch panel's "Rescan" button
// (POST /api/hotpatch/convenience-patches/rescan), the same explicit-invalidation approach
// MethodSearchCache uses rather than recomputing per request.
internal sealed class ConveniencePatchRegistry
{
    private readonly object _lock = new();
    private List<ConveniencePatch> _patches = [];

    internal IReadOnlyList<ConveniencePatch> All
    {
        get
        {
            lock (_lock)
            {
                return _patches;
            }
        }
    }

    // Scans DebugAssistance's own assembly (ConveniencePatches.cs's built-ins), every currently
    // running mod's loaded assemblies, and any hot-patch assembly the player has loaded this
    // session -- a [ConveniencePatch] declared in any of those three places is offered the same
    // way. Distinct() since DebugAssistance's own assembly is also one of LoadedModManager's
    // running mods.
    internal void Rescan()
    {
        var assemblies = new List<Assembly> { Assembly.GetExecutingAssembly() };
        assemblies.AddRange(
            LoadedModManager.RunningMods.SelectMany(mod => mod.assemblies.loadedAssemblies)
        );
        assemblies.AddRange(
            DebugAssistanceMod
                .LiveAssemblyLoader.GetAllLoaded()
                .Select(entry => entry.Loaded.Assembly)
        );

        var scanned = ConveniencePatchScanner.Scan(assemblies.Distinct());
        lock (_lock)
        {
            _patches = scanned;
        }
    }
}
