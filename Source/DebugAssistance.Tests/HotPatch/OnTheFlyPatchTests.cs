using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class OnTheFlyPatchTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FixtureTarget() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FixturePatchMethod() { }

    [Test]
    public static void CreatePopulatesEveryFieldFromItsArguments()
    {
        var target = typeof(OnTheFlyPatchTests).GetMethod(
            nameof(FixtureTarget),
            BindingFlags.NonPublic | BindingFlags.Static
        );
        var patchMethod = typeof(OnTheFlyPatchTests).GetMethod(
            nameof(FixturePatchMethod),
            BindingFlags.NonPublic | BindingFlags.Static
        );

        var patch = OnTheFlyPatch.Create(
            target,
            patchMethod,
            HarmonyPatchType.Prefix,
            "/some/path/Patches.dll",
            3
        );

        Assert.That(patch.Target.Name).Is.EqualTo(target.Name);
        Assert.That(patch.PatchMethod.Name).Is.EqualTo(patchMethod.Name);
        Assert.That(patch.PatchType).Is.EqualTo(HarmonyPatchType.Prefix);
        Assert.That(patch.SourceAssemblyPath).Is.EqualTo("/some/path/Patches.dll");
        Assert.That(patch.SourceAssemblyGeneration).Is.EqualTo(3);
    }

    [Test]
    public static void CreateGeneratesADifferentIdForEachCall()
    {
        var target = typeof(OnTheFlyPatchTests).GetMethod(
            nameof(FixtureTarget),
            BindingFlags.NonPublic | BindingFlags.Static
        );
        var patchMethod = typeof(OnTheFlyPatchTests).GetMethod(
            nameof(FixturePatchMethod),
            BindingFlags.NonPublic | BindingFlags.Static
        );

        var first = OnTheFlyPatch.Create(target, patchMethod, HarmonyPatchType.Prefix, "path", 1);
        var second = OnTheFlyPatch.Create(target, patchMethod, HarmonyPatchType.Prefix, "path", 1);

        Assert.That(first.Id.Equals(second.Id)).Is.False();
    }
}
