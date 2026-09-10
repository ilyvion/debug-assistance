using System.Diagnostics;
using DebugAssistance.Capture;
using RimTestRedux;

namespace DebugAssistance.Tests.Capture;

[TestSuite]
internal static class LogCaptureHookTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowProbeException() =>
        throw new InvalidOperationException("LogCaptureHookTests probe exception");

    // Mirrors StackTraceCapturePatch.Prefix: builds a RawCapture from a real, live exception's
    // StackTrace, plus a log condition string RawCaptureCorrelator will match it against (it
    // matches by substring, mirroring how RimWorld wraps ex.ToString() in a contextual prefix
    // before it reaches the log).
    private static (RawCapture Capture, string ConditionText) MakeCorrelatedCapture()
    {
        Exception exception;
        StackTrace stackTrace;
        try
        {
            ThrowProbeException();
            throw new InvalidOperationException("unreachable");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
            stackTrace = new StackTrace(ex, fNeedFileInfo: true);
        }

        var rawText = stackTrace.ToString();
        var capture = new RawCapture(
            exception.GetType().FullName,
            exception.Message,
            stackTrace.GetFrames() ?? [],
            rawText,
            DateTime.UtcNow
        );
        var conditionText =
            $"Exception ticking thing: {capture.ErrorTypeName}: {capture.Message}\n{rawText}";
        return (capture, conditionText);
    }

    [Test]
    public static void UsesTheLiveStackTraceTextRatherThanPerFrameToStringForACorrelatedCapture()
    {
        var (capture, conditionText) = MakeCorrelatedCapture();
        var ringBuffer = new RawCaptureRingBuffer();
        ringBuffer.Store(capture);
        var store = new CaptureStore();

        LogCaptureHook.Handle(store, ringBuffer, conditionText, "", LogType.Exception);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        var result = snapshot[0];

        // capture.RawText is StackTrace.ToString() — what Environment.GetStackTrace(Exception,
        // bool) itself returns, matching the game's own log text — so the whole captured
        // exception's raw trace, and each frame's own raw text, must come from splitting that
        // string rather than reformatting via StackFrame.ToString(), which renders the
        // differently-styled "<method> at offset <n> in file:line:column <file>:<line>:<col>".
        Assert.That(result.RawStackTrace).Is.EqualTo(capture.RawText);

        var expectedLines = capture
            .RawText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => line.Length > 0)
            .ToArray();
        Assert.ThatCollection(result.Frames).Has.Count(expectedLines.Length);
        Assert.That(result.Frames[0].RawText).Is.EqualTo(expectedLines[0]);
        Assert
            .That(result.Frames[0].RawText.Contains(" at offset ", StringComparison.Ordinal))
            .Is.False();
    }

    [Test]
    public static void DoesNotCaptureWhenCaptureEnabledReturnsFalse()
    {
        var (capture, conditionText) = MakeCorrelatedCapture();
        var ringBuffer = new RawCaptureRingBuffer();
        ringBuffer.Store(capture);
        var store = new CaptureStore();

        LogCaptureHook.Handle(store, ringBuffer, conditionText, "", LogType.Exception, () => false);

        Assert.ThatCollection(store.Snapshot()).Has.Count(0);
    }

    [Test]
    public static void FallsBackToPerFrameToStringWhenFrameCountDoesNotMatchTheRawTextLineCount()
    {
        var (capture, conditionText) = MakeCorrelatedCapture();
        var mismatched = new RawCapture(
            capture.ErrorTypeName,
            capture.Message,
            capture.Frames,
            "a single unrelated line",
            DateTime.UtcNow
        );
        var ringBuffer = new RawCaptureRingBuffer();
        ringBuffer.Store(mismatched);
        var store = new CaptureStore();

        LogCaptureHook.Handle(store, ringBuffer, conditionText, "", LogType.Exception);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        var result = snapshot[0];

        Assert.ThatCollection(result.Frames).Has.Count(capture.Frames.Length);
        Assert.That(result.Frames[0].RawText).Is.EqualTo(capture.Frames[0].ToString());
    }

    [Test]
    public static void FallsBackToParsingTheConditionWhenNoRawCaptureCorrelatesForAnError()
    {
        var (capture, conditionText) = MakeCorrelatedCapture();
        var ringBuffer = new RawCaptureRingBuffer();
        var store = new CaptureStore();

        LogCaptureHook.Handle(store, ringBuffer, conditionText, "", LogType.Exception);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        var result = snapshot[0];

        Assert.That(result.ErrorTypeName).Is.EqualTo(capture.ErrorTypeName);
        Assert.ThatCollection(result.Frames).Has.Count(capture.Frames.Length);
    }

    // Mirrors the real Verse.Log.Error(string) flow: ExtractStackTraceCapturePatch hands its live
    // frame capture to LogCaptureHook.SetPendingFrameCapture just before LogMessageEnqueueCapturePatch
    // calls CaptureFromLogMessage for the same call — no correlation against any log text is
    // involved.
    private static (StackFrame[] Frames, string RawText) MakePendingFrameCapture()
    {
        StackTrace stackTrace;
        try
        {
            ThrowProbeException();
            throw new InvalidOperationException("unreachable");
        }
        catch (InvalidOperationException)
        {
            stackTrace = new StackTrace(0, fNeedFileInfo: true);
        }

        return (stackTrace.GetFrames() ?? [], stackTrace.ToString());
    }

    [Test]
    public static void CapturesAPlainLogErrorUsingThePendingLiveFrameCapture()
    {
        var (frames, rawText) = MakePendingFrameCapture();
        var store = new CaptureStore();
        var ringBuffer = new RawCaptureRingBuffer();

        LogCaptureHook.SetPendingFrameCapture(frames, rawText);
        LogCaptureHook.CaptureFromLogMessage(
            store,
            ringBuffer,
            "XML patch failed to apply (plain capture)"
        );

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        var result = snapshot[0];

        Assert.That(result.ErrorTypeName).Is.EqualTo("");
        Assert.That(result.Message).Is.EqualTo("XML patch failed to apply (plain capture)");
        Assert.That(result.RawStackTrace).Is.EqualTo(rawText);
        Assert.ThatCollection(result.Frames).Has.Count(frames.Length);
    }

    [Test]
    public static void PrefersFramesParsedFromAnEmbeddedErrorOverThePendingLiveCaptureWhenNothingCorrelates()
    {
        var (capture, _) = MakeCorrelatedCapture();
        var (frames, rawText) = MakePendingFrameCapture();
        var store = new CaptureStore();
        var ringBuffer = new RawCaptureRingBuffer();
        var text =
            $"Error doing thing: {capture.ErrorTypeName}: {capture.Message}\n{capture.RawText}";

        LogCaptureHook.SetPendingFrameCapture(frames, rawText);
        LogCaptureHook.CaptureFromLogMessage(store, ringBuffer, text);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        var result = snapshot[0];

        Assert.That(result.ErrorTypeName).Is.EqualTo(capture.ErrorTypeName);
        Assert.That(result.Message).Is.EqualTo(capture.Message);
        Assert.ThatCollection(result.Frames).Has.Count(capture.Frames.Length);
    }

    // Regression test: StackTraceCapturePatch captures a caught exception's pristine type/message/
    // live frames into the ring buffer the moment Log.Error($"...: {ex}") calls ex.ToString() to
    // build its text — this must be preferred over re-parsing that same text, since only the live
    // frames carry Harmony patch info (FrameModResolver.ResolveLiveFrame needs a real StackFrame)
    // and only the pristine message excludes whatever HarmonyMod annotates onto the rendered trace
    // (e.g. a "[Ref ...]" cache-reference tag).
    [Test]
    public static void PrefersARingBufferCorrelatedLiveCaptureOverParsingAnEmbeddedError()
    {
        var (capture, _) = MakeCorrelatedCapture();
        var store = new CaptureStore();
        var ringBuffer = new RawCaptureRingBuffer();
        ringBuffer.Store(capture);
        var text =
            $"Error doing thing: {capture.ErrorTypeName}: {capture.Message} [Ref 1A2B3C]\n{capture.RawText}";

        LogCaptureHook.CaptureFromLogMessage(store, ringBuffer, text);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        var result = snapshot[0];

        Assert.That(result.ErrorTypeName).Is.EqualTo(capture.ErrorTypeName);
        Assert.That(result.Message).Is.EqualTo(capture.Message);
        Assert.That(result.RawStackTrace).Is.EqualTo(capture.RawText);
        Assert.ThatCollection(result.Frames).Has.Count(capture.Frames.Length);
    }

    // Regression test: two occurrences of the same exception must dedupe into one entry even when
    // HarmonyMod collapses the second occurrence's rendered trace into a frame-free "[Ref ...]
    // Duplicate stacktrace, see ref for original" placeholder — StackTraceCapturePatch's own ring
    // buffer capture (taken ahead of HarmonyMod's patch on the same method) still has the real
    // trace for both, so the dedupe key still matches.
    [Test]
    public static void DedupesACollapsedDuplicateAgainstItsRingBufferCorrelatedOriginal()
    {
        var (capture, _) = MakeCorrelatedCapture();
        var store = new CaptureStore();
        var ringBuffer = new RawCaptureRingBuffer();
        ringBuffer.Store(capture);
        var firstText =
            $"Error doing thing: {capture.ErrorTypeName}: {capture.Message} [Ref 1A2B3C]\n{capture.RawText}";
        var secondText =
            $"Error doing thing: {capture.ErrorTypeName}: {capture.Message} [Ref 1A2B3C] Duplicate stacktrace, see ref for original";

        LogCaptureHook.CaptureFromLogMessage(store, ringBuffer, firstText);
        LogCaptureHook.CaptureFromLogMessage(store, ringBuffer, secondText);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        Assert.That(snapshot[0].OccurrenceCount).Is.EqualTo(2);
    }

    [Test]
    public static void CapturesAPlainLogErrorWithNoFramesWhenNothingIsPending()
    {
        var store = new CaptureStore();
        var ringBuffer = new RawCaptureRingBuffer();

        LogCaptureHook.CaptureFromLogMessage(
            store,
            ringBuffer,
            "XML patch failed to apply (no pending)"
        );

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        var result = snapshot[0];

        Assert.That(result.Message).Is.EqualTo("XML patch failed to apply (no pending)");
        Assert.ThatCollection(result.Frames).Has.Count(0);
    }

    [Test]
    public static void DoesNotCaptureAnEnqueuedMessageWhenCaptureEnabledReturnsFalse()
    {
        var store = new CaptureStore();
        var ringBuffer = new RawCaptureRingBuffer();

        LogCaptureHook.CaptureFromLogMessage(
            store,
            ringBuffer,
            "XML patch failed to apply (disabled)",
            () => false
        );

        Assert.ThatCollection(store.Snapshot()).Has.Count(0);
    }

    [Test]
    public static void HandleSkipsAnErrorAlreadyCapturedViaCaptureFromLogMessage()
    {
        var store = new CaptureStore();
        var ringBuffer = new RawCaptureRingBuffer();
        const string text = "XML patch failed to apply (enqueue then handle)";

        LogCaptureHook.CaptureFromLogMessage(store, ringBuffer, text);
        LogCaptureHook.Handle(store, ringBuffer, text, "", LogType.Error);

        // Debug.LogError(text) fires unconditionally right after LogMessageQueue.Enqueue for this
        // same call, which is what Handle observes here — it must not turn into a second entry.
        Assert.ThatCollection(store.Snapshot()).Has.Count(1);
    }

    [Test]
    public static void HandleStillCapturesAnUnenqueuedErrorWhenIgnoringUnityOnlyErrorsIsDisabled()
    {
        var store = new CaptureStore();
        var ringBuffer = new RawCaptureRingBuffer();

        LogCaptureHook.Handle(
            store,
            ringBuffer,
            "some other mod's raw Debug.LogError",
            "",
            LogType.Error,
            ignoreUnityOnlyErrors: () => false
        );

        Assert.ThatCollection(store.Snapshot()).Has.Count(1);
    }

    // An Error that never went through Verse.LogMessageQueue.Enqueue (so never got marked handled
    // above) never came from Verse.Log - either Unity itself logged it directly (e.g. its texture
    // compression warning) or a mod called UnityEngine.Debug.LogError instead of Verse.Log. Both
    // are filtered by default, since there's no legitimate reason for the latter.
    [Test]
    public static void IgnoresAnUnenqueuedErrorByDefault()
    {
        var store = new CaptureStore();
        var ringBuffer = new RawCaptureRingBuffer();

        LogCaptureHook.Handle(
            store,
            ringBuffer,
            "Texture '' has dimensions (58 x 58) which are not multiples of 4. Compress will not work.",
            "",
            LogType.Error
        );

        Assert.ThatCollection(store.Snapshot()).Has.Count(0);
    }
}
