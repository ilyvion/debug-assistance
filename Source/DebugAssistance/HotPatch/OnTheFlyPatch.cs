namespace DebugAssistance.HotPatch;

// One active on-the-fly patch: session-only, applied/removed only by HotPatchManager through its
// own dedicated Harmony instance. Never Scribed — Target/PatchMethod are live MethodBase
// references, which must never be persisted directly.
internal sealed record OnTheFlyPatch(
    Guid Id,
    MethodBase Target,
    MethodInfo PatchMethod,
    HarmonyPatchType PatchType,
    string SourceAssemblyPath,
    int SourceAssemblyGeneration
)
{
    internal static OnTheFlyPatch Create(
        MethodBase target,
        MethodInfo patchMethod,
        HarmonyPatchType patchType,
        string sourceAssemblyPath,
        int sourceAssemblyGeneration
    ) =>
        new(
            Guid.NewGuid(),
            target,
            patchMethod,
            patchType,
            sourceAssemblyPath,
            sourceAssemblyGeneration
        );
}
