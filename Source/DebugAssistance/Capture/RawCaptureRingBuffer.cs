namespace DebugAssistance.Capture;

// Bridges the short gap between "exception thrown" (StackTraceCapturePatch) and "exception
// actually logged" (LogCaptureHook) — this is not the permanent store, that's CaptureStore.
// Bounded FIFO, keyed by (exception type, message); storing an already-present key overwrites its
// value in place without changing its eviction order (last-write-wins).
internal sealed class RawCaptureRingBuffer(
    int capacity = Constants.DefaultRawCaptureRingBufferCapacity
)
{
    private readonly object _lock = new();
    private readonly List<RawCaptureKey> _order = [];
    private readonly Dictionary<RawCaptureKey, RawCapture> _byKey = [];

    public void Store(RawCapture capture)
    {
        var key = new RawCaptureKey(capture.ErrorTypeName, capture.Message);
        lock (_lock)
        {
            if (!_byKey.ContainsKey(key))
            {
                _order.Add(key);
            }
            _byKey[key] = capture;

            while (_order.Count > capacity)
            {
                var oldest = _order[0];
                _order.RemoveAt(0);
                _ = _byKey.Remove(oldest);
            }
        }
    }

    // A point-in-time copy, safe to iterate (e.g. for correlation) without holding the buffer's
    // lock for the duration.
    public IReadOnlyList<RawCapture> Snapshot()
    {
        lock (_lock)
        {
            return [.. _byKey.Values];
        }
    }
}
