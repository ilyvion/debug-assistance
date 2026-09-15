using System.Diagnostics;

namespace DebugAssistance.Capture;

// Turns a live System.Diagnostics.StackFrame[] (plus the raw text StackTrace.ToString() rendered
// for it) into the List<CapturedStackFrame> shape both error capture (LogCaptureHook) and probe
// capture (Probes.ProbeManager) store, including FrameModResolver's mod/patch resolution per
// frame.
internal static class LiveStackFrameBuilder
{
    // rawText is StackTrace.ToString(), which renders one line per frame in the same order as the
    // frames themselves — pairing them by index gives each frame the exact text the runtime itself
    // would have produced for it. A count mismatch (frames filtered out via [StackTraceHidden],
    // say) falls back to StackFrame.ToString()'s differently-formatted text rather than pairing the
    // wrong line to the wrong frame.
    internal static List<CapturedStackFrame> BuildFrames(StackFrame[] frames, string rawText)
    {
        var rawLines = SplitFrameLines(rawText, frames.Length);
        return [.. frames.Select((frame, i) => BuildFrame(frame, rawLines?[i]))];
    }

    private static string[]? SplitFrameLines(string rawText, int expectedCount)
    {
        var lines = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => line.Length > 0)
            .ToArray();
        return lines.Length == expectedCount ? lines : null;
    }

    private static CapturedStackFrame BuildFrame(StackFrame stackFrame, string? rawText)
    {
        var method = stackFrame.GetMethod();
        // GetFileLineNumber()/GetFileColumnNumber() return 0, not a real position, when the frame
        // has no PDB info.
        var lineNumber = stackFrame.GetFileLineNumber();
        var columnNumber = stackFrame.GetFileColumnNumber();
        var ilOffset = stackFrame.GetILOffset();
        var frame = new CapturedStackFrame(
            rawText ?? stackFrame.ToString(),
            method?.DeclaringType?.FullName,
            method?.Name,
            stackFrame.GetFileName(),
            lineNumber == 0 ? null : lineNumber,
            columnNumber == 0 ? null : columnNumber,
            ilOffset == StackFrame.OFFSET_UNKNOWN ? null : ilOffset
        );
        FrameModResolver.ResolveLiveFrame(frame, stackFrame);
        return frame;
    }
}
