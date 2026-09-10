using System.Diagnostics;

namespace DebugAssistance.Capture;

// A pristine, un-tampered stack trace snapshot taken directly by StackTraceCapturePatch from a live
// Exception, bridging the short gap until LogCaptureHook can correlate it with the eventual log
// entry.
internal sealed class RawCapture(
    string errorTypeName,
    string message,
    StackFrame[] frames,
    string rawText,
    DateTime timestamp,
    IReadOnlyList<RawExceptionCause>? innerCauses = null
)
{
    public string ErrorTypeName { get; } = errorTypeName;
    public string Message { get; } = message;
    public StackFrame[] Frames { get; } = frames;

    // System.Diagnostics.StackTrace.ToString() of the same StackTrace the frames were pulled from
    // — this is what Environment.GetStackTrace(Exception, bool) itself returns, so it matches the
    // game's own log text format, unlike StackFrame.ToString()'s differently-formatted output.
    public string RawText { get; } = rawText;
    public DateTime Timestamp { get; } = timestamp;

    // e.InnerException, e.InnerException.InnerException, etc., outermost-inner-first — captured
    // live alongside e itself so a wrapper exception (TargetInvocationException, HarmonyException)
    // doesn't bury the actual cause the way it does in RimWorld's own logged text.
    public IReadOnlyList<RawExceptionCause> InnerCauses { get; } = innerCauses ?? [];
}

internal readonly record struct RawCaptureKey(string ErrorTypeName, string Message);

// One level of a live capture's InnerException chain — the same shape as RawCapture's own
// type/message/frames/text, minus a timestamp of its own since it shares the wrapping exception's.
internal sealed class RawExceptionCause(
    string errorTypeName,
    string message,
    StackFrame[] frames,
    string rawText
)
{
    public string ErrorTypeName { get; } = errorTypeName;
    public string Message { get; } = message;
    public StackFrame[] Frames { get; } = frames;
    public string RawText { get; } = rawText;
}
