namespace DebugAssistance.HotPatch;

// The on-the-fly patch kinds this project's own hot-patch feature supports. Prefix/Postfix/
// Transpiler/Finalizer map directly onto HarmonyLib.HarmonyPatchType's members of the same name;
// Replace has no HarmonyLib equivalent -- it's this project's own addition, applied under the hood
// as a generated destructive prefix (see ReplacePatchBuilder) rather than any patch role Harmony
// itself understands.
internal enum OnTheFlyPatchType
{
    Prefix,
    Postfix,
    Transpiler,
    Finalizer,
    Replace,
}
