using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class ConveniencePatchScannerTests
{
#pragma warning disable IDE0060 // Remove unused parameter -- only its type matters to the test
    private static class ConveniencePatchFixtures
    {
        [ConveniencePatch(
            "Skip it",
            "A prefix that skips the method.",
            ConveniencePatchType.Prefix
        )]
        internal static bool Prefix() => false;

        [ConveniencePatch(
            "Log return",
            "A postfix that logs the return value.",
            ConveniencePatchType.Postfix
        )]
        internal static void Postfix(object __result) { }

        [ConveniencePatch(
            "Swallow exception",
            "A finalizer that swallows the exception.",
            ConveniencePatchType.Finalizer
        )]
        internal static Exception? Finalizer(Exception? __exception) => null;
#pragma warning restore IDE0060

        // No [ConveniencePatch] attribute at all -- must never show up as a discovered one.
        internal static void NotAConveniencePatch() { }
    }

    private static List<ConveniencePatch> ScanThisAssembly() =>
        ConveniencePatchScanner.Scan([typeof(ConveniencePatchScannerTests).Assembly]);

    private static List<ConveniencePatch> DiscoveredFrom(Type patchContainer) =>
        [.. ScanThisAssembly().Where(patch => patch.PatchMethod.DeclaringType == patchContainer)];

    [Test]
    public static void ScanFindsEveryAttributedMethod()
    {
        var discovered = DiscoveredFrom(typeof(ConveniencePatchFixtures));

        Assert.ThatCollection(discovered).Has.Count(3);
        var names = discovered.Select(d => d.Name).ToList();
        Assert.ThatCollection(names).Does.Contain("Skip it");
        Assert.ThatCollection(names).Does.Contain("Log return");
        Assert.ThatCollection(names).Does.Contain("Swallow exception");
    }

    [Test]
    public static void ScanMapsEachConveniencePatchTypeToItsOnTheFlyEquivalent()
    {
        var discovered = DiscoveredFrom(typeof(ConveniencePatchFixtures));

        var skipIt = discovered.Single(d => d.Name == "Skip it");
        Assert.That(skipIt.PatchType).Is.EqualTo(OnTheFlyPatchType.Prefix);

        var logReturn = discovered.Single(d => d.Name == "Log return");
        Assert.That(logReturn.PatchType).Is.EqualTo(OnTheFlyPatchType.Postfix);

        var swallow = discovered.Single(d => d.Name == "Swallow exception");
        Assert.That(swallow.PatchType).Is.EqualTo(OnTheFlyPatchType.Finalizer);
    }

    [Test]
    public static void ScanCarriesTheAttributesDescription()
    {
        var discovered = DiscoveredFrom(typeof(ConveniencePatchFixtures));

        var skipIt = discovered.Single(d => d.Name == "Skip it");
        Assert.That(skipIt.Description).Is.EqualTo("A prefix that skips the method.");
    }

    [Test]
    public static void ScanSkipsAMethodWithNoConveniencePatchAttribute()
    {
        var discovered = DiscoveredFrom(typeof(ConveniencePatchFixtures));

        Assert.That(discovered.Any(d => d.PatchMethod.Name == "NotAConveniencePatch")).Is.False();
    }

    [Test]
    public static void DebugAssistancesOwnBuiltInsAreAllDiscoverable()
    {
        var discovered = ConveniencePatchScanner.Scan([typeof(ConveniencePatchType).Assembly]);

        Assert.ThatCollection(discovered).Has.Count(7);
    }
}
