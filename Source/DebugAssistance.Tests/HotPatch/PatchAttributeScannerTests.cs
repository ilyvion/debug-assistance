using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class PatchAttributeScannerTests
{
#pragma warning disable IDE0060 // Remove unused parameter -- only its type matters to the test
    private sealed class TargetMethods
    {
        public TargetMethods() { }

        public static void Foo(int x) { }

        public static void Bar() { }
    }

    [HarmonyPatch(typeof(TargetMethods), nameof(TargetMethods.Foo))]
    private static class ClassLevelPatches
    {
        [HarmonyPrefix]
        internal static bool Prefix(int x) => true;

        [HarmonyPostfix]
        internal static void Postfix(int x) { }
#pragma warning restore IDE0060

        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        ) => instructions;

        [HarmonyFinalizer]
        internal static Exception? Finalizer(Exception? __exception) => __exception;

        // No [Harmony*] attribute at all -- must never show up as a discovered patch.
        internal static void NotAPatchMethod() { }
    }

    [HarmonyPatch(typeof(TargetMethods), MethodType.Constructor)]
    private static class ConstructorTargetPatch
    {
        [HarmonyPrefix]
        internal static bool Prefix() => true;
    }

    private static class MethodLevelPatch
    {
        [HarmonyPatch(typeof(TargetMethods), nameof(TargetMethods.Bar))]
        [HarmonyPrefix]
        internal static bool Prefix() => true;
    }

    private static class UnresolvableTargetPatch
    {
        [HarmonyPatch(typeof(TargetMethods), "DoesNotExist")]
        [HarmonyPrefix]
        internal static bool Prefix() => true;
    }

    private static List<DiscoveredPatch> ScanThisAssembly() =>
        PatchAttributeScanner.Scan(typeof(PatchAttributeScannerTests).Assembly);

    private static List<DiscoveredPatch> DiscoveredFrom(Type patchContainer) =>
        [
            .. ScanThisAssembly()
                .Where(discovered => discovered.PatchMethod.DeclaringType == patchContainer),
        ];

    [Test]
    public static void ScanFindsAllFourAttributedMethodsOnAClassLevelTarget()
    {
        var discovered = DiscoveredFrom(typeof(ClassLevelPatches));
        var fooMethod = typeof(TargetMethods).GetMethod(nameof(TargetMethods.Foo));

        Assert.ThatCollection(discovered).Has.Count(4);
        var patchTypes = discovered.Select(d => d.PatchType).ToList();
        Assert.ThatCollection(patchTypes).Does.Contain(OnTheFlyPatchType.Prefix);
        Assert.ThatCollection(patchTypes).Does.Contain(OnTheFlyPatchType.Postfix);
        Assert.ThatCollection(patchTypes).Does.Contain(OnTheFlyPatchType.Transpiler);
        Assert.ThatCollection(patchTypes).Does.Contain(OnTheFlyPatchType.Finalizer);
        foreach (var d in discovered)
        {
            Assert.That(d.Target == fooMethod).Is.True();
        }
    }

    [Test]
    public static void ScanSkipsAMethodWithNoHarmonyPatchAttribute()
    {
        var discovered = DiscoveredFrom(typeof(ClassLevelPatches));

        Assert.That(discovered.Any(d => d.PatchMethod.Name == "NotAPatchMethod")).Is.False();
    }

    [Test]
    public static void ScanResolvesAConstructorTarget()
    {
        var discovered = DiscoveredFrom(typeof(ConstructorTargetPatch));
        var constructor = typeof(TargetMethods).GetConstructor(Type.EmptyTypes);

        Assert.ThatCollection(discovered).Has.Count(1);
        Assert.That(discovered[0].Target == constructor).Is.True();
        Assert.That(discovered[0].PatchType).Is.EqualTo(OnTheFlyPatchType.Prefix);
    }

    [Test]
    public static void ScanMergesAMethodLevelHarmonyPatchAttributeWithNoClassLevelOne()
    {
        var discovered = DiscoveredFrom(typeof(MethodLevelPatch));
        var barMethod = typeof(TargetMethods).GetMethod(nameof(TargetMethods.Bar));

        Assert.ThatCollection(discovered).Has.Count(1);
        Assert.That(discovered[0].Target == barMethod).Is.True();
    }

    [Test]
    public static void ScanSkipsAPatchWhoseTargetDoesNotResolve()
    {
        var discovered = DiscoveredFrom(typeof(UnresolvableTargetPatch));

        Assert.ThatCollection(discovered).Has.Count(0);
    }
}
