namespace DebugAssistance.Capture;

// One entry in a CapturedStackFrame's patch list — one row per prefix/postfix/transpiler/
// finalizer Harmony has applied to the frame's original method, mirroring HarmonyMod's own
// "- PREFIX owner: Method" log annotations. Kept as separate entries (rather than a single
// combined summary string) so a frame patched by several mods, or by one mod multiple times,
// keeps each patch individually addressable for decompilation.
internal sealed class CapturedPatchFrame : IExposable
{
#pragma warning disable IDE0032 // see CapturedStackFrame's identical suppression
    private string _ownerModId;
    private string _patchKind;
#pragma warning restore IDE0032
    private string? _declaringTypeName;
    private string? _methodName;

    public string OwnerModId => _ownerModId;
    public string PatchKind => _patchKind;
    public string? DeclaringTypeName => _declaringTypeName;
    public string? MethodName => _methodName;

    // Transient, populated only for a live-captured frame — never Scribed, see
    // CapturedStackFrame's data-safety note.
    public MethodBase? Method { get; set; }

    public CapturedPatchFrame(string ownerModId, string patchKind, MethodBase method)
    {
        _ownerModId = ownerModId;
        _patchKind = patchKind;
        _declaringTypeName = method.DeclaringType?.FullName;
        _methodName = method.Name;
        Method = method;
    }

#pragma warning disable CS8618 // Only for scribing
    private CapturedPatchFrame() { }
#pragma warning restore CS8618

    public void ExposeData()
    {
        Scribe_Values.Look(ref _ownerModId!, "ownerModId");
        Scribe_Values.Look(ref _patchKind!, "patchKind");
        Scribe_Values.Look(ref _declaringTypeName, "declaringTypeName");
        Scribe_Values.Look(ref _methodName, "methodName");
    }
}
