using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class MethodSearchCacheTests
{
    private static BrowsedMethod DummyMethod() =>
        new(
            typeof(MethodSearchCacheTests).Assembly,
            typeof(MethodSearchCacheTests),
            AccessTools.Method(typeof(MethodSearchCacheTests), nameof(DummyMethod))
        );

    // List<BrowsedMethod> isn't IComparable, so Assert.That's value grammar can't compare it
    // directly -- these tests instead assert on the reference-equality/null checks themselves.
    private static bool IsSameList(List<BrowsedMethod>? actual, List<BrowsedMethod> expected) =>
        ReferenceEquals(actual, expected);

    [Test]
    public static void TryGetReturnsWhatSetJustStored()
    {
        MethodSearchCache.Clear();
        var key = $"key_{Guid.NewGuid():N}";
        var results = new List<BrowsedMethod> { DummyMethod() };

        MethodSearchCache.Set(key, results, ttlSeconds: 60, maxEntries: 20);

        Assert.That(IsSameList(MethodSearchCache.TryGet(key, ttlSeconds: 60), results)).Is.True();
    }

    [Test]
    public static void TryGetReturnsNullForAKeyThatWasNeverSet()
    {
        MethodSearchCache.Clear();

        Assert
            .That(MethodSearchCache.TryGet($"missing_{Guid.NewGuid():N}", ttlSeconds: 60) is null)
            .Is.True();
    }

    // Regression coverage for the address-issue request: searching "foo" then "foobar" then back
    // to "foo" should reuse the "foo" entry rather than treat it as evicted, as long as it's still
    // within the cache's size and never expired.
    [Test]
    public static void ReturningToAnEarlierStillCachedSearchReusesItsEntry()
    {
        MethodSearchCache.Clear();
        var fooResults = new List<BrowsedMethod> { DummyMethod() };
        var foobarResults = new List<BrowsedMethod> { DummyMethod() };

        MethodSearchCache.Set("foo", fooResults, ttlSeconds: 60, maxEntries: 20);
        MethodSearchCache.Set("foobar", foobarResults, ttlSeconds: 60, maxEntries: 20);

        Assert
            .That(IsSameList(MethodSearchCache.TryGet("foo", ttlSeconds: 60), fooResults))
            .Is.True();
    }

    // TTL expiry only decides when the background sweep (EvictExpiredEntries) is allowed to
    // reclaim an entry -- it isn't a per-lookup validity check. A lookup that lands between an
    // entry's expiry and the next sweep should still get the (still perfectly good) cached
    // result instead of being forced to re-search.
    [Test]
    public static void TryGetStillReturnsAnEntryWhoseTtlHasPassedButHasNotBeenSweptYet()
    {
        MethodSearchCache.Clear();
        var results = new List<BrowsedMethod> { DummyMethod() };
        MethodSearchCache.Set("key", results, ttlSeconds: 1, maxEntries: 20);

        Thread.Sleep(TimeSpan.FromSeconds(1.5));

        Assert.That(IsSameList(MethodSearchCache.TryGet("key", ttlSeconds: 60), results)).Is.True();
    }

    // TryGet is a "used", not "added", LRU: a hit refreshes ExpiresAt, so an entry that's
    // actively being paged through or revisited keeps getting a fresh TTL window instead of
    // expiring out from under repeated use.
    [Test]
    public static void TryGetRefreshesAnEntrysExpiryOnEachHit()
    {
        MethodSearchCache.Clear();
        var results = new List<BrowsedMethod> { DummyMethod() };
        MethodSearchCache.Set("key", results, ttlSeconds: 1, maxEntries: 20);

        Thread.Sleep(TimeSpan.FromSeconds(0.7));
        // Refresh with a much longer TTL before the original 1-second window would have expired.
        _ = MethodSearchCache.TryGet("key", ttlSeconds: 60);
        Thread.Sleep(TimeSpan.FromSeconds(0.7));
        MethodSearchCache.EvictExpiredEntries();

        Assert.That(IsSameList(MethodSearchCache.TryGet("key", ttlSeconds: 60), results)).Is.True();
    }

    // maxEntries limits the cache to the given number of distinct searches, evicting whichever was
    // least recently touched (by either Set or a hit through TryGet) first -- this is what keeps
    // typing out a filter one character at a time ("p", "pa", "paw", ...) from pinning every
    // intermediate search in the cache at once.
    // The eviction sweep must actively remove expired entries on its own, not merely refuse to
    // return them -- otherwise an abandoned search (typed, then never revisited) would sit in
    // memory indefinitely instead of being reclaimed.
    [Test]
    public static void EvictExpiredEntriesActivelyRemovesExpiredEntries()
    {
        MethodSearchCache.Clear();
        MethodSearchCache.Set("a", [DummyMethod()], ttlSeconds: 1, maxEntries: 20);

        Thread.Sleep(TimeSpan.FromSeconds(1.5));
        MethodSearchCache.EvictExpiredEntries();

        Assert.That(MethodSearchCache.Count).Is.EqualTo(0);
        Assert.That(MethodSearchCache.TryGet("a", ttlSeconds: 60) is null).Is.True();
    }

    [Test]
    public static void OverflowingMaxEntriesEvictsTheLeastRecentlyUsedEntry()
    {
        MethodSearchCache.Clear();
        var resultsA = new List<BrowsedMethod> { DummyMethod() };
        var resultsB = new List<BrowsedMethod> { DummyMethod() };
        var resultsC = new List<BrowsedMethod> { DummyMethod() };

        MethodSearchCache.Set("a", resultsA, ttlSeconds: 60, maxEntries: 2);
        MethodSearchCache.Set("b", resultsB, ttlSeconds: 60, maxEntries: 2);
        MethodSearchCache.Set("c", resultsC, ttlSeconds: 60, maxEntries: 2);

        Assert.That(MethodSearchCache.TryGet("a", ttlSeconds: 60) is null).Is.True();
        Assert.That(IsSameList(MethodSearchCache.TryGet("b", ttlSeconds: 60), resultsB)).Is.True();
        Assert.That(IsSameList(MethodSearchCache.TryGet("c", ttlSeconds: 60), resultsC)).Is.True();
    }

    [Test]
    public static void TouchingAnEntryThroughTryGetProtectsItFromEviction()
    {
        MethodSearchCache.Clear();
        var resultsA = new List<BrowsedMethod> { DummyMethod() };
        var resultsB = new List<BrowsedMethod> { DummyMethod() };
        var resultsC = new List<BrowsedMethod> { DummyMethod() };

        MethodSearchCache.Set("a", resultsA, ttlSeconds: 60, maxEntries: 2);
        MethodSearchCache.Set("b", resultsB, ttlSeconds: 60, maxEntries: 2);
        // Touch "a" so "b" becomes the least recently used entry instead.
        _ = MethodSearchCache.TryGet("a", ttlSeconds: 60);
        MethodSearchCache.Set("c", resultsC, ttlSeconds: 60, maxEntries: 2);

        Assert.That(IsSameList(MethodSearchCache.TryGet("a", ttlSeconds: 60), resultsA)).Is.True();
        Assert.That(MethodSearchCache.TryGet("b", ttlSeconds: 60) is null).Is.True();
    }

    [Test]
    public static void ClearRemovesEveryEntry()
    {
        MethodSearchCache.Clear();
        MethodSearchCache.Set("a", [DummyMethod()], ttlSeconds: 60, maxEntries: 20);
        MethodSearchCache.Set("b", [DummyMethod()], ttlSeconds: 60, maxEntries: 20);

        MethodSearchCache.Clear();

        Assert.That(MethodSearchCache.TryGet("a", ttlSeconds: 60) is null).Is.True();
        Assert.That(MethodSearchCache.TryGet("b", ttlSeconds: 60) is null).Is.True();
    }
}
