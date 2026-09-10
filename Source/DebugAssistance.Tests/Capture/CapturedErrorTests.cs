using DebugAssistance.Capture;
using RimTestRedux;

namespace DebugAssistance.Tests.Capture;

[TestSuite]
internal static class CapturedErrorTests
{
    [Test]
    public static void ComputeDedupeKeyIsDeterministicForIdenticalInputs() =>
        Assert
            .That(
                CapturedError.ComputeDedupeKey(
                    "System.NullReferenceException",
                    "Object reference not set",
                    "at Foo.Bar()"
                )
            )
            .Is.EqualTo(
                CapturedError.ComputeDedupeKey(
                    "System.NullReferenceException",
                    "Object reference not set",
                    "at Foo.Bar()"
                )
            );

    [Test]
    public static void ComputeDedupeKeyDiffersOnErrorTypeName() =>
        Assert
            .That(CapturedError.ComputeDedupeKey("System.NullReferenceException", "m", "t"))
            .Is.Not.EqualTo(
                CapturedError.ComputeDedupeKey("System.InvalidOperationException", "m", "t")
            );

    [Test]
    public static void ComputeDedupeKeyDiffersOnMessage() =>
        Assert
            .That(CapturedError.ComputeDedupeKey("System.Exception", "message one", "t"))
            .Is.Not.EqualTo(CapturedError.ComputeDedupeKey("System.Exception", "message two", "t"));

    [Test]
    public static void ComputeDedupeKeyDiffersOnRawStackTrace() =>
        Assert
            .That(CapturedError.ComputeDedupeKey("System.Exception", "m", "trace one"))
            .Is.Not.EqualTo(CapturedError.ComputeDedupeKey("System.Exception", "m", "trace two"));

    [Test]
    public static void ComputeDedupeKeyDiffersOnInnerCauses()
    {
        var causeOne = new CapturedExceptionCause(
            "System.NullReferenceException",
            "cause one",
            "at Foo.Bar()",
            []
        );
        var causeTwo = new CapturedExceptionCause(
            "System.InvalidOperationException",
            "cause two",
            "at Baz.Qux()",
            []
        );

        Assert
            .That(CapturedError.ComputeDedupeKey("System.Exception", "m", "t", [causeOne]))
            .Is.Not.EqualTo(
                CapturedError.ComputeDedupeKey("System.Exception", "m", "t", [causeTwo])
            );
        Assert
            .That(CapturedError.ComputeDedupeKey("System.Exception", "m", "t"))
            .Is.Not.EqualTo(
                CapturedError.ComputeDedupeKey("System.Exception", "m", "t", [causeOne])
            );
    }

    [Test]
    public static void ComputeDedupeKeyTreatsNullAndEmptyInnerCausesTheSame() =>
        Assert
            .That(CapturedError.ComputeDedupeKey("System.Exception", "m", "t", null))
            .Is.EqualTo(CapturedError.ComputeDedupeKey("System.Exception", "m", "t", []));

    [Test]
    public static void NewCapturedErrorHasNoInnerCausesByDefault()
    {
        var exception = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            DateTime.UtcNow
        );

        Assert.ThatCollection(exception.InnerCauses).Is.Empty();
    }

    [Test]
    public static void NewCapturedErrorStartsAtOccurrenceCountOneWithMatchingFirstAndLastSeen()
    {
        var timestamp = new DateTime(2026, 1, 1);
        var exception = new CapturedError("System.Exception", "message", "trace", [], timestamp);

        Assert.That(exception.OccurrenceCount).Is.EqualTo(1);
        Assert.That(exception.FirstSeen).Is.EqualTo(timestamp);
        Assert.That(exception.LastSeen).Is.EqualTo(timestamp);
    }

    [Test]
    public static void RecordOccurrenceIncrementsCountAndAdvancesLastSeen()
    {
        var first = new DateTime(2026, 1, 1);
        var second = new DateTime(2026, 1, 2);
        var exception = new CapturedError("System.Exception", "message", "trace", [], first);

        exception.RecordOccurrence(second);

        Assert.That(exception.OccurrenceCount).Is.EqualTo(2);
        Assert.That(exception.FirstSeen).Is.EqualTo(first);
        Assert.That(exception.LastSeen).Is.EqualTo(second);
    }

    [Test]
    public static void RecordOccurrenceNeverMovesLastSeenBackwards()
    {
        var later = new DateTime(2026, 1, 5);
        var earlier = new DateTime(2026, 1, 1);
        var exception = new CapturedError("System.Exception", "message", "trace", [], later);

        exception.RecordOccurrence(earlier);

        Assert.That(exception.OccurrenceCount).Is.EqualTo(2);
        Assert.That(exception.LastSeen).Is.EqualTo(later);
    }

    [Test]
    public static void MergeFromSumsOccurrenceCountsAndWidensTheFirstLastSeenRange()
    {
        var exception = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            new DateTime(2026, 1, 5)
        );
        exception.RecordOccurrence(new DateTime(2026, 1, 6));
        // exception: count 2, first 2026-01-05, last 2026-01-06

        var loaded = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            new DateTime(2026, 1, 1)
        );
        loaded.RecordOccurrence(new DateTime(2026, 1, 10));
        // loaded: count 2, first 2026-01-01, last 2026-01-10

        exception.MergeFrom(loaded);

        Assert.That(exception.OccurrenceCount).Is.EqualTo(4);
        Assert.That(exception.FirstSeen).Is.EqualTo(new DateTime(2026, 1, 1));
        Assert.That(exception.LastSeen).Is.EqualTo(new DateTime(2026, 1, 10));
    }

    [Test]
    public static void MergeFromFillsInAMissingHarmonyRefHashButNeverOverwritesAnExistingOne()
    {
        var withoutHash = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            DateTime.UtcNow
        );
        var withHash = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            DateTime.UtcNow
        )
        {
            HarmonyRefHash = 0x1234,
        };

        withoutHash.MergeFrom(withHash);
        Assert.That(withoutHash.HarmonyRefHash).Is.EqualTo(0x1234);

        var otherHash = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            DateTime.UtcNow
        )
        {
            HarmonyRefHash = 0x5678,
        };
        withHash.MergeFrom(otherHash);
        Assert.That(withHash.HarmonyRefHash).Is.EqualTo(0x1234);
    }
}
