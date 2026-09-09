using System.Diagnostics;

namespace DebugAssistance.Capture;

// Frame -> owning-mod attribution, in priority order: (1) Harmony's own public API for opaque
// patch-trampoline frames — GetOriginalMethodFromStackframe needs a live StackFrame, so this only
// applies to ring-buffer-sourced frames, not text-parsed fallback ones; (2) an assembly -> mod
// name lookup built once from the running mod list (improves on BetterStacktraces' uncached
// per-frame scan); (3) a name-based classifier for the well-known non-mod assemblies every frame
// can otherwise land in — the base game's own Assembly-CSharp, UnityEngine's modules, and the
// BCL — none of which appear in any ModContentPack's loadedAssemblies; (4) "unresolvable" for
// whatever none of those can explain (e.g. a dynamic method with no useful DeclaringType) —
// inherent to how detoured DynamicMethod trampolines work, not a bug.
internal static class FrameModResolver
{
    internal const string UnresolvableLabel = "unresolvable (dynamic method)";

    private static Dictionary<Assembly, string>? _assemblyToModName;

    internal static void ResolveLiveFrame(CapturedStackFrame frame, StackFrame stackFrame)
    {
        // GetOriginalMethodFromStackframe returns the frame's own method back, unchanged, for any
        // frame that isn't actually a Harmony patch trampoline — it is not itself a signal that
        // the frame was patched. GetPatchInfo is the real gate: it's null unless Harmony has
        // recorded patch state for the method.
        var original = Harmony.GetOriginalMethodFromStackframe(stackFrame);
        var patchInfo = original is null ? null : Harmony.GetPatchInfo(original);
        if (original is not null && patchInfo is not null)
        {
            // The frame itself is attributed to the original method's own mod/assembly (e.g.
            // "RimWorld" for a base-game method), not to whichever mod(s) patched it — that
            // attribution belongs to the individual patch entries below instead.
            frame.Method = original;
            frame.Assembly = original.DeclaringType?.Assembly;
            frame.SetResolvedOriginalMethod(original);
            ResolveByAssembly(frame, frame.Assembly);
            frame.SetPatches(BuildPatchList(patchInfo));
            return;
        }

        ResolveByAssembly(frame, stackFrame.GetMethod()?.DeclaringType?.Assembly);
    }

    // One CapturedPatchFrame per prefix/postfix/transpiler/finalizer, mirroring HarmonyMod's own
    // "- PREFIX owner: Method" / "- POSTFIX owner: Method" log annotations — a method can carry
    // several patches from the same mod (e.g. both a prefix and a postfix) or from several mods,
    // so each is kept as its own entry rather than collapsed into one summary.
    private static List<CapturedPatchFrame> BuildPatchList(HarmonyLib.Patches patchInfo)
    {
        List<CapturedPatchFrame> patches = [];
        AddPatches(patches, patchInfo.Prefixes, "prefix");
        AddPatches(patches, patchInfo.Postfixes, "postfix");
        AddPatches(patches, patchInfo.Transpilers, "transpiler");
        AddPatches(patches, patchInfo.Finalizers, "finalizer");
        return patches;
    }

    private static void AddPatches(
        List<CapturedPatchFrame> patches,
        IEnumerable<Patch> source,
        string kind
    )
    {
        foreach (var patch in source)
        {
            patches.Add(new CapturedPatchFrame(patch.owner, kind, patch.PatchMethod));
        }
    }

    internal static void ResolveParsedFrame(CapturedStackFrame frame) =>
        ResolveByAssembly(frame, FindTypeByName(frame.DeclaringTypeName)?.Assembly);

    private static void ResolveByAssembly(CapturedStackFrame frame, Assembly? assembly)
    {
        if (assembly is null)
        {
            frame.ResolvedModName = UnresolvableLabel;
            return;
        }

        frame.ResolvedAssemblyShortName = assembly.GetName().Name;
        frame.ResolvedModName =
            AssemblyToModNameCache().GetValueOrDefault(assembly)
            ?? ClassifyFrameworkAssembly(assembly)
            ?? UnresolvableLabel;
    }

    // Name-based, not path-based: assembly *names* (Assembly-CSharp, UnityEngine.CoreModule,
    // mscorlib, ...) are stable across machines and RimWorld versions, unlike install paths.
    internal static string? ClassifyFrameworkAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return name switch
        {
            "Assembly-CSharp" => "RimWorld",
            _ when name.StartsWith("UnityEngine", StringComparison.Ordinal) => "Unity",
            "mscorlib" or "netstandard" => ".NET Runtime",
            _ when name.StartsWith("System", StringComparison.Ordinal) => ".NET Runtime",
            _ => null,
        };
    }

    internal static Type? FindTypeByName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return null;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(typeName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }
        return null;
    }

    private static Dictionary<Assembly, string> AssemblyToModNameCache() =>
        _assemblyToModName ??= BuildAssemblyToModNameMap(
            LoadedModManager.RunningMods.Select(mod =>
                (mod.Name, (IEnumerable<Assembly>)mod.assemblies.loadedAssemblies)
            )
        );

    // Pure — testable with fake in-memory assemblies/mod names, no real ModContentPack needed.
    // Earlier mods in `mods` win ties for an assembly claimed by more than one mod name.
    internal static Dictionary<Assembly, string> BuildAssemblyToModNameMap(
        IEnumerable<(string ModName, IEnumerable<Assembly> Assemblies)> mods
    )
    {
        var map = new Dictionary<Assembly, string>();
        foreach (var (modName, assemblies) in mods)
        {
            foreach (var assembly in assemblies)
            {
                _ = map.TryAdd(assembly, modName);
            }
        }
        return map;
    }
}
