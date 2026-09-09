namespace DebugAssistance.Capture;

internal sealed class CapturedStackFrame : IExposable
{
    // IDE0032 ("use auto property") doesn't account for this being reassigned via `ref` from
    // ExposeData — an auto property's backing field isn't addressable that way.
#pragma warning disable IDE0032
    private string _rawText;
#pragma warning restore IDE0032
    private string? _declaringTypeName;
    private string? _methodName;

    // From a shipped PDB when present. Display-only metadata (e.g. "originally reported at
    // Foo.cs:33") — never a navigation/highlight target, since a shipped PDB's line numbers don't
    // reliably match the decompiled IL actually running.
    private string? _fileName;
    private int? _lineNumber;
    private int? _columnNumber;

    // IL offset within the method body, present even when no PDB/file info is — the fallback
    // shown in the inspector when FileName is null.
    private int? _ilOffset;

    // Set once frame->mod resolution runs. Null until then.
    private string? _resolvedModName;
    private string? _resolvedAssemblyShortName;

    // Harmony patches applied to this frame's method, one entry per prefix/postfix/transpiler/
    // finalizer. Empty for a frame Harmony has never patched.
    private List<CapturedPatchFrame> _patches = [];

    public string RawText => _rawText;
    public string? DeclaringTypeName => _declaringTypeName;
    public string? MethodName => _methodName;
    public string? FileName => _fileName;
    public int? LineNumber => _lineNumber;
    public int? ColumnNumber => _columnNumber;
    public int? IlOffset => _ilOffset;
    public IReadOnlyList<CapturedPatchFrame> Patches => _patches;

    public string? ResolvedModName
    {
        get => _resolvedModName;
        set => _resolvedModName = value;
    }

    public string? ResolvedAssemblyShortName
    {
        get => _resolvedAssemblyShortName;
        set => _resolvedAssemblyShortName = value;
    }

    // Transient, populated only when a live hook captured this frame. Never Scribed —
    // Type/MethodBase/Assembly must not be persisted directly. Absent (null) after a load, same
    // as before resolution ever ran.
    public MethodBase? Method { get; set; }
    public Assembly? Assembly { get; set; }

    // A live StackFrame for a Harmony-patched method resolves back to an opaque merged
    // prefix/original/postfix trampoline (no useful DeclaringType/MethodName of its own), not the
    // original method actually being patched. FrameModResolver calls this to replace the frame's
    // identity with that original method once it resolves one, attributing the frame to the
    // original method rather than the trampoline. The patches applied to it are recorded
    // separately via SetPatches, not folded into this identity.
    // The IL offset captured for this frame was measured against the trampoline's own IL layout
    // (prefixes/postfixes/transpilers change where everything sits relative to the original
    // method's own IL), not this original method's — so a highlight resolved against it can land
    // on the wrong line. It's kept anyway rather than discarded: a plausibly-wrong highlight is
    // still more useful to the player than no highlight at all.
    internal void SetResolvedOriginalMethod(MethodBase original)
    {
        _declaringTypeName = original.DeclaringType?.FullName;
        _methodName = original.Name;
        _rawText = $"{_declaringTypeName ?? "?"}.{_methodName}";
    }

    internal void SetPatches(List<CapturedPatchFrame> patches) => _patches = patches;

    public CapturedStackFrame(
        string rawText,
        string? declaringTypeName,
        string? methodName,
        string? fileName,
        int? lineNumber,
        int? columnNumber = null,
        int? ilOffset = null
    )
    {
        _rawText = rawText;
        _declaringTypeName = declaringTypeName;
        _methodName = methodName;
        _fileName = fileName;
        _lineNumber = lineNumber;
        _columnNumber = columnNumber;
        _ilOffset = ilOffset;
    }

#pragma warning disable CS8618 // Only for scribing
    private CapturedStackFrame() { }
#pragma warning restore CS8618

    public void ExposeData()
    {
        Scribe_Values.Look(ref _rawText!, "rawText");
        Scribe_Values.Look(ref _declaringTypeName, "declaringTypeName");
        Scribe_Values.Look(ref _methodName, "methodName");
        Scribe_Values.Look(ref _fileName, "fileName");
        Scribe_Values.Look(ref _lineNumber, "lineNumber");
        Scribe_Values.Look(ref _columnNumber, "columnNumber");
        Scribe_Values.Look(ref _ilOffset, "ilOffset");
        Scribe_Values.Look(ref _resolvedModName, "resolvedModName");
        Scribe_Values.Look(ref _resolvedAssemblyShortName, "resolvedAssemblyShortName");
        Scribe_Collections.Look(ref _patches, "patches", LookMode.Deep);
        _patches ??= [];
    }
}
