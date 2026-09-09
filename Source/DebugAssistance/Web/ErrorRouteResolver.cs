using DebugAssistance.Capture;

namespace DebugAssistance.Web;

// Pure request-path resolution logic for DebugAssistanceServer's per-error routes, kept
// separate from the HttpListenerContext-handling server itself so it's unit-testable without a
// real HTTP request (mirrors ErrorListFilter's own reason for existing as a separate class).
internal static class ErrorRouteResolver
{
    internal static CapturedError? FindError(
        IReadOnlyList<CapturedError> entries,
        string dedupeKey
    ) => entries.FirstOrDefault(e => e.DedupeKey == dedupeKey);

    internal static bool ResolveFrame(
        IReadOnlyList<CapturedError> entries,
        string dedupeKey,
        string frameIndexPart,
        out CapturedStackFrame? frame,
        out string? error
    )
    {
        frame = null;

        if (FindError(entries, dedupeKey) is not { } capturedError)
        {
            error = "Error not found";
            return false;
        }

        if (
            !int.TryParse(frameIndexPart, out var frameIndex)
            || frameIndex < 0
            || frameIndex >= capturedError.Frames.Count
        )
        {
            error = "Frame not found";
            return false;
        }

        frame = capturedError.Frames[frameIndex];
        error = null;
        return true;
    }
}
