using DebugAssistance.Capture;
using DebugAssistance.Web;
using RimTestRedux;

namespace DebugAssistance.Tests.Web;

[TestSuite]
internal static class ErrorRouteResolverTests
{
    private static CapturedError MakeError(
        string dedupeKeySeed = "a",
        IReadOnlyList<CapturedStackFrame>? frames = null
    ) => new($"System.Exception{dedupeKeySeed}", "message", "trace", frames ?? [], DateTime.UtcNow);

    private static CapturedStackFrame MakeFrame() =>
        new("raw", declaringTypeName: null, methodName: null, fileName: null, lineNumber: null);

    [Test]
    public static void FindErrorReturnsNullWhenNoEntryHasTheGivenDedupeKey()
    {
        var result = ErrorRouteResolver.FindError([MakeError()], "no-such-key");

        Assert.That(result is null).Is.True();
    }

    [Test]
    public static void FindErrorReturnsTheMatchingEntry()
    {
        var capturedError = MakeError();

        var result = ErrorRouteResolver.FindError([capturedError], capturedError.DedupeKey);

        Assert.That(ReferenceEquals(result, capturedError)).Is.True();
    }

    [Test]
    public static void ResolveFrameFailsWithErrorNotFoundForAnUnknownDedupeKey()
    {
        var success = ErrorRouteResolver.ResolveFrame(
            [MakeError()],
            "no-such-key",
            "0",
            out var frame,
            out var error
        );

        Assert.That(success).Is.False();
        Assert.That(frame is null).Is.True();
        Assert.That(error).Is.EqualTo("Error not found");
    }

    [Test]
    public static void ResolveFrameFailsWithFrameNotFoundForANonNumericIndex()
    {
        var capturedError = MakeError(frames: [MakeFrame()]);

        var success = ErrorRouteResolver.ResolveFrame(
            [capturedError],
            capturedError.DedupeKey,
            "not-a-number",
            out var frame,
            out var error
        );

        Assert.That(success).Is.False();
        Assert.That(frame is null).Is.True();
        Assert.That(error).Is.EqualTo("Frame not found");
    }

    [Test]
    public static void ResolveFrameFailsWithFrameNotFoundForAnOutOfRangeIndex()
    {
        var capturedError = MakeError(frames: [MakeFrame()]);

        var success = ErrorRouteResolver.ResolveFrame(
            [capturedError],
            capturedError.DedupeKey,
            "1",
            out var frame,
            out var error
        );

        Assert.That(success).Is.False();
        Assert.That(frame is null).Is.True();
        Assert.That(error).Is.EqualTo("Frame not found");
    }

    [Test]
    public static void ResolveFrameFailsWithFrameNotFoundForANegativeIndex()
    {
        var capturedError = MakeError(frames: [MakeFrame()]);

        var success = ErrorRouteResolver.ResolveFrame(
            [capturedError],
            capturedError.DedupeKey,
            "-1",
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
        var capturedError = MakeError(frames: [first, second]);

        var success = ErrorRouteResolver.ResolveFrame(
            [capturedError],
            capturedError.DedupeKey,
            "1",
            out var frame,
            out var error
        );

        Assert.That(success).Is.True();
        Assert.That(error is null).Is.True();
        Assert.That(ReferenceEquals(frame, second)).Is.True();
    }
}
