using System.Diagnostics;

namespace DebugAssistance.Capture;

// Subscribes to Application.logMessageReceivedThreaded (not the main-thread-only
// logMessageReceived — exceptions can be logged from any thread) for log entries that never pass
// through Verse.LogMessageQueue.Enqueue — genuine exceptions and asserts. A Verse.Log.Error(string)
// call is captured directly at Enqueue time instead (see CaptureFromLogMessage), since other mods
// can rewrite log text between Enqueue and whatever eventually reaches this event, making that text
// unreliable to correlate against afterwards.
internal static class LogCaptureHook
{
    private static readonly object PendingLock = new();
    private static (StackFrame[] Frames, string RawText)? _pendingFrameCapture;
    private static string? _enqueueCapturedText;

    internal static void Initialize(
        CaptureStore store,
        RawCaptureRingBuffer ringBuffer,
        Func<bool>? captureEnabled = null,
        Func<bool>? ignoreUnityOnlyErrors = null
    )
    {
        var enabled = captureEnabled ?? (() => DebugAssistanceMod.Settings.ErrorCaptureEnabled);
        var ignoreUnityOnly =
            ignoreUnityOnlyErrors ?? (() => DebugAssistanceMod.Settings.IgnoreUnityOnlyErrors);
        Application.logMessageReceivedThreaded += (condition, stackTrace, type) =>
            Handle(store, ringBuffer, condition, stackTrace, type, enabled, ignoreUnityOnly);
    }

    // ExtractStackTraceCapturePatch hands its live frame capture off here; CaptureFromLogMessage
    // consumes it moments later for the very same call, since Verse.Log.Error/Warning/Message
    // always call LogMessageQueue.Enqueue right after building their trace.
    internal static void SetPendingFrameCapture(StackFrame[] frames, string rawText)
    {
        lock (PendingLock)
        {
            _pendingFrameCapture = (frames, rawText);
        }
    }

    private static (StackFrame[] Frames, string RawText)? TakePendingFrameCapture()
    {
        lock (PendingLock)
        {
            var pending = _pendingFrameCapture;
            _pendingFrameCapture = null;
            return pending;
        }
    }

    private static void MarkHandledByEnqueue(string text)
    {
        lock (PendingLock)
        {
            _enqueueCapturedText = text;
        }
    }

    // Handle checks this so it doesn't re-capture the same entry a second time once
    // Debug.LogError(text) — this call's own eventual arrival at Application.logMessageReceivedThreaded
    // — fires for it.
    private static bool TryConsumeHandledByEnqueue(string condition)
    {
        lock (PendingLock)
        {
            if (_enqueueCapturedText == condition)
            {
                _enqueueCapturedText = null;
                return true;
            }
        }
        return false;
    }

    // LogMessageEnqueueCapturePatch's entry point. When the logged text embeds a live exception
    // (e.g. a mod logging a caught exception via Log.Error($"...: {ex}")), StackTraceCapturePatch
    // already captured that exception's pristine type/message/frames into the ring buffer the
    // moment its ToString() ran to build this very text — the same correlation Handle() does for
    // exceptions reaching Application.logMessageReceivedThreaded directly. Preferring that
    // pristine capture (over re-parsing the logged text) is what keeps per-frame Harmony patch
    // info intact and keeps repeat occurrences — including ones HarmonyMod has collapsed into a
    // frame-free "[Ref ...] Duplicate stacktrace" placeholder — deduping against the original
    // instead of parsing HarmonyMod's ref annotation into the message and creating a new entry.
    // Only once no such live capture is found does this fall back to parsing the logged text
    // itself, and finally to ExtractStackTraceCapturePatch's live call-site capture (see
    // TakePendingFrameCapture) for a plain Log.Error(text) call with no exception involved at all.
    internal static void CaptureFromLogMessage(
        CaptureStore store,
        RawCaptureRingBuffer ringBuffer,
        string text,
        Func<bool>? captureEnabled = null
    )
    {
        var pending = TakePendingFrameCapture();
        if (captureEnabled?.Invoke() == false)
        {
            return;
        }

        var timestamp = DateTime.UtcNow;
        var correlated = RawCaptureCorrelator.FindMatch(ringBuffer.Snapshot(), text);

        CapturedError captured;
        if (correlated is not null)
        {
            captured = BuildFromRawCapture(correlated, timestamp);
        }
        else
        {
            var parsed = LogTraceParser.Parse(text);
            List<CapturedStackFrame> frames;
            string rawStackTrace;
            if (parsed.Frames.Count > 0)
            {
                frames = [.. parsed.Frames.Select(BuildFrameFromParsed)];
                rawStackTrace = text;
            }
            else if (pending is { } p)
            {
                frames = BuildFramesFromLiveCapture(p.Frames, p.RawText);
                rawStackTrace = p.RawText;
            }
            else
            {
                frames = [];
                rawStackTrace = text;
            }

            captured = new CapturedError(
                parsed.ErrorTypeName,
                parsed.Message,
                rawStackTrace,
                frames,
                timestamp
            );
        }

        captured.HarmonyRefHash = LogTraceParser.ParseHarmonyRefHash(text);

        _ = store.Add(captured);
        MarkHandledByEnqueue(text);
    }

    internal static void Handle(
        CaptureStore store,
        RawCaptureRingBuffer ringBuffer,
        string condition,
        string stackTrace,
        LogType type,
        Func<bool>? captureEnabled = null,
        Func<bool>? ignoreUnityOnlyErrors = null
    )
    {
        if (type is not (LogType.Error or LogType.Exception or LogType.Assert))
        {
            return;
        }

        if (type == LogType.Error && TryConsumeHandledByEnqueue(condition))
        {
            return;
        }

        // A LogType.Error reaching here was never routed through Verse.LogMessageQueue.Enqueue
        // (RimWorld's Log.Error/Warning/Message pipeline always hits Enqueue, which marks its text
        // consumed above) - so it's either a Unity-engine-internal message (e.g. an asset import
        // warning) or a mod calling UnityEngine.Debug.LogError directly instead of Verse.Log.
        if (type == LogType.Error && ignoreUnityOnlyErrors?.Invoke() != false)
        {
            return;
        }

        if (captureEnabled?.Invoke() == false)
        {
            return;
        }

        var timestamp = DateTime.UtcNow;
        var snapshot = ringBuffer.Snapshot();
        var correlated =
            RawCaptureCorrelator.FindMatch(snapshot, condition)
            ?? RawCaptureCorrelator.FindTextMatch(snapshot, stackTrace);
        var captured = correlated is not null
            ? BuildFromRawCapture(correlated, timestamp)
            : BuildFromLogText(
                stackTrace.Length == 0 ? condition : condition + "\n" + stackTrace,
                timestamp
            );
        captured.HarmonyRefHash = LogTraceParser.ParseHarmonyRefHash(condition);

        _ = store.Add(captured);
    }

    private static CapturedError BuildFromRawCapture(RawCapture raw, DateTime timestamp) =>
        new(
            raw.ErrorTypeName,
            raw.Message,
            raw.RawText,
            BuildFramesFromLiveCapture(raw.Frames, raw.RawText),
            timestamp
        );

    // raw.RawText is StackTrace.ToString(), which renders one line per frame in the same order as
    // the frames themselves — pairing them by index gives each frame the exact text
    // Environment.GetStackTrace(Exception, bool) itself would have produced. A count mismatch
    // (frames filtered out via [StackTraceHidden], say) falls back to StackFrame.ToString()'s
    // differently-formatted text rather than pairing the wrong line to the wrong frame.
    private static List<CapturedStackFrame> BuildFramesFromLiveCapture(
        StackFrame[] frames,
        string rawText
    )
    {
        var rawLines = SplitFrameLines(rawText, frames.Length);
        return [.. frames.Select((frame, i) => BuildFrameFromLive(frame, rawLines?[i]))];
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

    private static CapturedStackFrame BuildFrameFromLive(StackFrame stackFrame, string? rawText)
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

    private static CapturedError BuildFromLogText(string logText, DateTime timestamp)
    {
        var parsed = LogTraceParser.Parse(logText);
        return new CapturedError(
            parsed.ErrorTypeName,
            parsed.Message,
            logText,
            [.. parsed.Frames.Select(BuildFrameFromParsed)],
            timestamp
        );
    }

    private static CapturedStackFrame BuildFrameFromParsed(ParsedFrame parsedFrame)
    {
        var frame = new CapturedStackFrame(
            parsedFrame.RawText,
            parsedFrame.DeclaringTypeName,
            parsedFrame.MethodName,
            parsedFrame.FileName,
            parsedFrame.LineNumber,
            ilOffset: parsedFrame.IlOffset
        );
        FrameModResolver.ResolveParsedFrame(frame);
        return frame;
    }
}
