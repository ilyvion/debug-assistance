using MonoMod.Utils;

namespace DebugAssistance.Decompilation;

// Rebuilds the exact merged prefix/original/transpiler(s)/postfix/finalizer replacement method
// Harmony composes for a patched method, using the same internal machinery
// PatchFunctions.UpdateWrapper itself uses (HarmonySharedState.GetPatchInfo,
// PatchFunctions.GetSortedPatchMethods, MethodCreator.CreateReplacement) — but stops short of
// UpdateWrapper's own PatchTools.DetourMethod call, so building this for decompilation never
// touches the JIT hook Harmony (or another mod) has already installed for the method.
internal static class PatchedMethodBuilder
{
    // Returns the Cecil-backed DynamicMethodDefinition MethodCreator builds internally
    // (MethodCreatorConfig.patch), not the baked MethodInfo CreateReplacement() also returns —
    // MethodBase.GetMethodBody() throws InvalidOperationException for a DynamicMethod on this
    // runtime, which PatchedMethodAssemblyWriter would need if it tried to re-read the merged
    // method's IL a second time from that MethodInfo instead of straight from the Cecil
    // definition that already holds it after CreateReplacement() finishes emitting into it.
    // Null when the method isn't (or is no longer) Harmony-patched — the patch composition can
    // change between when a frame was captured and when the player asks to decompile it.
    internal static DynamicMethodDefinition? Build(MethodBase original)
    {
        var patchInfo = HarmonySharedState.GetPatchInfo(original);
        if (patchInfo is null)
        {
            return null;
        }

        var prefixes = PatchFunctions.GetSortedPatchMethods(
            original,
            patchInfo.prefixes,
            debug: false
        );
        var postfixes = PatchFunctions.GetSortedPatchMethods(
            original,
            patchInfo.postfixes,
            debug: false
        );
        var transpilers = PatchFunctions.GetSortedPatchMethods(
            original,
            patchInfo.transpilers,
            debug: false
        );
        var finalizers = PatchFunctions.GetSortedPatchMethods(
            original,
            patchInfo.finalizers,
            debug: false
        );
        var innerPrefixes = PatchFunctions.GetInfixes(patchInfo.innerprefixes);
        var innerPostfixes = PatchFunctions.GetInfixes(patchInfo.innerpostfixes);

        var config = new MethodCreatorConfig(
            original,
            source: null,
            prefixes,
            postfixes,
            transpilers,
            finalizers,
            innerPrefixes,
            innerPostfixes,
            debug: false
        );
        var creator = new MethodCreator(config);
        _ = creator.CreateReplacement();
        return config.patch;
    }
}
