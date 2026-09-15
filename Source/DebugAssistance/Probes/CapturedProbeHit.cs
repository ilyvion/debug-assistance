using System.Security.Cryptography;
using System.Text;
using DebugAssistance.Capture;

namespace DebugAssistance.Probes;

// One deduplicated captured call stack that invoked a probed method — the probe analogue of
// Capture.CapturedError, structured the same way (constructor, RecordOccurrence, MergeFrom,
// ComputeDedupeKey, IExposable) but for a probe invocation instead of an exception.
internal sealed class CapturedProbeHit : IExposable
{
#pragma warning disable IDE0032 // reassigned via `ref` from ExposeData; not addressable as an auto property's backing field
    private string _targetDeclaringTypeName;
    private string _targetMethodName;
    private string _targetDisplayName;
    private string _rawStackTrace;
    private string _dedupeKey;
#pragma warning restore IDE0032
    private List<CapturedStackFrame> _frames = [];
    private int _occurrenceCount;

    public string TargetDeclaringTypeName => _targetDeclaringTypeName;
    public string TargetMethodName => _targetMethodName;
    public string TargetDisplayName => _targetDisplayName;
    public string RawStackTrace => _rawStackTrace;
    public IReadOnlyList<CapturedStackFrame> Frames => _frames;
    public DateTime FirstSeen { get; private set; }
    public DateTime LastSeen { get; private set; }
    public int OccurrenceCount => _occurrenceCount;
    public string DedupeKey => _dedupeKey;

    public CapturedProbeHit(
        string targetDeclaringTypeName,
        string targetMethodName,
        string targetDisplayName,
        string rawStackTrace,
        IReadOnlyList<CapturedStackFrame> frames,
        DateTime timestamp
    )
    {
        _targetDeclaringTypeName = targetDeclaringTypeName;
        _targetMethodName = targetMethodName;
        _targetDisplayName = targetDisplayName;
        _rawStackTrace = rawStackTrace;
        _frames = [.. frames];
        FirstSeen = timestamp;
        LastSeen = timestamp;
        _occurrenceCount = 1;
        _dedupeKey = ComputeDedupeKey(targetDeclaringTypeName, targetMethodName, rawStackTrace);
    }

#pragma warning disable CS8618 // Only for scribing
    private CapturedProbeHit() { }
#pragma warning restore CS8618

    internal void RecordOccurrence(DateTime timestamp)
    {
        _occurrenceCount++;
        if (timestamp > LastSeen)
        {
            LastSeen = timestamp;
        }
    }

    // Folds a loaded duplicate's historical data into this already-in-memory entry, for the
    // merge-load flow (ProbeHitStore.Merge) — distinct from RecordOccurrence, which represents
    // exactly one new live occurrence rather than a whole saved history being folded back in.
    internal void MergeFrom(CapturedProbeHit other)
    {
        _occurrenceCount += other._occurrenceCount;
        if (other.FirstSeen < FirstSeen)
        {
            FirstSeen = other.FirstSeen;
        }
        if (other.LastSeen > LastSeen)
        {
            LastSeen = other.LastSeen;
        }
    }

    // targetDeclaringTypeName/targetMethodName (rather than the target's own metadata token) plus
    // rawStackTrace identify which method was probed and which exact call chain reached it, the
    // same joining/hashing approach as CapturedError.ComputeDedupeKey.
    internal static string ComputeDedupeKey(
        string targetDeclaringTypeName,
        string targetMethodName,
        string rawStackTrace
    )
    {
        var bytes = Encoding.UTF8.GetBytes(
            string.Join("\n", targetDeclaringTypeName, targetMethodName, rawStackTrace)
        );
        using var sha256 = SHA256.Create();
        return BitConverter
            .ToString(sha256.ComputeHash(bytes))
            .Replace("-", "", StringComparison.Ordinal);
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref _targetDeclaringTypeName!, "targetDeclaringTypeName");
        Scribe_Values.Look(ref _targetMethodName!, "targetMethodName");
        Scribe_Values.Look(ref _targetDisplayName!, "targetDisplayName");
        Scribe_Values.Look(ref _rawStackTrace!, "rawStackTrace");
        Scribe_Collections.Look(ref _frames, "frames", LookMode.Deep);

        var firstSeenTicks = FirstSeen.Ticks;
        Scribe_Values.Look(ref firstSeenTicks, "firstSeen");
        FirstSeen = new DateTime(firstSeenTicks);

        var lastSeenTicks = LastSeen.Ticks;
        Scribe_Values.Look(ref lastSeenTicks, "lastSeen");
        LastSeen = new DateTime(lastSeenTicks);

        Scribe_Values.Look(ref _occurrenceCount, "occurrenceCount");
        Scribe_Values.Look(ref _dedupeKey!, "dedupeKey");

        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            _frames ??= [];
        }
    }
}
