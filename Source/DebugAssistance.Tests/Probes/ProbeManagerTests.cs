using DebugAssistance.Probes;
using RimTestRedux;

namespace DebugAssistance.Tests.Probes;

// Exercises ProbeManager the same way HotPatchManagerTests exercises HotPatchManager: real Harmony
// patching against plain static fixture methods, no live RimWorld/game process needed. Each test
// uses its own fixture target method (never shared) since ProbeManager's target->probe routing
// table is process-global.
[TestSuite]
internal static class ProbeManagerTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UniqueHitTarget() { }

    [Test]
    public static void CallingTheProbedMethodFromTheSameCallSiteTwiceCountsOneUniqueHit()
    {
        var hitStore = new ProbeHitStore(() => 500);
        var manager = new ProbeManager(
            hitStore,
            "test.debugassistance.probemanagertests.uniquehit",
            maxInvocations: () => 1000
        );
        var target = AccessTools.Method(typeof(ProbeManagerTests), nameof(UniqueHitTarget));

        var (probe, error) = manager.AddProbe(target);
        Assert.That(error is null).Is.True();

        UniqueHitTarget();
        UniqueHitTarget();

        Assert.That(probe!.TotalInvocationCount).Is.EqualTo(2L);
        Assert.That(probe.UniqueHitCount).Is.EqualTo(1);
        var hits = hitStore.Snapshot();
        Assert.ThatCollection(hits).Has.Count(1);
        Assert.That(hits[0].OccurrenceCount).Is.EqualTo(2);
        Assert.That(hits[0].TargetMethodName).Is.EqualTo(nameof(UniqueHitTarget));

        _ = manager.RemoveProbe(probe.Id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CapTarget() { }

    [Test]
    public static void ReachingTheInvocationCapAutoUnpatchesAndStopsCountingFurtherCalls()
    {
        var hitStore = new ProbeHitStore(() => 500);
        var manager = new ProbeManager(
            hitStore,
            "test.debugassistance.probemanagertests.cap",
            maxInvocations: () => 2
        );
        var target = AccessTools.Method(typeof(ProbeManagerTests), nameof(CapTarget));

        var (probe, error) = manager.AddProbe(target);
        Assert.That(error is null).Is.True();

        CapTarget();
        CapTarget();
        Assert.That(probe!.IsActive).Is.False();
        Assert.That(probe.CapReason).Is.EqualTo(ProbeCapReason.InvocationCapReached);
        Assert.That(probe.TotalInvocationCount).Is.EqualTo(2L);

        // Already unpatched, so this call never reaches the shared prefix at all.
        CapTarget();
        Assert.That(probe.TotalInvocationCount).Is.EqualTo(2L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveTarget() { }

    [Test]
    public static void RemoveProbeUnpatchesAndDropsItFromActiveProbes()
    {
        var hitStore = new ProbeHitStore(() => 500);
        var manager = new ProbeManager(
            hitStore,
            "test.debugassistance.probemanagertests.remove",
            maxInvocations: () => 1000
        );
        var target = AccessTools.Method(typeof(ProbeManagerTests), nameof(RemoveTarget));

        var (probe, error) = manager.AddProbe(target);
        Assert.That(error is null).Is.True();
        Assert.ThatCollection(manager.ActiveProbes).Has.Count(1);

        var removed = manager.RemoveProbe(probe!.Id);

        Assert.That(removed).Is.True();
        Assert.ThatCollection(manager.ActiveProbes).Is.Empty();

        RemoveTarget();
        Assert.That(probe.TotalInvocationCount).Is.EqualTo(0L);
    }

    [Test]
    public static void RemoveProbeReturnsFalseForAnUnknownId()
    {
        var manager = new ProbeManager(
            new ProbeHitStore(() => 500),
            "test.debugassistance.probemanagertests.removeunknown",
            maxInvocations: () => 1000
        );

        Assert.That(manager.RemoveProbe(Guid.NewGuid().ToString())).Is.False();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReAddTarget() { }

    [Test]
    public static void AddingAProbeForAnAlreadyActiveTargetReturnsTheExistingProbeInstead()
    {
        var manager = new ProbeManager(
            new ProbeHitStore(() => 500),
            "test.debugassistance.probemanagertests.readd",
            maxInvocations: () => 1000
        );
        var target = AccessTools.Method(typeof(ProbeManagerTests), nameof(ReAddTarget));

        var (first, errorA) = manager.AddProbe(target);
        var (second, errorB) = manager.AddProbe(target);

        Assert.That(errorA is null).Is.True();
        Assert.That(errorB is null).Is.True();
        Assert.That(ReferenceEquals(first, second)).Is.True();
        Assert.ThatCollection(manager.ActiveProbes).Has.Count(1);

        _ = manager.RemoveProbe(first!.Id);
    }
}
