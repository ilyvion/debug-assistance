using DebugAssistance.Capture;
using DebugAssistance.Probes;
using DebugAssistance.Web;
using RimTestRedux;

namespace DebugAssistance.Tests.Web;

[TestSuite]
internal static class ProbeRouteResolverTests
{
    private static CapturedProbeHit MakeHit(
        string dedupeKeySeed = "a",
        IReadOnlyList<CapturedStackFrame>? frames = null
    ) =>
        new(
            "Some.Type",
            $"Method{dedupeKeySeed}",
            "Some.Type.Method()",
            "trace",
            frames ?? [],
            DateTime.UtcNow
        );

    private static CapturedStackFrame MakeFrame() =>
        new("raw", declaringTypeName: null, methodName: null, fileName: null, lineNumber: null);

    [Test]
    public static void FindHitReturnsNullWhenNoEntryHasTheGivenDedupeKey()
    {
        var result = ProbeRouteResolver.FindHit([MakeHit()], "no-such-key");

        Assert.That(result is null).Is.True();
    }

    [Test]
    public static void FindHitReturnsTheMatchingEntry()
    {
        var hit = MakeHit();

        var result = ProbeRouteResolver.FindHit([hit], hit.DedupeKey);

        Assert.That(ReferenceEquals(result, hit)).Is.True();
    }

    [Test]
    public static void ResolveFrameFailsWithNotFoundForAnUnknownDedupeKey()
    {
        var success = ProbeRouteResolver.ResolveFrame(
            [MakeHit()],
            "no-such-key",
            "0",
            out var hit,
            out var frame,
            out var error
        );

        Assert.That(success).Is.False();
        Assert.That(hit is null).Is.True();
        Assert.That(frame is null).Is.True();
        Assert.That(error).Is.EqualTo("Probe hit not found");
    }

    [Test]
    public static void ResolveFrameFailsWithFrameNotFoundForAnOutOfRangeIndex()
    {
        var capturedHit = MakeHit(frames: [MakeFrame()]);

        var success = ProbeRouteResolver.ResolveFrame(
            [capturedHit],
            capturedHit.DedupeKey,
            "1",
            out _,
            out var frame,
            out var error
        );

        Assert.That(success).Is.False();
        Assert.That(frame is null).Is.True();
        Assert.That(error).Is.EqualTo("Frame not found");
    }

    [Test]
    public static void ResolveFrameReturnsTheFrameAtTheGivenIndex()
    {
        var first = MakeFrame();
        var second = MakeFrame();
        var capturedHit = MakeHit(frames: [first, second]);

        var success = ProbeRouteResolver.ResolveFrame(
            [capturedHit],
            capturedHit.DedupeKey,
            "1",
            out var hit,
            out var frame,
            out var error
        );

        Assert.That(success).Is.True();
        Assert.That(error is null).Is.True();
        Assert.That(ReferenceEquals(hit, capturedHit)).Is.True();
        Assert.That(ReferenceEquals(frame, second)).Is.True();
    }
}
