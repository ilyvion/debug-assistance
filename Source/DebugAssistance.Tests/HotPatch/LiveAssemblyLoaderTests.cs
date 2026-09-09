using System.Reflection.Emit;
using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class LiveAssemblyLoaderTests
{
    private static string UniqueFixturePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebugAssistanceTests");
        _ = Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"DALiveAssemblyLoaderFixture_{Guid.NewGuid():N}.dll");
    }

    // A tiny real assembly containing exactly one uniquely-named, otherwise-empty type — enough
    // to tell two builds of "the same path, rebuilt" apart by which marker type they contain,
    // without needing anything Reflection.Emit-tricky (unlike MethodBrowserTests' broken-type
    // fixture, LiveAssemblyLoader itself never inspects types).
    private static byte[] BuildFixtureBytes(string markerTypeName)
    {
        var assemblyName = new AssemblyName($"DALiveAssemblyLoaderContent_{Guid.NewGuid():N}");
        var dir = Path.Combine(Path.GetTempPath(), "DebugAssistanceTests");
        _ = Directory.CreateDirectory(dir);
        var fileName = assemblyName.Name + ".dll";

        var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.RunAndSave,
            dir
        );
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name, fileName);
        var typeBuilder = moduleBuilder.DefineType(
            markerTypeName,
            TypeAttributes.Public | TypeAttributes.Class
        );
        _ = typeBuilder.CreateType();
        assemblyBuilder.Save(fileName);

        return File.ReadAllBytes(Path.Combine(dir, fileName));
    }

    [Test]
    public static void LoadReturnsAnAssemblyContainingTheExpectedMarkerType()
    {
        var path = UniqueFixturePath();
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.Marker"));
        var loader = new LiveAssemblyLoader();

        var loaded = loader.Load(path);

        Assert.That(loaded.Assembly.GetType("Fixture.Marker") is not null).Is.True();
    }

    [Test]
    public static void GetLoadedReturnsNullForAPathThatWasNeverLoaded()
    {
        var loader = new LiveAssemblyLoader();

        Assert.That(loader.GetLoaded(UniqueFixturePath()) is null).Is.True();
    }

    [Test]
    public static void GetLoadedReturnsTheMostRecentLoadForAPath()
    {
        var path = UniqueFixturePath();
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.Marker"));
        var loader = new LiveAssemblyLoader();

        var loaded = loader.Load(path);

        Assert
            .That(ReferenceEquals(loader.GetLoaded(path)!.Value.Assembly, loaded.Assembly))
            .Is.True();
    }

    // Assembly.Load(byte[]) (unlike LoadFrom) never memory-maps or locks the file it read from —
    // the whole point is that the player can rebuild the DLL mid-session without restarting
    // RimWorld. Overwriting it here would throw IOException if it were locked.
    [Test]
    public static void LoadDoesNotLockTheFileOnDiskForFurtherWrites()
    {
        var path = UniqueFixturePath();
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.Lockable"));
        var loader = new LiveAssemblyLoader();
        _ = loader.Load(path);

        Assert
            .ThatFunc(() => File.WriteAllBytes(path, BuildFixtureBytes("Fixture.Overwritten")))
            .Does.Not.Throw();
    }

    [Test]
    public static void LoadingTheSamePathTwiceReplacesTheAssemblyAndIncrementsTheGeneration()
    {
        var path = UniqueFixturePath();
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.First"));
        var loader = new LiveAssemblyLoader();
        var first = loader.Load(path);
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.Second"));

        var second = loader.Load(path);

        Assert.That(ReferenceEquals(second.Assembly, first.Assembly)).Is.False();
        Assert.That(second.Assembly.GetType("Fixture.Second") is not null).Is.True();
        Assert.That(second.Generation).Is.GreaterThan(first.Generation);
    }

    // The generation counter disambiguates reloads of the *same* path in the active-patch list
    // ("MyPatches#2") -- an unrelated path's own reloads must count separately rather than
    // sharing one global counter, or two different patch assemblies loaded once each would show
    // as #1 and #2 instead of both starting at #1.
    [Test]
    public static void EachPathCountsItsOwnGenerationsIndependently()
    {
        var pathA = UniqueFixturePath();
        var pathB = UniqueFixturePath();
        File.WriteAllBytes(pathA, BuildFixtureBytes("Fixture.A1"));
        File.WriteAllBytes(pathB, BuildFixtureBytes("Fixture.B1"));
        var loader = new LiveAssemblyLoader();

        var a1 = loader.Load(pathA);
        var b1 = loader.Load(pathB);
        File.WriteAllBytes(pathA, BuildFixtureBytes("Fixture.A2"));
        var a2 = loader.Load(pathA);

        Assert.That(a1.Generation).Is.EqualTo(1);
        Assert.That(b1.Generation).Is.EqualTo(1);
        Assert.That(a2.Generation).Is.EqualTo(2);
    }

    [Test]
    public static void GetAllLoadedListsEveryLoadedPathWithItsCurrentGeneration()
    {
        var pathA = UniqueFixturePath();
        var pathB = UniqueFixturePath();
        File.WriteAllBytes(pathA, BuildFixtureBytes("Fixture.A"));
        File.WriteAllBytes(pathB, BuildFixtureBytes("Fixture.B"));
        var loader = new LiveAssemblyLoader();
        var a = loader.Load(pathA);
        var b = loader.Load(pathB);

        var all = loader.GetAllLoaded();

        Assert.ThatCollection(all).Has.Count(2);
        Assert
            .That(all.Any(entry => entry.Path == pathA && entry.Loaded.Generation == a.Generation))
            .Is.True();
        Assert
            .That(all.Any(entry => entry.Path == pathB && entry.Loaded.Generation == b.Generation))
            .Is.True();
    }

    // Reloading a path overwrites GetAllLoaded's entry for it with the new generation rather than
    // appending a second entry -- the picker should only ever offer the current generation of a
    // given path, since browsing an older, superseded generation for new patches makes no sense.
    [Test]
    public static void GetAllLoadedReflectsOnlyTheMostRecentGenerationOfAReloadedPath()
    {
        var path = UniqueFixturePath();
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.First"));
        var loader = new LiveAssemblyLoader();
        _ = loader.Load(path);
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.Second"));
        var second = loader.Load(path);

        var all = loader.GetAllLoaded();

        Assert.ThatCollection(all).Has.Count(1);
        Assert.That(all[0].Loaded.Generation).Is.EqualTo(second.Generation);
    }

    [Test]
    public static void GetPathForAssemblyReturnsNullForAnAssemblyNeverLoadedThroughThisLoader()
    {
        var loader = new LiveAssemblyLoader();

        Assert.That(loader.GetPathForAssembly(typeof(object).Assembly) is null).Is.True();
    }

    [Test]
    public static void GetPathForAssemblyResolvesTheCurrentlyLoadedGeneration()
    {
        var path = UniqueFixturePath();
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.Marker"));
        var loader = new LiveAssemblyLoader();
        var loaded = loader.Load(path);

        Assert.That(loader.GetPathForAssembly(loaded.Assembly)).Is.EqualTo(path);
    }

    // A CapturedPatchFrame built before a reload can still hold a live MethodBase against the
    // *previous* generation's Assembly instance — see FrameDecompiler's own regression test for
    // why: the classic .NET/Mono runtime RimWorld embeds never actually unloads it. Loading a path
    // again overwrites GetLoaded's per-path entry with the new generation, but GetPathForAssembly
    // must still resolve the superseded instance back to its path rather than reporting it as gone.
    [Test]
    public static void GetPathForAssemblyStillResolvesAGenerationSupersededByAReload()
    {
        var path = UniqueFixturePath();
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.First"));
        var loader = new LiveAssemblyLoader();
        var first = loader.Load(path);
        File.WriteAllBytes(path, BuildFixtureBytes("Fixture.Second"));
        var second = loader.Load(path);

        Assert.That(loader.GetPathForAssembly(first.Assembly)).Is.EqualTo(path);
        Assert.That(loader.GetPathForAssembly(second.Assembly)).Is.EqualTo(path);
    }
}
