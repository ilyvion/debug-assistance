using System.Collections.Concurrent;
using System.Diagnostics;
using DebugAssistance.Capture;
using DebugAssistance.HotPatch;

namespace DebugAssistance.Probes;

// Owns the Harmony mechanics for probes: injects one shared static prefix method onto every
// probed target (Harmony hands it __originalMethod so it knows which target fired), tracked by
// ProbeDefinition in a global MethodBase -> ProbeDefinition map. Deliberately its own dedicated
// Harmony instance — distinct from both the error-capture patches' PatchAll instance and
// HotPatchManager's own instance — so removing a probe or resetting this manager never disturbs
// either of those.
internal sealed class ProbeManager
{
    internal const string HarmonyId = "ilyvion.debugassistance.probe";

    // Keyed by MethodBase rather than owned per ProbeManager instance, since Harmony patch
    // prefixes must be a single static method — SharedPrefix has no instance of its own to look an
    // owning ProbeManager up through, so every instance shares this one routing table. Each
    // ProbeDefinition still carries its own creating instance's Harmony (OwningHarmony) and
    // ProbeHitStore, so per-instance ownership (e.g. isolated instances in tests) is preserved for
    // everything else.
    private static readonly ConcurrentDictionary<MethodBase, ProbeDefinition> ActiveByTarget =
        new();

    private static readonly MethodInfo SharedPrefixMethod = AccessTools.Method(
        typeof(ProbeManager),
        nameof(SharedPrefix)
    );

    private readonly object _lock = new();
    private readonly Harmony _harmony;
    private readonly ProbeHitStore _hitStore;
    private readonly Func<long> _maxInvocations;
    private readonly Dictionary<string, ProbeDefinition> _byId = [];

    internal ProbeManager()
        : this(DebugAssistanceMod.ProbeHitStore, HarmonyId) { }

    internal ProbeManager(
        ProbeHitStore hitStore,
        string harmonyId,
        Func<long>? maxInvocations = null
    )
    {
        _harmony = new Harmony(harmonyId);
        _hitStore = hitStore;
        _maxInvocations = maxInvocations ?? (() => DebugAssistanceMod.Settings.ProbeMaxInvocations);
    }

    internal IReadOnlyList<ProbeDefinition> ActiveProbes
    {
        get
        {
            lock (_lock)
            {
                return [.. _byId.Values];
            }
        }
    }

    // Re-arms an existing, no-longer-active probe on the same target (e.g. one that hit its
    // invocation cap) rather than stacking a second prefix on top of it. Harmony throws for a
    // target it can't patch at all (e.g. an abstract or generic-definition method) -- caught here
    // and returned as a message rather than left to propagate, the same as HotPatchManager.Apply.
    internal (ProbeDefinition? Probe, string? Error) AddProbe(MethodBase target)
    {
        if (ActiveByTarget.TryGetValue(target, out var existingActive) && existingActive.IsActive)
        {
            return (existingActive, null);
        }

        var probe = new ProbeDefinition(
            Guid.NewGuid().ToString(),
            target,
            CSharpTypeFormatter.DescribeMethod(target),
            _harmony,
            _hitStore,
            _maxInvocations()
        );

        try
        {
            _ = _harmony.Patch(target, prefix: new HarmonyMethod(SharedPrefixMethod));
        }
        catch (Exception ex)
        {
            Log.Error($"[DebugAssistance] Adding a probe failed: {ex}");
            return (null, ex.Message);
        }

        ActiveByTarget[target] = probe;
        lock (_lock)
        {
            _byId[probe.Id] = probe;
        }

        return (probe, null);
    }

    // Unpatches (if still active) and marks ManuallyRemoved, then drops the ProbeDefinition from
    // the active set entirely — unlike an invocation-capped probe, a manually removed one isn't
    // kept around for the UI to keep showing. Returns false without touching Harmony at all if
    // `id` isn't (or is no longer) tracked.
    internal bool RemoveProbe(string id)
    {
        ProbeDefinition? probe;
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out probe))
            {
                return false;
            }
            _ = _byId.Remove(id);
        }

        if (probe.IsActive)
        {
            _harmony.Unpatch(probe.TargetMethod, SharedPrefixMethod);
            probe.Deactivate(ProbeCapReason.ManuallyRemoved);
        }
        _ = ActiveByTarget.TryRemove(probe.TargetMethod, out _);

        return true;
    }

    // Harmony's injected special parameter: the original method being probed, so this one shared
    // prefix can look up which ProbeDefinition fired regardless of which target it's patched onto.
#pragma warning disable IDE0051 // Used by Harmony via reflection
    private static void SharedPrefix(MethodBase __originalMethod)
    {
        if (!ActiveByTarget.TryGetValue(__originalMethod, out var probe) || !probe.IsActive)
        {
            return;
        }

        var newTotal = probe.IncrementInvocationCount();
        if (newTotal >= probe.MaxInvocations)
        {
            // Only the call that lands exactly on the cap actually unpatches — Harmony supports
            // unpatching a method while a call to it is already in flight (future calls hit the
            // original; this call finishes running whatever body it's already inside of), so a
            // handful of calls racing past the cap concurrently just fall through to this same
            // early return rather than each trying to unpatch again.
            if (newTotal == probe.MaxInvocations)
            {
                probe.OwningHarmony.Unpatch(probe.TargetMethod, SharedPrefixMethod);
                probe.Deactivate(ProbeCapReason.InvocationCapReached);
            }
            return;
        }

        RecordInvocation(probe);
    }
#pragma warning restore IDE0051

    // The cheap fast path: a hash built purely from each live frame's MethodBase, with no string
    // formatting or PDB/file lookups — cheap enough to run on every invocation of a potentially hot
    // method. Only once a fast-path key is genuinely new does this fall through to the expensive
    // full symbolication (BuildCapturedHit) that error capture also pays on every occurrence.
    private static void RecordInvocation(ProbeDefinition probe)
    {
        // Skips this method's own frame and SharedPrefix's (its caller) — frame 2 onward is the
        // actual call chain that reached the probed target, without ProbeManager's own plumbing
        // cluttering the captured stack.
        var cheapFrames = new StackTrace(2, fNeedFileInfo: false).GetFrames();
        if (cheapFrames is null || cheapFrames.Length == 0)
        {
            return;
        }

        var fastPathKey = ComputeFastPathKey(cheapFrames);
        if (probe.TryGetDedupeKeyForFastPathKey(fastPathKey, out var existingDedupeKey))
        {
            if (existingDedupeKey is not null)
            {
                _ = probe.HitStore.RecordOccurrence(existingDedupeKey, DateTime.UtcNow);
            }
            return;
        }

        var hit = BuildCapturedHit(probe);
        var stored = probe.HitStore.Add(hit);
        probe.RecordFastPathKey(fastPathKey, stored.DedupeKey);
    }

    private static CapturedProbeHit BuildCapturedHit(ProbeDefinition probe)
    {
        // Skips BuildCapturedHit's own frame, RecordInvocation's (its caller), and SharedPrefix's
        // (RecordInvocation's caller) — same three frames of ProbeManager's own plumbing the cheap
        // fast-path key above already skips, kept in sync so both agree on where the real call
        // chain starts.
        var stackTrace = new StackTrace(3, fNeedFileInfo: true);
        var frames = LiveStackFrameBuilder.BuildFrames(
            stackTrace.GetFrames() ?? [],
            stackTrace.ToString()
        );
        return new CapturedProbeHit(
            probe.TargetMethod.DeclaringType?.FullName ?? "?",
            probe.TargetMethod.Name,
            probe.TargetDisplayName,
            stackTrace.ToString(),
            frames,
            DateTime.UtcNow
        );
    }

    // Combines each frame's method identity (its handle, not its symbolicated name) into one hash
    // — deliberately no string formatting or file/line lookups here, that's the whole point of the
    // fast path.
    internal static long ComputeFastPathKey(StackFrame[] frames)
    {
        unchecked
        {
            var hash = 17L;
            foreach (var frame in frames)
            {
                var handle = frame.GetMethod()?.MethodHandle.Value ?? IntPtr.Zero;
                hash = (hash * 31) + handle.ToInt64();
            }
            return hash;
        }
    }
}
