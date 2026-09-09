namespace DebugAssistance.Capture;

// Result of LogTraceParser.Parse's fallback text parsing — used when no ring-buffer correlation
// was found for a logged error/exception.
internal sealed record ParsedError(
    string ErrorTypeName,
    string Message,
    IReadOnlyList<ParsedFrame> Frames
);

internal sealed record ParsedFrame(
    string RawText,
    string? DeclaringTypeName,
    string? MethodName,
    string? FileName,
    int? LineNumber,
    int? IlOffset
);
