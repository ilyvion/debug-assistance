namespace DebugAssistance.Capture;

// One level of an exception's InnerException chain, captured alongside the outer CapturedError
// that wraps it (e.g. Harmony's HarmonyException wrapping the real TargetInvocationException from
// a failed TargetMethod()). Ordered outermost-inner-first on CapturedError.InnerCauses: index 0 is
// the wrapping exception's own InnerException, index 1 is that exception's InnerException, etc.
internal sealed class CapturedExceptionCause : IExposable
{
#pragma warning disable IDE0032 // reassigned via `ref` from ExposeData; not addressable as an auto property's backing field
    private string _errorTypeName;
    private string _message;
    private string _rawStackTrace;
#pragma warning restore IDE0032
    private List<CapturedStackFrame> _frames = [];

    public string ErrorTypeName => _errorTypeName;
    public string Message => _message;
    public string RawStackTrace => _rawStackTrace;
    public IReadOnlyList<CapturedStackFrame> Frames => _frames;

    public CapturedExceptionCause(
        string errorTypeName,
        string message,
        string rawStackTrace,
        IReadOnlyList<CapturedStackFrame> frames
    )
    {
        _errorTypeName = errorTypeName;
        _message = message;
        _rawStackTrace = rawStackTrace;
        _frames = [.. frames];
    }

#pragma warning disable CS8618 // Only for scribing
    private CapturedExceptionCause() { }
#pragma warning restore CS8618

    public void ExposeData()
    {
        Scribe_Values.Look(ref _errorTypeName!, "errorTypeName");
        Scribe_Values.Look(ref _message!, "message");
        Scribe_Values.Look(ref _rawStackTrace!, "rawStackTrace");
        Scribe_Collections.Look(ref _frames, "frames", LookMode.Deep);

        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            _frames ??= [];
        }
    }
}
