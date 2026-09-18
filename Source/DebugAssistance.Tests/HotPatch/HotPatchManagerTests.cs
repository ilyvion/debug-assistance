using System.Reflection.Emit;
using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class HotPatchManagerTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PrefixTarget(int value) => value + 1;

#pragma warning disable IDE0051 // Used as a Harmony patch method, invoked by reflection
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SkipWithFixedResultPrefix(ref int __result)
    {
        __result = 100;
        return false;
    }
#pragma warning restore IDE0051

    [Test]
    public static void ApplyingAPrefixChangesTargetBehaviorAndRemovingItReverts()
    {
        var manager = new HotPatchManager("test.debugassistance.hotpatchmanagertests.prefix");
        var target = AccessTools.Method(typeof(HotPatchManagerTests), nameof(PrefixTarget));
        var patchMethod = AccessTools.Method(
            typeof(HotPatchManagerTests),
            nameof(SkipWithFixedResultPrefix)
        );

        var (patch, error) = manager.Apply(
            target,
            patchMethod,
            OnTheFlyPatchType.Prefix,
            "fixture.dll",
            1
        );

        Assert.That(error is null).Is.True();
        Assert.That(PrefixTarget(5)).Is.EqualTo(100);

        Assert.That(manager.Remove(patch!)).Is.True();
        Assert.That(PrefixTarget(5)).Is.EqualTo(6);
    }

    private static readonly List<string> PostfixLog = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PostfixTarget(int value) => _ = value;

#pragma warning disable IDE0051 // Used as a Harmony patch method, invoked by reflection
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoggingPostfixA(int value) => PostfixLog.Add($"A:{value}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoggingPostfixB(int value) => PostfixLog.Add($"B:{value}");
#pragma warning restore IDE0051

    // Direct regression coverage for the "must not disturb other patches" design decision: two
    // independent on-the-fly patches on the same target, applied through the same HotPatchManager,
    // must be removable one at a time without the other one going along with it.
    [Test]
    public static void RemovingOneOfTwoIndependentPatchesOnTheSameTargetLeavesTheOtherActive()
    {
        PostfixLog.Clear();
        var manager = new HotPatchManager("test.debugassistance.hotpatchmanagertests.two");
        var target = AccessTools.Method(typeof(HotPatchManagerTests), nameof(PostfixTarget));
        var (patchA, errorA) = manager.Apply(
            target,
            AccessTools.Method(typeof(HotPatchManagerTests), nameof(LoggingPostfixA)),
            OnTheFlyPatchType.Postfix,
            "fixture.dll",
            1
        );
        var (patchB, errorB) = manager.Apply(
            target,
            AccessTools.Method(typeof(HotPatchManagerTests), nameof(LoggingPostfixB)),
            OnTheFlyPatchType.Postfix,
            "fixture.dll",
            1
        );
        Assert.That(errorA is null).Is.True();
        Assert.That(errorB is null).Is.True();

        PostfixTarget(1);
        Assert.ThatCollection(PostfixLog).Does.Contain("A:1");
        Assert.ThatCollection(PostfixLog).Does.Contain("B:1");

        Assert.That(manager.Remove(patchA!)).Is.True();
        PostfixLog.Clear();
        PostfixTarget(2);

        Assert.ThatCollection(PostfixLog).Does.Not.Contain("A:2");
        Assert.ThatCollection(PostfixLog).Does.Contain("B:2");

        _ = manager.Remove(patchB!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WrongSignatureTarget() { }

#pragma warning disable IDE0051 // Used as a Harmony patch method, invoked by reflection
    // A postfix with no parameters and a non-void return type isn't a valid "pass through"
    // postfix (that requires the return type to match its first parameter's type) -- Harmony
    // rejects it at Patch() time.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string WrongSignaturePostfix() => "not-void";
#pragma warning restore IDE0051

    [Test]
    public static void ApplyingAWrongSignaturePatchMethodReturnsAnErrorInsteadOfThrowing()
    {
        var manager = new HotPatchManager("test.debugassistance.hotpatchmanagertests.badsig");
        var target = AccessTools.Method(typeof(HotPatchManagerTests), nameof(WrongSignatureTarget));
        var patchMethod = AccessTools.Method(
            typeof(HotPatchManagerTests),
            nameof(WrongSignaturePostfix)
        );

        var (patch, error) = manager.Apply(
            target,
            patchMethod,
            OnTheFlyPatchType.Postfix,
            "fixture.dll",
            1
        );

        Assert.That(patch is null).Is.True();
        Assert.That(string.IsNullOrEmpty(error)).Is.False();
        Assert.ThatCollection(manager.ActivePatches).Is.Empty();
    }

    private sealed class InstancePatchMethods
    {
#pragma warning disable CA1822 // Mark members as static -- deliberately an instance method
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void InstancePrefix() { }
#pragma warning restore CA1822
    }

    // Harmony's manual Patch(target, prefix: new HarmonyMethod(patchMethod)) API has no way to
    // supply an instance for a non-static patch method -- letting one through produces the opaque
    // "Invalid IL code ... call 0x00000001"-style JIT error this rejects up front instead.
    [Test]
    public static void ApplyingANonStaticPatchMethodIsRejectedBeforeReachingHarmony()
    {
        var manager = new HotPatchManager("test.debugassistance.hotpatchmanagertests.nonstatic");
        var target = AccessTools.Method(typeof(HotPatchManagerTests), nameof(PrefixTarget));
        var patchMethod = AccessTools.Method(
            typeof(InstancePatchMethods),
            nameof(InstancePatchMethods.InstancePrefix)
        );

        var (patch, error) = manager.Apply(
            target,
            patchMethod,
            OnTheFlyPatchType.Prefix,
            "fixture.dll",
            1
        );

        Assert.That(patch is null).Is.True();
        Assert
            .That(error is not null && error.Contains("static", StringComparison.Ordinal))
            .Is.True();
        Assert.ThatCollection(manager.ActivePatches).Is.Empty();
        Assert.That(PrefixTarget(5)).Is.EqualTo(6);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReplaceStaticTarget(int value) => value + 1;

#pragma warning disable IDE0051 // Used as a Harmony patch method, invoked by reflection
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReplaceStaticReplacement(int value) => value * 2;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReplaceMismatchedReplacement(int value, int extra) => value + extra;
#pragma warning restore IDE0051

    [Test]
    public static void ApplyingAReplaceOnAStaticMethodCallsTheReplacementInsteadAndRemovingItReverts()
    {
        var manager = new HotPatchManager(
            "test.debugassistance.hotpatchmanagertests.replacestatic"
        );
        var target = AccessTools.Method(typeof(HotPatchManagerTests), nameof(ReplaceStaticTarget));
        var replacement = AccessTools.Method(
            typeof(HotPatchManagerTests),
            nameof(ReplaceStaticReplacement)
        );

        var (patch, error) = manager.Apply(
            target,
            replacement,
            OnTheFlyPatchType.Replace,
            "fixture.dll",
            1
        );

        Assert.That(error is null).Is.True();
        Assert.That(ReplaceStaticTarget(5)).Is.EqualTo(10);

        Assert.That(manager.Remove(patch!)).Is.True();
        Assert.That(ReplaceStaticTarget(5)).Is.EqualTo(6);
    }

    [Test]
    public static void ApplyingAReplaceWithAMismatchedSignatureReturnsAnErrorInsteadOfThrowing()
    {
        var manager = new HotPatchManager(
            "test.debugassistance.hotpatchmanagertests.replacemismatch"
        );
        var target = AccessTools.Method(typeof(HotPatchManagerTests), nameof(ReplaceStaticTarget));
        var replacement = AccessTools.Method(
            typeof(HotPatchManagerTests),
            nameof(ReplaceMismatchedReplacement)
        );

        var (patch, error) = manager.Apply(
            target,
            replacement,
            OnTheFlyPatchType.Replace,
            "fixture.dll",
            1
        );

        Assert.That(patch is null).Is.True();
        Assert.That(string.IsNullOrEmpty(error)).Is.False();
        Assert.ThatCollection(manager.ActivePatches).Is.Empty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReplaceRefTarget(ref int value) => value += 1;

#pragma warning disable IDE0051 // Used as a Harmony patch method, invoked by reflection
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReplaceRefReplacement(ref int value) => value *= 2;
#pragma warning restore IDE0051

    [Test]
    public static void ApplyingAReplaceForwardsRefParametersToTheReplacement()
    {
        var manager = new HotPatchManager("test.debugassistance.hotpatchmanagertests.replaceref");
        var target = AccessTools.Method(typeof(HotPatchManagerTests), nameof(ReplaceRefTarget));
        var replacement = AccessTools.Method(
            typeof(HotPatchManagerTests),
            nameof(ReplaceRefReplacement)
        );

        var (patch, error) = manager.Apply(
            target,
            replacement,
            OnTheFlyPatchType.Replace,
            "fixture.dll",
            1
        );

        Assert.That(error is null).Is.True();
        var value = 5;
        ReplaceRefTarget(ref value);
        Assert.That(value).Is.EqualTo(10);

        _ = manager.Remove(patch!);
    }

    // Simulates the feature's real motivating scenario: a target instance method and a
    // "replacement" instance method declared on two entirely distinct (but layout-identical)
    // types, standing in for a target's original type and the reloaded fixed assembly's own
    // (necessarily distinct, per LiveAssemblyLoader) copy of that type. Proves the replacement's
    // body actually runs against the *original* type's live instance.
    private sealed class ReplaceOriginalInstanceType
    {
        public int Field = 10;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Compute(int value) => value + Field;
    }

    private sealed class ReplaceReplacementInstanceType
    {
        public int Field = 10;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Compute(int value) => value * Field;
    }

    [Test]
    public static void ApplyingAReplaceOnAnInstanceMethodRunsTheReplacementAgainstTheOriginalInstance()
    {
        var manager = new HotPatchManager(
            "test.debugassistance.hotpatchmanagertests.replaceinstance"
        );
        var target = AccessTools.Method(
            typeof(ReplaceOriginalInstanceType),
            nameof(ReplaceOriginalInstanceType.Compute)
        );
        var replacement = AccessTools.Method(
            typeof(ReplaceReplacementInstanceType),
            nameof(ReplaceReplacementInstanceType.Compute)
        );

        var (patch, error) = manager.Apply(
            target,
            replacement,
            OnTheFlyPatchType.Replace,
            "fixture.dll",
            1
        );

        Assert.That(error is null).Is.True();
        var instance = new ReplaceOriginalInstanceType();
        Assert.That(instance.Compute(4)).Is.EqualTo(40);

        Assert.That(manager.Remove(patch!)).Is.True();
        Assert.That(instance.Compute(4)).Is.EqualTo(14);
    }

    // Regression test: the reverse-patched static "core" that the Replace shim calls into used to
    // have no GC root of its own once Build() returned, so a collection between applying the patch
    // and the target actually running could free its JIT-compiled code out from under the shim.
    [Test]
    public static void ApplyingAReplaceOnAnInstanceMethodSurvivesAGarbageCollectionBeforeItRuns()
    {
        var manager = new HotPatchManager(
            "test.debugassistance.hotpatchmanagertests.replaceinstance.gc"
        );
        var target = AccessTools.Method(
            typeof(ReplaceOriginalInstanceType),
            nameof(ReplaceOriginalInstanceType.Compute)
        );
        var replacement = AccessTools.Method(
            typeof(ReplaceReplacementInstanceType),
            nameof(ReplaceReplacementInstanceType.Compute)
        );

        var (patch, error) = manager.Apply(
            target,
            replacement,
            OnTheFlyPatchType.Replace,
            "fixture.dll",
            1
        );
        Assert.That(error is null).Is.True();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var instance = new ReplaceOriginalInstanceType();
        Assert.That(instance.Compute(4)).Is.EqualTo(40);

        Assert.That(manager.Remove(patch!)).Is.True();
    }

    [Test]
    public static void FormatIlDiagnosticMarksTheFailingInstructionAndKeepsSurroundingContext()
    {
        List<CodeInstruction> instructions =
        [
            new(OpCodes.Ldarg_0),
            new(OpCodes.Ldc_I4_1),
            new(OpCodes.Call),
            new(OpCodes.Ret),
        ];

        var result = HotPatchManager.FormatIlDiagnostic(
            "Invalid IL code: call 0x00000001",
            instructions,
            2
        );

        Assert
            .That(result.Contains("Invalid IL code: call 0x00000001", StringComparison.Ordinal))
            .Is.True();
        var lines = result.Split(Environment.NewLine);
        Assert
            .That(
                lines.Any(l =>
                    l.StartsWith("-> ", StringComparison.Ordinal)
                    && l.Contains("call", StringComparison.Ordinal)
                )
            )
            .Is.True();
        Assert
            .That(
                lines.Any(l =>
                    l.StartsWith("   ", StringComparison.Ordinal)
                    && l.Contains("ldarg", StringComparison.Ordinal)
                )
            )
            .Is.True();
    }

    [Test]
    public static void FormatIlDiagnosticClampsContextAtTheStartAndEndOfTheInstructionList()
    {
        List<CodeInstruction> instructions = [new(OpCodes.Nop), new(OpCodes.Ret)];

        var result = HotPatchManager.FormatIlDiagnostic("boom", instructions, 0);

        Assert.That(result.Contains("boom", StringComparison.Ordinal)).Is.True();
        var lines = result.Split(Environment.NewLine);
        Assert
            .ThatCollection(
                lines.Where(l =>
                    l.StartsWith("-> ", StringComparison.Ordinal)
                    || l.StartsWith("   ", StringComparison.Ordinal)
                )
            )
            .Has.Count(2);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveManyTargetA(int value) => _ = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveManyTargetB(int value) => _ = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveManyTargetC(int value) => _ = value;

#pragma warning disable IDE0051 // Used as a Harmony patch method, invoked by reflection
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveManyNoOpPrefix() { }
#pragma warning restore IDE0051

    // The multi-select "Remove selected" scenario: only the ids passed in are removed, an active
    // patch whose id isn't among them is left alone, and an id that doesn't match any active patch
    // is silently ignored rather than erroring the whole call.
    [Test]
    public static void RemoveManyRemovesOnlyTheGivenIdsAndIgnoresUnknownOnes()
    {
        var manager = new HotPatchManager("test.debugassistance.hotpatchmanagertests.removemany");
        var patchMethod = AccessTools.Method(
            typeof(HotPatchManagerTests),
            nameof(RemoveManyNoOpPrefix)
        );

        var (patchA, errorA) = manager.Apply(
            AccessTools.Method(typeof(HotPatchManagerTests), nameof(RemoveManyTargetA)),
            patchMethod,
            OnTheFlyPatchType.Prefix,
            "fixture.dll",
            1
        );
        var (patchB, errorB) = manager.Apply(
            AccessTools.Method(typeof(HotPatchManagerTests), nameof(RemoveManyTargetB)),
            patchMethod,
            OnTheFlyPatchType.Prefix,
            "fixture.dll",
            1
        );
        var (patchC, errorC) = manager.Apply(
            AccessTools.Method(typeof(HotPatchManagerTests), nameof(RemoveManyTargetC)),
            patchMethod,
            OnTheFlyPatchType.Prefix,
            "fixture.dll",
            1
        );
        Assert.That(errorA is null).Is.True();
        Assert.That(errorB is null).Is.True();
        Assert.That(errorC is null).Is.True();

        var removed = manager.RemoveMany([patchA!.Id, patchC!.Id, Guid.NewGuid()]);

        Assert.ThatCollection(removed).Has.Count(2);
        Assert.ThatCollection(manager.ActivePatches).Has.Count(1);
        Assert.That(manager.ActivePatches[0].Id.Equals(patchB!.Id)).Is.True();

        _ = manager.Remove(patchB!);
    }
}
