using System.Diagnostics;

namespace DebugAssistance.Capture;

// Verse.Log.Error(string)/Warning/ErrorOnce/WarningOnce all call StackTraceUtility.ExtractStackTrace()
// unconditionally to build the game's own debug-log stack trace text; no live Exception is
// involved here, it just walks the current call stack at the call site. BetterStacktraces
// (Alexey.BetterStacktraces) is a soft/optional dependency that also prefixes this exact method at
// the same priority band and can return false, which — per Harmony's prefix-chain semantics —
// skips every remaining prefix once one does; HarmonyPriority.First plus HarmonyBefore guarantee
// this prefix still runs first regardless of whether that mod is installed. This patch builds its
// own independent StackTrace rather than ever calling ExtractStackTrace()/GetStackTrace() again, so
// it can never itself trip any mod's collapsing/caching layer on this method.
[HarmonyPatch(typeof(StackTraceUtility), "ExtractStackTrace")]
[HarmonyPriority(Priority.First)]
[HarmonyBefore("Alexey.BetterStacktraces")]
internal static class ExtractStackTraceCapturePatch
{
#pragma warning disable IDE0051 // Used by reflection
    private static void Prefix()
    {
        var stackTrace = new StackTrace(1, fNeedFileInfo: true);
        var frames = stackTrace.GetFrames();
        if (frames is null)
        {
            return;
        }

        var liveFrames = SkipLoggingChainFrames(frames);
        // stackTrace.ToString() — not frame.ToString() per frame — matches what
        // Environment.GetStackTrace(Exception, bool) itself produces (see RawCapture.RawText),
        // which is the format LogCaptureHook.BuildFramesFromLiveCapture pairs raw text lines
        // against by index; trimming the same number of leading lines here as frames skipped
        // above keeps that pairing intact instead of falling back to StackFrame.ToString()'s
        // differently-formatted text for every frame.
        var rawText = TrimLeadingFrameLines(
            stackTrace.ToString(),
            frames.Length - liveFrames.Length
        );

        // Handed off to LogMessageEnqueueCapturePatch, which runs moments later for this same
        // call — Verse.Log.Error/Warning/Message always call LogMessageQueue.Enqueue right after
        // building the trace this prefix just captured.
        LogCaptureHook.SetPendingFrameCapture(liveFrames, rawText);
    }
#pragma warning restore IDE0051

    internal static string TrimLeadingFrameLines(string fullTraceText, int frameCountToSkip)
    {
        if (frameCountToSkip == 0)
        {
            return fullTraceText;
        }

        var lines = fullTraceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join("\n", lines.Skip(frameCountToSkip));
    }

    // Verse.Log.Error/Warning call StackTraceUtility.ExtractStackTrace directly; ErrorOnce/WarningOnce
    // just forward to Error/Warning. This is the complete, fixed set of frames between the real
    // caller and this prefix's own capture point — every one of them can independently carry a
    // Harmony patch, which replaces its frame with a generated replacement method.
    // Harmony.GetOriginalMethodFromStackframe resolves such a frame back to the original method it
    // replaces (or hands back the frame's own method unchanged when it isn't a replacement at all),
    // which is what lets this recognize the fixed set below regardless of whichever mechanism the
    // running Harmony version happens to use to build its replacement methods internally — and, just
    // as importantly, lets a caller that is itself a Harmony-patched method resolve to its own
    // original method instead of being mistaken for one of these.
    private static readonly MethodBase[] LoggingChainMethods =
    [
        AccessTools.Method(typeof(StackTraceUtility), nameof(StackTraceUtility.ExtractStackTrace)),
        AccessTools.Method(typeof(Log), nameof(Log.Error), [typeof(string)]),
        AccessTools.Method(typeof(Log), nameof(Log.ErrorOnce), [typeof(string), typeof(int)]),
        AccessTools.Method(typeof(Log), nameof(Log.Warning), [typeof(string)]),
        AccessTools.Method(typeof(Log), nameof(Log.WarningOnce), [typeof(string), typeof(int)]),
    ];

    internal static StackFrame[] SkipLoggingChainFrames(StackFrame[] frames) =>
        SkipLoggingChainFrames(frames, LoggingChainMethods);

    internal static StackFrame[] SkipLoggingChainFrames(
        StackFrame[] frames,
        IReadOnlyCollection<MethodBase> loggingChainMethods
    )
    {
        var start = 0;
        while (
            start < frames.Length
            && loggingChainMethods.Contains(Harmony.GetOriginalMethodFromStackframe(frames[start]))
        )
        {
            start++;
        }
        return frames[start..];
    }
}
