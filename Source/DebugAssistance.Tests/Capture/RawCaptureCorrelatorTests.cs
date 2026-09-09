using DebugAssistance.Capture;
using RimTestRedux;

namespace DebugAssistance.Tests.Capture;

[TestSuite]
internal static class RawCaptureCorrelatorTests
{
    private static RawCapture MakeCapture(
        string errorTypeName,
        string message,
        DateTime? timestamp = null
    ) => new(errorTypeName, message, [], "", timestamp ?? DateTime.UtcNow);

    [Test]
    public static void MatchesWhenLogTextWrapsTheErrorWithAContextualPrefix()
    {
        var candidate = MakeCapture(
            "System.NullReferenceException",
            "Object reference not set to an instance of an object"
        );
        var logText =
            "Exception ticking Pawn Bob: System.NullReferenceException: "
            + "Object reference not set to an instance of an object\n  at Foo.Bar()";

        var match = RawCaptureCorrelator.FindMatch([candidate], logText);

        Assert.That(ReferenceEquals(match, candidate)).Is.True();
    }

    [Test]
    public static void MatchesWhenLogTextWrapsWithARootLevelPrefix()
    {
        var candidate = MakeCapture("System.InvalidOperationException", "Collection was modified");
        var logText =
            "Root level exception in Update(): System.InvalidOperationException: Collection was modified";

        var match = RawCaptureCorrelator.FindMatch([candidate], logText);

        Assert.That(ReferenceEquals(match, candidate)).Is.True();
    }

    [Test]
    public static void DoesNotMatchWhenTheErrorTypeIsAbsent()
    {
        var candidate = MakeCapture("System.NullReferenceException", "boom");
        var logText = "System.InvalidOperationException: boom";

        var match = RawCaptureCorrelator.FindMatch([candidate], logText);

        Assert.That(match is null).Is.True();
    }

    [Test]
    public static void DoesNotMatchWhenTheMessageIsAbsent()
    {
        var candidate = MakeCapture("System.NullReferenceException", "boom");
        var logText = "System.NullReferenceException: something else entirely";

        var match = RawCaptureCorrelator.FindMatch([candidate], logText);

        Assert.That(match is null).Is.True();
    }

    [Test]
    public static void PicksTheMostRecentCandidateWhenMultipleMatch()
    {
        var older = MakeCapture("System.Exception", "boom", new DateTime(2026, 1, 1));
        var newer = MakeCapture("System.Exception", "boom", new DateTime(2026, 1, 2));
        var logText = "System.Exception: boom";

        var match = RawCaptureCorrelator.FindMatch([older, newer], logText);

        Assert.That(ReferenceEquals(match, newer)).Is.True();
    }

    [Test]
    public static void ReturnsNullWhenNoCandidatesAreGiven()
    {
        var match = RawCaptureCorrelator.FindMatch([], "System.Exception: boom");

        Assert.That(match is null).Is.True();
    }

    [Test]
    public static void FallsBackToNoMatchWhenLogTextIsHarmonyModsCollapsedDuplicatePlaceholder()
    {
        var candidate = MakeCapture("System.NullReferenceException", "boom");
        var logText = "[Ref 1A2B3C4D] Duplicate stacktrace, see ref for original";

        var match = RawCaptureCorrelator.FindMatch([candidate], logText);

        // The type/message aren't present in a collapsed placeholder, so this correctly falls
        // back to "no correlation" — LogCaptureHook then falls back to text-only parsing, which
        // itself can't recover the lost frame detail; that's the residual risk this bug leaves.
        Assert.That(match is null).Is.True();
    }

    private static RawCapture MakeTextOnlyCapture(string rawText, DateTime? timestamp = null) =>
        new("", rawText, [], rawText, timestamp ?? DateTime.UtcNow);

    [Test]
    public static void FindTextMatchMatchesWhenLogTextContainsTheCapturesRawText()
    {
        var candidate = MakeTextOnlyCapture("  at Foo.Bar()\n  at Baz.Qux()");
        var logText = "XML patch failed to apply\n  at Foo.Bar()\n  at Baz.Qux()";

        var match = RawCaptureCorrelator.FindTextMatch([candidate], logText);

        Assert.That(ReferenceEquals(match, candidate)).Is.True();
    }

    [Test]
    public static void FindTextMatchDoesNotMatchWhenTheRawTextIsAbsent()
    {
        var candidate = MakeTextOnlyCapture("  at Foo.Bar()");
        var logText = "Something unrelated entirely";

        var match = RawCaptureCorrelator.FindTextMatch([candidate], logText);

        Assert.That(match is null).Is.True();
    }

    [Test]
    public static void FindTextMatchIgnoresCandidatesWithEmptyRawText()
    {
        var candidate = MakeTextOnlyCapture("");

        var match = RawCaptureCorrelator.FindTextMatch([candidate], "anything at all");

        Assert.That(match is null).Is.True();
    }

    [Test]
    public static void FindTextMatchPicksTheMostRecentCandidateWhenMultipleMatch()
    {
        var older = MakeTextOnlyCapture("  at Foo.Bar()", new DateTime(2026, 1, 1));
        var newer = MakeTextOnlyCapture("  at Foo.Bar()", new DateTime(2026, 1, 2));
        var logText = "  at Foo.Bar()";

        var match = RawCaptureCorrelator.FindTextMatch([older, newer], logText);

        Assert.That(ReferenceEquals(match, newer)).Is.True();
    }

    [Test]
    public static void FindTextMatchReturnsNullWhenNoCandidatesAreGiven()
    {
        var match = RawCaptureCorrelator.FindTextMatch([], "  at Foo.Bar()");

        Assert.That(match is null).Is.True();
    }
}
