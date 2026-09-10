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

    internal static bool ResolveCauseFrame(
        IReadOnlyList<CapturedError> entries,
        string dedupeKey,
        string causeIndexPart,
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
            !int.TryParse(causeIndexPart, out var causeIndex)
            || causeIndex < 0
            || causeIndex >= capturedError.InnerCauses.Count
        )
        {
            error = "Cause not found";
            return false;
        }

        var cause = capturedError.InnerCauses[causeIndex];

        if (
            !int.TryParse(frameIndexPart, out var frameIndex)
            || frameIndex < 0
            || frameIndex >= cause.Frames.Count
        )
        {
            error = "Frame not found";
            return false;
        }

        frame = cause.Frames[frameIndex];
        error = null;
        return true;
    }
}
