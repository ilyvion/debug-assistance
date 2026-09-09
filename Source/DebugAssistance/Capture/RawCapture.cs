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
    DateTime timestamp
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
}

internal readonly record struct RawCaptureKey(string ErrorTypeName, string Message);
