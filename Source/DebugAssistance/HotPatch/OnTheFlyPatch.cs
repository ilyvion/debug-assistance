namespace DebugAssistance.HotPatch;

// One active on-the-fly patch: session-only, applied/removed only by HotPatchManager through its
// own dedicated Harmony instance. Never Scribed — Target/PatchMethod/AppliedMethod are live
// MethodBase references, which must never be persisted directly.
//
// PatchMethod is always the player-selected method (the prefix/postfix/.../replacement method
// picked in the UI), which is what PatchesFromAssembly/RemoveAllFromAssembly key their Module.
// Assembly attribution on. AppliedMethod is whatever was actually handed to Harmony.Patch: the
// same as PatchMethod for Prefix/Postfix/Transpiler/Finalizer, but for Replace it's the generated
// destructive-prefix shim (ReplacePatchBuilder.Build) — a DynamicMethod with no assembly of its
// own — since that's what Harmony.Unpatch needs to reverse the patch.
internal sealed record OnTheFlyPatch(
    Guid Id,
    MethodBase Target,
    MethodInfo PatchMethod,
    MethodInfo AppliedMethod,
    OnTheFlyPatchType PatchType,
    string SourceAssemblyPath,
    int SourceAssemblyGeneration
)
{
    internal static OnTheFlyPatch Create(
        MethodBase target,
        MethodInfo patchMethod,
        MethodInfo appliedMethod,
        OnTheFlyPatchType patchType,
        string sourceAssemblyPath,
        int sourceAssemblyGeneration
    ) =>
        new(
            Guid.NewGuid(),
            target,
            patchMethod,
            appliedMethod,
            patchType,
            sourceAssemblyPath,
            sourceAssemblyGeneration
        );
}
