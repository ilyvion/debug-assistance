namespace DebugAssistance.Capture;

internal sealed class CaptureStore(Func<int>? maxCapturedEntries = null)
{
    private readonly object _lock = new();
    private readonly List<CapturedError> _entries = [];
    private readonly Dictionary<string, CapturedError> _byKey = [];
    private readonly Func<int> _maxCapturedEntries =
        maxCapturedEntries ?? (() => DebugAssistanceMod.Settings.MaxCapturedEntries);

    // Increments the existing entry's count/last-seen on a dedupe-key match, otherwise inserts
    // `candidate` as a new entry (evicting the oldest entries once over the cap). Returns
    // whichever CapturedError the caller should keep referring to going forward.
    public CapturedError Add(CapturedError candidate)
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

    // Folds a loaded set into the current in-memory set: a dedupe-key match combines historical
    // data via CapturedError.MergeFrom (summing occurrence counts, not just recording one
    // more live occurrence — a loaded entry represents its own whole capture history, unlike
    // Add's candidate, which is always exactly one fresh occurrence), while a new key is inserted
    // as-is. Whole operation is atomic w.r.t. concurrent Add calls from the capture hook.
    public void Merge(IEnumerable<CapturedError> loaded)
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

    // Clears the current in-memory set before merging `loaded` in, per the confirmed load-flow
    // design (replace vs merge is the player's explicit choice, never a silent default).
    public void Replace(IEnumerable<CapturedError> loaded)
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
    public IReadOnlyList<CapturedError> Snapshot()
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
