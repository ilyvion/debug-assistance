using DebugAssistance.Capture;
using DebugAssistance.Probes;

namespace DebugAssistance.Web;

// Pure request-path resolution logic for DebugAssistanceServer's per-probe-hit routes, mirroring
// ErrorRouteResolver's own reason for existing as a separate, unit-testable class.
internal static class ProbeRouteResolver
{
    internal static CapturedProbeHit? FindHit(
        IReadOnlyList<CapturedProbeHit> entries,
        string dedupeKey
    ) => entries.FirstOrDefault(h => h.DedupeKey == dedupeKey);

    internal static bool ResolveFrame(
        IReadOnlyList<CapturedProbeHit> entries,
        string dedupeKey,
        string frameIndexPart,
        out CapturedProbeHit? hit,
        out CapturedStackFrame? frame,
        out string? error
    )
    {
        hit = null;
        frame = null;

        if (FindHit(entries, dedupeKey) is not { } capturedHit)
        {
            error = "Probe hit not found";
            return false;
        }

        if (
            !int.TryParse(frameIndexPart, out var frameIndex)
            || frameIndex < 0
            || frameIndex >= capturedHit.Frames.Count
        )
        {
            error = "Frame not found";
            return false;
        }

        hit = capturedHit;
        frame = capturedHit.Frames[frameIndex];
        error = null;
        return true;
    }
}
