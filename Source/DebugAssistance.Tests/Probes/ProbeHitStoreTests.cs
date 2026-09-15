using DebugAssistance.Probes;
using RimTestRedux;

namespace DebugAssistance.Tests.Probes;

[TestSuite]
internal static class ProbeHitStoreTests
{
    private static CapturedProbeHit MakeHit(string trace = "trace", DateTime? timestamp = null) =>
        new("Some.Type", "Method", "Some.Type.Method()", trace, [], timestamp ?? DateTime.UtcNow);

    [Test]
    public static void AddingANewEntryReturnsItInTheSnapshot()
    {
        var store = new ProbeHitStore();
        var hit = MakeHit();

        _ = store.Add(hit);

        Assert.ThatCollection(store.Snapshot()).Does.Contain(hit);
    }

    [Test]
    public static void AddingTheSameDedupeKeyTwiceKeepsOneEntryAndIncrementsCount()
    {
        var store = new ProbeHitStore();
        var first = MakeHit(timestamp: new DateTime(2026, 1, 1));
        var second = MakeHit(timestamp: new DateTime(2026, 1, 2));

        var firstResult = store.Add(first);
        var secondResult = store.Add(second);

        Assert.ThatCollection(store.Snapshot()).Has.Count(1);
        Assert.That(ReferenceEquals(firstResult, secondResult)).Is.True();
        Assert.That(firstResult.OccurrenceCount).Is.EqualTo(2);
        Assert.That(firstResult.LastSeen).Is.EqualTo(new DateTime(2026, 1, 2));
    }

    [Test]
    public static void DifferentDedupeKeysProduceSeparateEntries()
    {
        var store = new ProbeHitStore();
        _ = store.Add(MakeHit(trace: "trace one"));
        _ = store.Add(MakeHit(trace: "trace two"));

        Assert.ThatCollection(store.Snapshot()).Has.Count(2);
    }

    [Test]
    public static void RecordOccurrenceBumpsAnExistingEntryWithoutRebuildingIt()
    {
        var store = new ProbeHitStore();
        var hit = MakeHit(timestamp: new DateTime(2026, 1, 1));
        _ = store.Add(hit);

        var recorded = store.RecordOccurrence(hit.DedupeKey, new DateTime(2026, 1, 2));

        Assert.That(recorded).Is.True();
        Assert.That(hit.OccurrenceCount).Is.EqualTo(2);
        Assert.That(hit.LastSeen).Is.EqualTo(new DateTime(2026, 1, 2));
    }

    [Test]
    public static void RecordOccurrenceReturnsFalseForAnUnknownDedupeKey()
    {
        var store = new ProbeHitStore();

        var recorded = store.RecordOccurrence("no-such-key", DateTime.UtcNow);

        Assert.That(recorded).Is.False();
    }

    [Test]
    public static void FifoEvictionRemovesOldestEntryOnceOverTheCap()
    {
        var store = new ProbeHitStore(() => 2);
        var oldest = MakeHit(trace: "oldest");
        var middle = MakeHit(trace: "middle");
        var newest = MakeHit(trace: "newest");

        _ = store.Add(oldest);
        _ = store.Add(middle);
        _ = store.Add(newest);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(2);
        Assert.ThatCollection(snapshot).Does.Not.Contain(oldest);
        Assert.ThatCollection(snapshot).Does.Contain(middle);
        Assert.ThatCollection(snapshot).Does.Contain(newest);
    }

    [Test]
    public static void MergeOfAMatchingKeySumsOccurrenceCountsRatherThanJustRecordingOneMore()
    {
        var store = new ProbeHitStore();
        var existing = MakeHit(timestamp: new DateTime(2026, 1, 5));
        existing.RecordOccurrence(new DateTime(2026, 1, 6));
        _ = store.Add(existing);

        var loaded = MakeHit(timestamp: new DateTime(2026, 1, 1));
        loaded.RecordOccurrence(new DateTime(2026, 1, 10));

        store.Merge([loaded]);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        Assert.That(snapshot[0].OccurrenceCount).Is.EqualTo(4);
    }

    [Test]
    public static void ReplaceClearsExistingEntriesBeforeMerging()
    {
        var store = new ProbeHitStore();
        _ = store.Add(MakeHit(trace: "old"));

        store.Replace([MakeHit(trace: "new")]);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        Assert.That(snapshot[0].RawStackTrace).Is.EqualTo("new");
    }

    [Test]
    public static void RemoveDeletesTheMatchingEntryAndReturnsTrue()
    {
        var store = new ProbeHitStore();
        var toRemove = MakeHit(trace: "remove me");
        _ = store.Add(toRemove);
        _ = store.Add(MakeHit(trace: "keep me"));

        var removed = store.Remove(toRemove.DedupeKey);

        Assert.That(removed).Is.True();
        Assert.ThatCollection(store.Snapshot()).Has.Count(1);
    }

    [Test]
    public static void ClearRemovesEveryEntryAndReturnsHowManyWereRemoved()
    {
        var store = new ProbeHitStore();
        _ = store.Add(MakeHit(trace: "first"));
        _ = store.Add(MakeHit(trace: "second"));

        var clearedCount = store.Clear();

        Assert.That(clearedCount).Is.EqualTo(2);
        Assert.ThatCollection(store.Snapshot()).Has.Count(0);
    }
}
