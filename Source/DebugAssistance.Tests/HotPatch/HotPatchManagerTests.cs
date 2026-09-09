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
            HarmonyPatchType.Prefix,
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
            HarmonyPatchType.Postfix,
            "fixture.dll",
            1
        );
        var (patchB, errorB) = manager.Apply(
            target,
            AccessTools.Method(typeof(HotPatchManagerTests), nameof(LoggingPostfixB)),
            HarmonyPatchType.Postfix,
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
            HarmonyPatchType.Postfix,
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
            HarmonyPatchType.Prefix,
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

    private static readonly List<string> ReloadLog = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReloadTargetA(int value) => _ = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReloadTargetB(int value) => _ = value;

#pragma warning disable IDE0051 // Used as a Harmony patch method, invoked by reflection
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReloadMarkerPostfix(int value) => ReloadLog.Add($"marker:{value}");
#pragma warning restore IDE0051

    // The scenario a reload-with-removal depends on: only the patches whose patch method came
    // from the reloaded assembly are removed, an independent patch whose patch method came from a
    // different assembly is left alone.
    [Test]
    public static void RemoveAllFromAssemblyOnlyRemovesPatchesFromThatAssembly()
    {
        ReloadLog.Clear();
        var manager = new HotPatchManager("test.debugassistance.hotpatchmanagertests.reload");
        var thisAssemblyPatchMethod = AccessTools.Method(
            typeof(HotPatchManagerTests),
            nameof(ReloadMarkerPostfix)
        );
        var otherAssemblyPatchMethod = AccessTools.Method(
            typeof(GC),
            nameof(GC.WaitForPendingFinalizers)
        );

        var (fromThisAssembly, errorA) = manager.Apply(
            AccessTools.Method(typeof(HotPatchManagerTests), nameof(ReloadTargetA)),
            thisAssemblyPatchMethod,
            HarmonyPatchType.Postfix,
            "this-assembly.dll",
            1
        );
        var (fromOtherAssembly, errorB) = manager.Apply(
            AccessTools.Method(typeof(HotPatchManagerTests), nameof(ReloadTargetB)),
            otherAssemblyPatchMethod,
            HarmonyPatchType.Postfix,
            "other-assembly.dll",
            1
        );
        Assert.That(errorA is null).Is.True();
        Assert.That(errorB is null).Is.True();

        var removed = manager.RemoveAllFromAssembly(typeof(HotPatchManagerTests).Assembly);

        Assert.ThatCollection(removed).Has.Count(1);
        Assert.That(removed[0].Id.Equals(fromThisAssembly!.Id)).Is.True();
        Assert.ThatCollection(manager.ActivePatches).Has.Count(1);
        Assert.That(manager.ActivePatches[0].Id.Equals(fromOtherAssembly!.Id)).Is.True();

        ReloadTargetA(1);
        Assert.ThatCollection(ReloadLog).Is.Empty();

        _ = manager.Remove(fromOtherAssembly!);
    }
}
