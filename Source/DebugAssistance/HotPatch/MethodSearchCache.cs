namespace DebugAssistance.HotPatch;

// A short-lived LRU cache for MethodBrowser.Browse results, keyed by the exact search
// parameters that produced them (see HotPatchEndpoints.BuildSearchCacheKey). Paging through the
// same search's later results, or returning to an earlier search that's still cached (e.g.
// "foo" -> "foobar" -> "foo"), reuses the cached list instead of re-running the search. Entries
// expire Settings.SearchCacheTtlSeconds after their last use (TryGet refreshes it, so a search
// that's actively being paged through or revisited doesn't expire) and the cache never holds more
// than Settings.SearchCacheMaxEntries entries at once, evicting the least-recently-used entry first --
// both are read fresh from Settings on every Set, so a player changing either setting takes
// effect immediately without a restart. A recurring sweep (see SweepTimer) also actively removes
// expired entries in the background, rather than leaving them to be reclaimed only the next time
// they happen to be looked up.
internal static class MethodSearchCache
{
    private sealed record Entry(List<BrowsedMethod> Results, DateTime ExpiresAt);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    private static readonly object Lock = new();

    // Most-recently-used at the front, so a hit's touch and an overflow eviction are both O(1).
    private static readonly LinkedList<(string Key, Entry Entry)> Order = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, Entry Entry)>> Nodes =
    [];

#pragma warning disable IDE0052, CA1823 // value is never read, but the field must stay alive to keep the timer firing
    private static readonly Timer SweepTimer = new(
        _ => EvictExpiredEntries(),
        null,
        SweepInterval,
        SweepInterval
    );
#pragma warning restore IDE0052, CA1823

    internal static int Count
    {
        get
        {
            lock (Lock)
            {
                return Nodes.Count;
            }
        }
    }

    // A hit refreshes ExpiresAt from ttlSeconds: this is an LRU cache keyed on last use, not last
    // add, so a search that's still being paged through or returned to stays alive instead of
    // expiring out from under it. Any entry SweepTimer hasn't gotten to yet is still fair to
    // return, expired or not -- TTL expiry only decides when the sweep is allowed to reclaim an
    // entry, not when a lookup should start refusing to hand it back.
    internal static List<BrowsedMethod>? TryGet(string key, int ttlSeconds)
    {
        lock (Lock)
        {
            if (!Nodes.TryGetValue(key, out var node))
            {
                return null;
            }

            node.Value = (
                node.Value.Key,
                node.Value.Entry with
                {
                    ExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(1, ttlSeconds)),
                }
            );
            Order.Remove(node);
            Order.AddFirst(node);
            return node.Value.Entry.Results;
        }
    }

    internal static void Set(
        string key,
        List<BrowsedMethod> results,
        int ttlSeconds,
        int maxEntries
    )
    {
        lock (Lock)
        {
            if (Nodes.TryGetValue(key, out var existing))
            {
                RemoveNode(existing);
            }

            var node = Order.AddFirst(
                (key, new Entry(results, DateTime.UtcNow.AddSeconds(Math.Max(1, ttlSeconds))))
            );
            Nodes[key] = node;

            while (Nodes.Count > Math.Max(1, maxEntries))
            {
                RemoveNode(Order.Last);
            }
        }
    }

    // Called after a successful assembly (re)load: a reload can add, remove, or change the
    // methods a previously cached search would have found, both for a search scoped to that path
    // and for a path-less search across every loaded assembly.
    internal static void Clear()
    {
        lock (Lock)
        {
            Order.Clear();
            Nodes.Clear();
        }
    }

    // Removes every already-expired entry regardless of whether it's been looked up since
    // expiring. Runs on SweepTimer's recurring schedule; also called directly by tests to force
    // an out-of-band sweep without waiting on the timer.
    internal static void EvictExpiredEntries()
    {
        lock (Lock)
        {
            var now = DateTime.UtcNow;
            var node = Order.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Entry.ExpiresAt <= now)
                {
                    RemoveNode(node);
                }
                node = next;
            }
        }
    }

    private static void RemoveNode(LinkedListNode<(string Key, Entry Entry)> node)
    {
        Order.Remove(node);
        _ = Nodes.Remove(node.Value.Key);
    }
}
