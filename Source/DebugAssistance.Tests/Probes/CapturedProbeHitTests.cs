using DebugAssistance.Probes;
using RimTestRedux;

namespace DebugAssistance.Tests.Probes;

[TestSuite]
internal static class CapturedProbeHitTests
{
    [Test]
    public static void ComputeDedupeKeyIsDeterministicForIdenticalInputs() =>
        Assert
            .That(CapturedProbeHit.ComputeDedupeKey("Some.Type", "Method", "at Foo.Bar()"))
            .Is.EqualTo(CapturedProbeHit.ComputeDedupeKey("Some.Type", "Method", "at Foo.Bar()"));

    [Test]
    public static void ComputeDedupeKeyDiffersOnTargetDeclaringTypeName() =>
        Assert
            .That(CapturedProbeHit.ComputeDedupeKey("Type.One", "Method", "t"))
            .Is.Not.EqualTo(CapturedProbeHit.ComputeDedupeKey("Type.Two", "Method", "t"));

    [Test]
    public static void ComputeDedupeKeyDiffersOnTargetMethodName() =>
        Assert
            .That(CapturedProbeHit.ComputeDedupeKey("Type", "MethodOne", "t"))
            .Is.Not.EqualTo(CapturedProbeHit.ComputeDedupeKey("Type", "MethodTwo", "t"));

    [Test]
    public static void ComputeDedupeKeyDiffersOnRawStackTrace() =>
        Assert
            .That(CapturedProbeHit.ComputeDedupeKey("Type", "Method", "trace one"))
            .Is.Not.EqualTo(CapturedProbeHit.ComputeDedupeKey("Type", "Method", "trace two"));

    [Test]
    public static void NewCapturedProbeHitStartsAtOccurrenceCountOneWithMatchingFirstAndLastSeen()
    {
        var timestamp = new DateTime(2026, 1, 1);
        var hit = new CapturedProbeHit("Type", "Method", "Type.Method()", "trace", [], timestamp);

        Assert.That(hit.OccurrenceCount).Is.EqualTo(1);
        Assert.That(hit.FirstSeen).Is.EqualTo(timestamp);
        Assert.That(hit.LastSeen).Is.EqualTo(timestamp);
    }

    [Test]
    public static void RecordOccurrenceIncrementsCountAndAdvancesLastSeen()
    {
        var first = new DateTime(2026, 1, 1);
        var second = new DateTime(2026, 1, 2);
        var hit = new CapturedProbeHit("Type", "Method", "Type.Method()", "trace", [], first);

        hit.RecordOccurrence(second);

        Assert.That(hit.OccurrenceCount).Is.EqualTo(2);
        Assert.That(hit.FirstSeen).Is.EqualTo(first);
        Assert.That(hit.LastSeen).Is.EqualTo(second);
    }

    [Test]
    public static void MergeFromSumsOccurrenceCountsAndWidensTheFirstLastSeenRange()
    {
        var hit = new CapturedProbeHit(
            "Type",
            "Method",
            "Type.Method()",
            "trace",
            [],
            new DateTime(2026, 1, 5)
        );
        hit.RecordOccurrence(new DateTime(2026, 1, 6));

        var loaded = new CapturedProbeHit(
            "Type",
            "Method",
            "Type.Method()",
            "trace",
            [],
            new DateTime(2026, 1, 1)
        );
        loaded.RecordOccurrence(new DateTime(2026, 1, 10));

        hit.MergeFrom(loaded);

        Assert.That(hit.OccurrenceCount).Is.EqualTo(4);
        Assert.That(hit.FirstSeen).Is.EqualTo(new DateTime(2026, 1, 1));
        Assert.That(hit.LastSeen).Is.EqualTo(new DateTime(2026, 1, 10));
    }
}
