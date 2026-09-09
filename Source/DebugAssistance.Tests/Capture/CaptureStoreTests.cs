using DebugAssistance.Capture;
using RimTestRedux;

namespace DebugAssistance.Tests.Capture;

[TestSuite]
internal static class CaptureStoreTests
{
    private static CapturedError MakeError(
        string message = "message",
        DateTime? timestamp = null
    ) => new("System.Exception", message, "trace", [], timestamp ?? DateTime.UtcNow);

    [Test]
    public static void AddingANewEntryReturnsItInTheSnapshot()
    {
        var store = new CaptureStore();
        var error = MakeError();

        _ = store.Add(error);

        Assert.ThatCollection(store.Snapshot()).Does.Contain(error);
    }

    [Test]
    public static void AddingTheSameDedupeKeyTwiceKeepsOneEntryAndIncrementsCount()
    {
        var store = new CaptureStore();
        var first = MakeError(timestamp: new DateTime(2026, 1, 1));
        var second = MakeError(timestamp: new DateTime(2026, 1, 2));

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
        var store = new CaptureStore();
        _ = store.Add(MakeError(message: "first"));
        _ = store.Add(MakeError(message: "second"));

        Assert.ThatCollection(store.Snapshot()).Has.Count(2);
    }

    [Test]
    public static void FifoEvictionRemovesOldestEntryOnceOverTheCap()
    {
        var store = new CaptureStore(() => 2);
        var oldest = MakeError(message: "oldest");
        var middle = MakeError(message: "middle");
        var newest = MakeError(message: "newest");

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
    public static void SnapshotReturnsAnIndependentCopy()
    {
        var store = new CaptureStore();
        _ = store.Add(MakeError());
        var snapshot = store.Snapshot();

        _ = store.Add(MakeError(message: "second"));

        Assert.ThatCollection(snapshot).Has.Count(1);
    }

    [Test]
    public static void ConcurrentAddsForTheSameKeyAllRecordAsOccurrencesOfOneEntry()
    {
        var store = new CaptureStore();
        const int concurrentAdds = 50;

        var tasks = Enumerable
            .Range(0, concurrentAdds)
            .Select(_ => Task.Run(() => store.Add(MakeError())))
            .ToArray();
        Task.WaitAll(tasks);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        Assert.That(snapshot[0].OccurrenceCount).Is.EqualTo(concurrentAdds);
    }

    [Test]
    public static void ConcurrentAddsForDifferentKeysAllSurvive()
    {
        var store = new CaptureStore();
        const int concurrentAdds = 50;

        var tasks = Enumerable
            .Range(0, concurrentAdds)
            .Select(i => Task.Run(() => store.Add(MakeError(message: $"message-{i}"))))
            .ToArray();
        Task.WaitAll(tasks);

        Assert.ThatCollection(store.Snapshot()).Has.Count(concurrentAdds);
    }

    [Test]
    public static void MergeOfANonMatchingKeyInsertsItAsANewEntry()
    {
        var store = new CaptureStore();
        _ = store.Add(MakeError(message: "already present"));

        store.Merge([MakeError(message: "loaded")]);

        Assert.ThatCollection(store.Snapshot()).Has.Count(2);
    }

    [Test]
    public static void MergeOfAMatchingKeySumsOccurrenceCountsRatherThanJustRecordingOneMore()
    {
        var store = new CaptureStore();
        var existing = MakeError(timestamp: new DateTime(2026, 1, 5));
        existing.RecordOccurrence(new DateTime(2026, 1, 6));
        _ = store.Add(existing);

        var loaded = MakeError(timestamp: new DateTime(2026, 1, 1));
        loaded.RecordOccurrence(new DateTime(2026, 1, 10));

        store.Merge([loaded]);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        Assert.That(snapshot[0].OccurrenceCount).Is.EqualTo(4);
        Assert.That(snapshot[0].FirstSeen).Is.EqualTo(new DateTime(2026, 1, 1));
        Assert.That(snapshot[0].LastSeen).Is.EqualTo(new DateTime(2026, 1, 10));
    }

    [Test]
    public static void MergeStillEnforcesTheCapAcrossTheWholeBatch()
    {
        var store = new CaptureStore(() => 2);
        _ = store.Add(MakeError(message: "pre-existing"));

        store.Merge([MakeError(message: "loaded-1"), MakeError(message: "loaded-2")]);

        Assert.ThatCollection(store.Snapshot()).Has.Count(2);
    }

    [Test]
    public static void ReplaceClearsExistingEntriesBeforeMerging()
    {
        var store = new CaptureStore();
        _ = store.Add(MakeError(message: "old"));

        store.Replace([MakeError(message: "new")]);

        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        Assert.That(snapshot[0].Message).Is.EqualTo("new");
    }

    [Test]
    public static void RemoveDeletesTheMatchingEntryAndReturnsTrue()
    {
        var store = new CaptureStore();
        var toRemove = MakeError(message: "remove me");
        _ = store.Add(toRemove);
        _ = store.Add(MakeError(message: "keep me"));

        var removed = store.Remove(toRemove.DedupeKey);

        Assert.That(removed).Is.True();
        var snapshot = store.Snapshot();
        Assert.ThatCollection(snapshot).Has.Count(1);
        Assert.ThatCollection(snapshot).Does.Not.Contain(toRemove);
    }

    [Test]
    public static void RemoveReturnsFalseForAnUnknownDedupeKey()
    {
        var store = new CaptureStore();
        _ = store.Add(MakeError());

        var removed = store.Remove("no-such-key");

        Assert.That(removed).Is.False();
        Assert.ThatCollection(store.Snapshot()).Has.Count(1);
    }

    [Test]
    public static void ClearRemovesEveryEntryAndReturnsHowManyWereRemoved()
    {
        var store = new CaptureStore();
        _ = store.Add(MakeError(message: "first"));
        _ = store.Add(MakeError(message: "second"));

        var clearedCount = store.Clear();

        Assert.That(clearedCount).Is.EqualTo(2);
        Assert.ThatCollection(store.Snapshot()).Has.Count(0);
    }

    [Test]
    public static void ClearOnAnEmptyStoreReturnsZero()
    {
        var store = new CaptureStore();

        var clearedCount = store.Clear();

        Assert.That(clearedCount).Is.EqualTo(0);
    }
}
