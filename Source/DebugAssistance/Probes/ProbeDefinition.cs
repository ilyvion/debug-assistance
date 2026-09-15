using System.Collections.Concurrent;

namespace DebugAssistance.Probes;

// Why a probe most recently stopped tracking its target: None while still active,
// InvocationCapReached once ProbeManager's shared prefix auto-unpatches it after reaching its
// invocation cap, ManuallyRemoved once the player stops it from the UI.
internal enum ProbeCapReason
{
    None,
    InvocationCapReached,
    ManuallyRemoved,
}

// One active (or just-deactivated) tracker attached to a target method by ProbeManager.AddProbe —
// distinct from CapturedProbeHit, which is one deduplicated captured stack a probe has recorded.
// Never Scribed: TargetMethod is a live MethodBase reference, and probes (like on-the-fly hot
// patches) don't persist across a patch-manager reset/reload — only the CapturedProbeHit entries
// already recorded into ProbeHitStore survive that.
internal sealed class ProbeDefinition(
    string id,
    MethodBase targetMethod,
    string targetDisplayName,
    Harmony owningHarmony,
    ProbeHitStore hitStore,
    long maxInvocations
)
{
    // Maps a cheap fast-path stack key (see ProbeManager.ComputeFastPathKey) to the dedupe key of
    // the CapturedProbeHit already recorded for it, so a repeat call from the same call site only
    // ever costs a dictionary lookup plus ProbeHitStore.RecordOccurrence, never a re-symbolication.
    private readonly ConcurrentDictionary<long, string> _seenFastPathKeys = new();
    private long _totalInvocationCount;
    private volatile bool _isActive = true;
    private volatile ProbeCapReason _capReason = ProbeCapReason.None;

    internal string Id => id;
    internal MethodBase TargetMethod => targetMethod;
    internal string TargetDisplayName => targetDisplayName;
    internal Harmony OwningHarmony => owningHarmony;
    internal ProbeHitStore HitStore => hitStore;
    internal long MaxInvocations => maxInvocations;
    internal DateTime AppliedAt { get; } = DateTime.UtcNow;
    internal long TotalInvocationCount => Interlocked.Read(ref _totalInvocationCount);
    internal int UniqueHitCount => _seenFastPathKeys.Count;
    internal bool IsActive => _isActive;
    internal ProbeCapReason CapReason => _capReason;

    // Returns the post-increment total, so the caller can tell in one atomic step whether this
    // exact call is the one that just crossed the invocation cap.
    internal long IncrementInvocationCount() => Interlocked.Increment(ref _totalInvocationCount);

    internal bool TryGetDedupeKeyForFastPathKey(long fastPathKey, out string? dedupeKey) =>
        _seenFastPathKeys.TryGetValue(fastPathKey, out dedupeKey);

    internal void RecordFastPathKey(long fastPathKey, string dedupeKey) =>
        _seenFastPathKeys[fastPathKey] = dedupeKey;

    internal void Deactivate(ProbeCapReason reason)
    {
        _isActive = false;
        _capReason = reason;
    }
}
