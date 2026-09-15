namespace DebugAssistance.Probes;

// Global, flat store of captured probe hits across every probe — a hit's own
// TargetDeclaringTypeName/TargetMethodName fields identify which probed method it came from,
// there's no per-probe sub-store. Structured identically to Capture.CaptureStore (dictionary-by-
// dedupe-key + insertion-order list, same locking pattern), just typed for CapturedProbeHit.
internal sealed class ProbeHitStore(Func<int>? maxCapturedEntries = null)
{
    private readonly object _lock = new();
    private readonly List<CapturedProbeHit> _entries = [];
    private readonly Dictionary<string, CapturedProbeHit> _byKey = [];
    private readonly Func<int> _maxCapturedEntries =
        maxCapturedEntries ?? (() => DebugAssistanceMod.Settings.MaxCapturedEntries);

    // Increments the existing entry's count/last-seen on a dedupe-key match, otherwise inserts
    // `candidate` as a new entry (evicting the oldest entries once over the cap). Returns
    // whichever CapturedProbeHit the caller should keep referring to going forward.
    public CapturedProbeHit Add(CapturedProbeHit candidate)
    {
        lock (_lock)
        {
            if (_byKey.TryGetValue(candidate.DedupeKey, out var existing))
            {
                existing.RecordOccurrence(candidate.LastSeen);
                return existing;
            }

            _entries.Add(candidate);
            _byKey[candidate.DedupeKey] = candidate;
            EvictOverCap();

            return candidate;
        }
    }

    // Bumps an already-stored entry's occurrence count/last-seen without needing the caller to
    // rebuild a whole candidate CapturedProbeHit for it — ProbeManager's fast-path dedup hit uses
    // this to record a repeat call at effectively no cost. Returns whether a matching entry existed.
    public bool RecordOccurrence(string dedupeKey, DateTime timestamp)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue(dedupeKey, out var existing))
            {
                return false;
            }

            existing.RecordOccurrence(timestamp);
            return true;
        }
    }

    // Folds a loaded set into the current in-memory set: a dedupe-key match combines historical
    // data via CapturedProbeHit.MergeFrom (summing occurrence counts, not just recording one more
    // live occurrence), while a new key is inserted as-is.
    public void Merge(IEnumerable<CapturedProbeHit> loaded)
    {
        lock (_lock)
        {
            foreach (var candidate in loaded)
            {
                if (_byKey.TryGetValue(candidate.DedupeKey, out var existing))
                {
                    existing.MergeFrom(candidate);
                    continue;
                }

                _entries.Add(candidate);
                _byKey[candidate.DedupeKey] = candidate;
            }

            EvictOverCap();
        }
    }

    // Clears the current in-memory set before merging `loaded` in.
    public void Replace(IEnumerable<CapturedProbeHit> loaded)
    {
        lock (_lock)
        {
            _entries.Clear();
            _byKey.Clear();
            Merge(loaded);
        }
    }

    // A point-in-time copy, safe to iterate (e.g. for UI drawing or save export) without holding
    // the store's lock for the duration.
    public IReadOnlyList<CapturedProbeHit> Snapshot()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }

    // Removes the single entry matching dedupeKey, if present. Returns whether an entry was removed.
    public bool Remove(string dedupeKey)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue(dedupeKey, out var existing))
            {
                return false;
            }

            _ = _entries.Remove(existing);
            _ = _byKey.Remove(dedupeKey);
            return true;
        }
    }

    // Removes every captured entry. Returns how many entries were removed.
    public int Clear()
    {
        lock (_lock)
        {
            var count = _entries.Count;
            _entries.Clear();
            _byKey.Clear();
            return count;
        }
    }

    private void EvictOverCap()
    {
        while (_entries.Count > _maxCapturedEntries())
        {
            var oldest = _entries[0];
            _entries.RemoveAt(0);
            _ = _byKey.Remove(oldest.DedupeKey);
        }
    }
}
