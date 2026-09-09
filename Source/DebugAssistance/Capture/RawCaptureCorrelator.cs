namespace DebugAssistance.Capture;

// Bridges LogCaptureHook's incoming log text back to the pristine frame data
// StackTraceCapturePatch stored moments earlier, bypassing whatever the log text itself says
// (which may be HarmonyMod's collapsed "Duplicate stacktrace" placeholder). RimWorld wraps
// ex.ToString() in contextual prefixes ("Exception ticking {thing}: {ex}", etc.), so matching is
// by substring, not an exact prefix format.
internal static class RawCaptureCorrelator
{
    internal static RawCapture? FindMatch(IEnumerable<RawCapture> candidates, string logText)
    {
        RawCapture? best = null;
        foreach (var candidate in candidates)
        {
            if (
                !logText.Contains(candidate.ErrorTypeName, StringComparison.Ordinal)
                || !logText.Contains(candidate.Message, StringComparison.Ordinal)
            )
            {
                continue;
            }

            if (best is null || candidate.Timestamp > best.Timestamp)
            {
                best = candidate;
            }
        }
        return best;
    }

    // Second matching mode, tried only when FindMatch finds nothing: matches a raw capture by
    // looking for its own rendered stack trace text as a substring of the incoming log text
    // instead of its exception type/message pair.
    internal static RawCapture? FindTextMatch(IEnumerable<RawCapture> candidates, string logText)
    {
        RawCapture? best = null;
        foreach (var candidate in candidates)
        {
            if (
                candidate.RawText.Length == 0
                || !logText.Contains(candidate.RawText, StringComparison.Ordinal)
            )
            {
                continue;
            }

            if (best is null || candidate.Timestamp > best.Timestamp)
            {
                best = candidate;
            }
        }
        return best;
    }
}
