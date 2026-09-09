using System.Diagnostics;
using DebugAssistance.Capture;
using RimTestRedux;

namespace DebugAssistance.Tests.Capture;

[TestSuite]
internal static class ExtractStackTraceCapturePatchTests
{
    private static StackFrame[]? _capturedFrames;

    // Real Harmony patches on dedicated test methods, mirroring the production shape (Log.Error
    // calling ExtractStackTrace, itself calling into this prefix) closely enough that Harmony's own
    // HarmonySharedState genuinely tracks each as a replacement — the same mechanism
    // Harmony.GetOriginalMethodFromStackframe resolves through in production, rather than a
    // hand-built stand-in for what a trampoline frame looks like.
    static ExtractStackTraceCapturePatchTests()
    {
        var harmony = new Harmony("DebugAssistance.Tests.ExtractStackTraceCapturePatchTests");
        _ = harmony.Patch(
            AccessTools.Method(
                typeof(ExtractStackTraceCapturePatchTests),
                nameof(SimulatedExtractStackTrace)
            ),
            prefix: new HarmonyMethod(
                AccessTools.Method(
                    typeof(ExtractStackTraceCapturePatchTests),
                    nameof(CaptureFrames)
                )
            )
        );
        var noOpPrefix = new HarmonyMethod(
            AccessTools.Method(typeof(ExtractStackTraceCapturePatchTests), nameof(NoOpPrefix))
        );
        _ = harmony.Patch(
            AccessTools.Method(
                typeof(ExtractStackTraceCapturePatchTests),
                nameof(SimulatedLogError)
            ),
            prefix: noOpPrefix
        );
        _ = harmony.Patch(
            AccessTools.Method(
                typeof(ExtractStackTraceCapturePatchTests),
                nameof(SimulatedUnrelatedConsumerPatch)
            ),
            prefix: noOpPrefix
        );
    }

    private static readonly MethodBase[] SimulatedLoggingChainMethods =
    [
        AccessTools.Method(
            typeof(ExtractStackTraceCapturePatchTests),
            nameof(SimulatedExtractStackTrace)
        ),
        AccessTools.Method(typeof(ExtractStackTraceCapturePatchTests), nameof(SimulatedLogError)),
    ];

    private static void NoOpPrefix() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CaptureFrames() =>
        _capturedFrames = new StackTrace(1, fNeedFileInfo: true).GetFrames();

    // Stands in for StackTraceUtility.ExtractStackTrace: CaptureFrames runs as its Harmony prefix,
    // exactly like ExtractStackTraceCapturePatch.Prefix does for the real method.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SimulatedExtractStackTrace() { }

    // Stands in for Verse.Log.Error: calls the ExtractStackTrace stand-in directly, same as the real
    // method does.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SimulatedLogError() => SimulatedExtractStackTrace();

    // A caller that is itself Harmony-patched by some unrelated mod, not part of the logging chain.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SimulatedUnrelatedConsumerPatch() => SimulatedLogError();

    [Test]
    public static void SkipsALeadingExtractStackTraceReplacementFrameToFindTheRealCaller()
    {
        _capturedFrames = null;

        SimulatedExtractStackTrace();

        var frames = _capturedFrames!;
        // Sanity check that this test actually reproduces the bug's shape before asserting the fix:
        // the live frame for a patched method resolves back to it via Harmony's own API, not by its
        // own (generated) Name or DeclaringType.
        Assert
            .That(Harmony.GetOriginalMethodFromStackframe(frames[0])?.Name)
            .Is.EqualTo(nameof(SimulatedExtractStackTrace));

        var result = ExtractStackTraceCapturePatch.SkipLoggingChainFrames(
            frames,
            SimulatedLoggingChainMethods
        );

        Assert
            .That(result[0].GetMethod()?.Name)
            .Is.EqualTo(nameof(SkipsALeadingExtractStackTraceReplacementFrameToFindTheRealCaller));
    }

    [Test]
    public static void SkipsBothTheExtractStackTraceAndLogErrorReplacementFramesToFindTheRealCaller()
    {
        _capturedFrames = null;

        SimulatedLogError();

        var frames = _capturedFrames!;
        Assert
            .That(Harmony.GetOriginalMethodFromStackframe(frames[1])?.Name)
            .Is.EqualTo(nameof(SimulatedLogError));

        var result = ExtractStackTraceCapturePatch.SkipLoggingChainFrames(
            frames,
            SimulatedLoggingChainMethods
        );

        Assert
            .That(result[0].GetMethod()?.Name)
            .Is.EqualTo(
                nameof(SkipsBothTheExtractStackTraceAndLogErrorReplacementFramesToFindTheRealCaller)
            );
    }

    // The critical case: a caller that itself is a Harmony-patched method resolves, via
    // Harmony.GetOriginalMethodFromStackframe, to its own original method — which isn't in the known
    // logging chain — rather than being lumped in with the logging machinery just because its frame
    // is also a generated replacement. Skipping by that shape alone (as an earlier version of this
    // fix did) would walk right past it and blame whoever called *that* method instead.
    [Test]
    public static void StopsAtAPatchedCallerRatherThanSkippingPastItAsWellAsTheLoggingFrames()
    {
        _capturedFrames = null;

        SimulatedUnrelatedConsumerPatch();

        var frames = _capturedFrames!;

        var result = ExtractStackTraceCapturePatch.SkipLoggingChainFrames(
            frames,
            SimulatedLoggingChainMethods
        );

        Assert
            .That(Harmony.GetOriginalMethodFromStackframe(result[0])?.Name)
            .Is.EqualTo(nameof(SimulatedUnrelatedConsumerPatch));
    }

    [Test]
    public static void ReturnsAllFramesUnchangedWhenNoneAreTrampolineFrames()
    {
        CaptureFrames();
        var frames = _capturedFrames!;

        var result = ExtractStackTraceCapturePatch.SkipLoggingChainFrames(frames);

        Assert.ThatCollection(result).Has.Count(frames.Length);
    }

    [Test]
    public static void TrimLeadingFrameLinesReturnsTextUnchangedWhenNoFramesAreSkipped()
    {
        var result = ExtractStackTraceCapturePatch.TrimLeadingFrameLines(
            "line1\nline2\nline3\n",
            0
        );

        Assert.That(result).Is.EqualTo("line1\nline2\nline3\n");
    }

    [Test]
    public static void TrimLeadingFrameLinesDropsAsManyLeadingLinesAsFramesSkipped()
    {
        var result = ExtractStackTraceCapturePatch.TrimLeadingFrameLines(
            "line1\nline2\nline3\n",
            2
        );

        Assert.That(result).Is.EqualTo("line3\n");
    }

    [Test]
    public static void TrimLeadingFrameLinesNormalizesWindowsLineEndingsBeforeSkipping()
    {
        var result = ExtractStackTraceCapturePatch.TrimLeadingFrameLines(
            "line1\r\nline2\r\nline3\r\n",
            1
        );

        Assert.That(result).Is.EqualTo("line2\nline3\n");
    }
}
