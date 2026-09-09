using System.Reflection.Emit;
using DebugAssistance.Capture;
using DebugAssistance.Decompilation;
using RimTestRedux;

namespace DebugAssistance.Tests.Decompilation;

[TestSuite]
internal static class FrameDecompilerTests
{
    private readonly record struct Fixture(
        string AssemblyPath,
        string TypeName,
        string MethodName,
        int Statement1Offset,
        int Statement2Offset
    );

    // Builds a tiny real assembly on disk with one method containing two distinct statements at
    // precisely known IL offsets, so FindHighlightLine's mapping can be checked against ground
    // truth instead of a magic number guessed from a C# compiler's own (build-config-dependent)
    // codegen. No PDB/debug info is emitted on purpose: CreateSequencePoints derives its mapping
    // purely from the raw IL, matching the common real-world case of a frame with no shipped PDB
    // at all.
    private static Fixture BuildFixture()
    {
        var assemblyName = new AssemblyName($"DADecompileFixture_{Guid.NewGuid():N}");
        var dir = Path.Combine(Path.GetTempPath(), "DebugAssistanceTests");
        _ = Directory.CreateDirectory(dir);
        var fileName = assemblyName.Name + ".dll";

        var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.RunAndSave,
            dir
        );
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name, fileName);
        var typeBuilder = moduleBuilder.DefineType("Fixture.Target", TypeAttributes.Public);

        // A no-op sink the "AddOne" body below calls between computing and returning its result —
        // without an intervening side effect using the local twice, the decompiler's own variable-
        // inlining transform collapses "int y = x + 1; return y;" into a single "return x + 1;"
        // line, leaving nothing distinct for the two offsets below to map to.
        var consumeBuilder = typeBuilder.DefineMethod(
            "Consume",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(int)]
        );
        consumeBuilder.GetILGenerator().Emit(OpCodes.Ret);

        var methodBuilder = typeBuilder.DefineMethod(
            "AddOne",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            [typeof(int)]
        );

        var il = methodBuilder.GetILGenerator();
        _ = il.DeclareLocal(typeof(int));

        // Statement 1: "int y = x + 1;"
        var statement1Offset = il.ILOffset;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc_0);

        // Statement 2: "Consume(y); return y;" — y is used twice, so the decompiler must keep it
        // as a real local rather than folding it back into a single expression.
        var statement2Offset = il.ILOffset;
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Call, consumeBuilder);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);

        _ = typeBuilder.CreateType();
        assemblyBuilder.Save(fileName);

        return new Fixture(
            Path.Combine(dir, fileName),
            "Fixture.Target",
            "AddOne",
            statement1Offset,
            statement2Offset
        );
    }

    // Fails with the actual Error text rather than a bare "expected True" — decompilation can
    // fail for several reasons (missing assembly, method not found, decompiler exception), and
    // a plain boolean assertion throws away exactly the detail needed to diagnose which one.
    private static void AssertSucceeded(DecompiledMethod result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Expected FrameDecompiler.Decompile to succeed but it failed: {result.Error}"
            );
        }
    }

    private static CapturedStackFrame FrameFor(
        Fixture fixture,
        Assembly assembly,
        int? ilOffset,
        string? fileName = null,
        int? lineNumber = null
    ) =>
        new(
            "raw",
            fixture.TypeName,
            fixture.MethodName,
            fileName: fileName,
            lineNumber: lineNumber,
            ilOffset: ilOffset
        )
        {
            Assembly = assembly,
        };

    [Test]
    public static void FindHighlightLineReturnsTheGreatestOffsetAtOrBeforeTarget()
    {
        List<(int Offset, int Line)> points = [(0, 10), (5, 11), (12, 12)];

        Assert.That(FrameDecompiler.FindHighlightLine(points, 5)!.Value).Is.EqualTo(11);
        // Offset 7 falls strictly between two recorded points — it still belongs to whichever
        // statement most recently started (11), not the next one (12).
        Assert.That(FrameDecompiler.FindHighlightLine(points, 7)!.Value).Is.EqualTo(11);
        Assert.That(FrameDecompiler.FindHighlightLine(points, 20)!.Value).Is.EqualTo(12);
    }

    [Test]
    public static void FindHighlightLineReturnsNullWhenTargetPrecedesEveryRecordedPoint()
    {
        List<(int Offset, int Line)> points = [(4, 10)];

        Assert.That(FrameDecompiler.FindHighlightLine(points, 0) is null).Is.True();
    }

    [Test]
    public static void DecompileMapsEachStatementsIlOffsetToItsOwnDistinctLine()
    {
        FrameDecompiler.ResetCacheForTests();
        var fixture = BuildFixture();
        var assembly = Assembly.LoadFrom(fixture.AssemblyPath);

        var statement1Result = FrameDecompiler.Decompile(
            FrameFor(fixture, assembly, fixture.Statement1Offset)
        );
        var statement2Result = FrameDecompiler.Decompile(
            FrameFor(fixture, assembly, fixture.Statement2Offset)
        );

        AssertSucceeded(statement1Result);
        AssertSucceeded(statement2Result);
        Assert
            .That(statement1Result.HighlightLine!.Value)
            .Is.LessThan(statement2Result.HighlightLine!.Value);
        Assert.That(statement1Result.Code!.Contains("AddOne", StringComparison.Ordinal)).Is.True();
        Assert.That(statement1Result.AssemblyPath).Is.EqualTo(assembly.Location);
    }

    [Test]
    public static void DecompileMapsAnOffsetBetweenTwoStatementsToTheEarlierOne()
    {
        FrameDecompiler.ResetCacheForTests();
        var fixture = BuildFixture();
        var assembly = Assembly.LoadFrom(fixture.AssemblyPath);

        var atStatement1 = FrameDecompiler.Decompile(
            FrameFor(fixture, assembly, fixture.Statement1Offset)
        );
        // One byte into statement 1's own instructions, still short of statement 2's offset.
        var midStatement1 = FrameDecompiler.Decompile(
            FrameFor(fixture, assembly, fixture.Statement1Offset + 1)
        );

        AssertSucceeded(atStatement1);
        AssertSucceeded(midStatement1);
        Assert
            .That(midStatement1.HighlightLine!.Value)
            .Is.EqualTo(atStatement1.HighlightLine!.Value);
    }

    // A PDB-reported file:line is display-only metadata and must never drive the
    // highlight/scroll target — only the IL offset, mapped against this mod's own decompiled
    // output, may do that. A frame carrying an (unrelated, deliberately wrong) file:line must
    // resolve to the exact same highlight line as one with no file:line at all.
    [Test]
    public static void DecompileIgnoresPdbFileAndLineAndUsesOnlyTheIlOffsetForTheHighlight()
    {
        FrameDecompiler.ResetCacheForTests();
        var fixture = BuildFixture();
        var assembly = Assembly.LoadFrom(fixture.AssemblyPath);

        var withoutFileInfo = FrameDecompiler.Decompile(
            FrameFor(fixture, assembly, fixture.Statement2Offset)
        );
        var withFileInfo = FrameDecompiler.Decompile(
            FrameFor(
                fixture,
                assembly,
                fixture.Statement2Offset,
                fileName: "/some/other/machine/Original.cs",
                lineNumber: 9999
            )
        );

        AssertSucceeded(withoutFileInfo);
        AssertSucceeded(withFileInfo);
        Assert
            .That(withFileInfo.HighlightLine!.Value)
            .Is.EqualTo(withoutFileInfo.HighlightLine!.Value);
    }

    // A tab renders at whatever (often huge) width the game's own font/skin gives it, so the
    // decompiler must be configured to indent with spaces instead — see FrameDecompiler's
    // IndentationString comment.
    [Test]
    public static void DecompileProducesCodeIndentedWithSpacesRatherThanTabs()
    {
        FrameDecompiler.ResetCacheForTests();
        var fixture = BuildFixture();
        var assembly = Assembly.LoadFrom(fixture.AssemblyPath);

        var result = FrameDecompiler.Decompile(
            FrameFor(fixture, assembly, fixture.Statement1Offset)
        );

        AssertSucceeded(result);
        Assert.That(result.Code!.Contains('\t', StringComparison.Ordinal)).Is.False();
    }

    [Test]
    public static void DecompileServesARepeatRequestForTheSameFrameFromCache()
    {
        FrameDecompiler.ResetCacheForTests();
        var fixture = BuildFixture();
        var assembly = Assembly.LoadFrom(fixture.AssemblyPath);
        var frame = FrameFor(fixture, assembly, fixture.Statement1Offset);

        _ = FrameDecompiler.Decompile(frame);
        _ = FrameDecompiler.Decompile(frame);

        Assert.That(FrameDecompiler.DecompileInvocationCountForTests).Is.EqualTo(1);
    }

    [Test]
    public static void DecompileFailsGracefullyWhenTheFrameHasNoDeclaringTypeOrMethod()
    {
        // Mirrors the dynamic-method/opaque-frame case where a frame has no declaring type or
        // method — there's nothing to look up, so this must return a Failed result, not throw.
        var frame = new CapturedStackFrame(
            "raw",
            declaringTypeName: null,
            methodName: null,
            fileName: null,
            lineNumber: null
        );

        var result = FrameDecompiler.Decompile(frame);

        Assert.That(result.Succeeded).Is.False();
    }

    [Test]
    public static void DecompileFailsGracefullyWhenTheAssemblyIsNotCurrentlyLoaded()
    {
        // A "mod uninstalled since capture" case: a loaded frame whose resolved
        // assembly short name doesn't match anything currently in the AppDomain.
        var frame = new CapturedStackFrame(
            "raw",
            "Some.Totally.Unknown.Type",
            "Method",
            fileName: null,
            lineNumber: null
        )
        {
            ResolvedAssemblyShortName = "ThisAssemblyDoesNotExistAnywhere",
        };

        var result = FrameDecompiler.Decompile(frame);

        Assert.That(result.Succeeded).Is.False();
        Assert.That(string.IsNullOrEmpty(result.Error)).Is.False();
    }

    // Regression test for a hot patch that shows up in a captured frame's patch list: its patch
    // method's assembly was loaded via LiveAssemblyLoader.Load, i.e. Assembly.Load(byte[]) rather
    // than LoadFrom, so Assembly.Location is empty even though it's very much loaded — decompiling
    // it must succeed by falling back to the path LiveAssemblyLoader loaded it from
    // (TryResolveMethodForDecompile), not report a false "not currently loaded".
    [Test]
    public static void DecompilePatchSucceedsForAHotPatchAssemblyLoadedFromBytesWithNoLocation()
    {
        FrameDecompiler.ResetCacheForTests();
        var fixture = BuildFixture();
        var assembly = DebugAssistanceMod.LiveAssemblyLoader.Load(fixture.AssemblyPath).Assembly;
        Assert.That(string.IsNullOrEmpty(assembly.Location)).Is.True();
        var method = assembly.GetType(fixture.TypeName)!.GetMethod(fixture.MethodName)!;
        var patch = new CapturedPatchFrame("ilyvion.debugassistance.hotpatch", "prefix", method);

        var result = FrameDecompiler.Decompile(patch);

        AssertSucceeded(result);
        Assert.That(result.AssemblyPath).Is.EqualTo(fixture.AssemblyPath);
        Assert.That(result.Code!.Contains("AddOne", StringComparison.Ordinal)).Is.True();
    }

    // Builds the same single-type/single-method shape BuildFixture does, at a caller-chosen path
    // (so a second call can simulate reloading the same on-disk file),
    // with a constant baked into the method body that differs between builds so the decompiled
    // text itself proves which build actually got decompiled.
    private static void BuildReloadableFixtureAt(string path, int constant)
    {
        var dir = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        var assemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(fileName));

        var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Save,
            dir
        );
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name, fileName);
        var typeBuilder = moduleBuilder.DefineType("Fixture.Target", TypeAttributes.Public);
        var methodBuilder = typeBuilder.DefineMethod(
            "AddConstant",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            [typeof(int)]
        );

        var il = methodBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, constant);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);

        _ = typeBuilder.CreateType();
        assemblyBuilder.Save(fileName);
    }

    // Regression test for OPP-001: FrameDecompiler.GetOrDecompile keys its cache purely on
    // (assemblyLocation, metadataToken), and reloading a path loads a rebuilt assembly from the
    // same path, keeping the same tokens for an unchanged type/method layout — so without
    // InvalidateCacheForAssemblyLocation, a decompile request after a reload would keep returning
    // the previous build's source.
    [Test]
    public static void InvalidateCacheForAssemblyLocationForcesADecompileOfTheReloadedBuild()
    {
        FrameDecompiler.ResetCacheForTests();
        var dir = Path.Combine(Path.GetTempPath(), "DebugAssistanceTests");
        _ = Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"DAReloadFixture_{Guid.NewGuid():N}.dll");

        BuildReloadableFixtureAt(path, constant: 1);
        var firstLoad = DebugAssistanceMod.LiveAssemblyLoader.Load(path).Assembly;
        var firstMethod = firstLoad.GetType("Fixture.Target")!.GetMethod("AddConstant")!;
        var firstResult = FrameDecompiler.Decompile(
            new CapturedPatchFrame("ilyvion.debugassistance.hotpatch", "prefix", firstMethod)
        );
        AssertSucceeded(firstResult);
        Assert.That(firstResult.Code!.Contains("+ 1", StringComparison.Ordinal)).Is.True();

        File.Delete(path);
        BuildReloadableFixtureAt(path, constant: 2);
        var reloaded = DebugAssistanceMod.LiveAssemblyLoader.Load(path);
        var reloadedMethod = reloaded.Assembly.GetType("Fixture.Target")!.GetMethod("AddConstant")!;
        Assert.That(reloadedMethod.MetadataToken).Is.EqualTo(firstMethod.MetadataToken);

        var staleResult = FrameDecompiler.Decompile(
            new CapturedPatchFrame("ilyvion.debugassistance.hotpatch", "prefix", reloadedMethod)
        );
        AssertSucceeded(staleResult);
        Assert.That(staleResult.Code!.Contains("+ 1", StringComparison.Ordinal)).Is.True();

        var countBeforeInvalidation = FrameDecompiler.DecompileInvocationCountForTests;
        FrameDecompiler.InvalidateCacheForAssemblyLocation(path);

        var freshResult = FrameDecompiler.Decompile(
            new CapturedPatchFrame("ilyvion.debugassistance.hotpatch", "prefix", reloadedMethod)
        );
        AssertSucceeded(freshResult);
        Assert.That(freshResult.Code!.Contains("+ 2", StringComparison.Ordinal)).Is.True();
        Assert
            .That(FrameDecompiler.DecompileInvocationCountForTests)
            .Is.EqualTo(countBeforeInvalidation + 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeMethodForPatchFallback() { }

    [Test]
    public static void DecompilePatchFallsBackToSearchingLoadedAssembliesWhenNoLiveMethodRemains()
    {
        FrameDecompiler.ResetCacheForTests();
        var method = typeof(FrameDecompilerTests).GetMethod(
            nameof(ProbeMethodForPatchFallback),
            BindingFlags.NonPublic | BindingFlags.Static
        );
        var patch = new CapturedPatchFrame("some.mod", "prefix", method)
        {
            // Simulates a reload from a save: the transient live Method reference is gone,
            // leaving only the persisted declaring-type/method-name strings behind.
            Method = null,
        };

        var result = FrameDecompiler.Decompile(patch);

        AssertSucceeded(result);
        Assert.That(result.HighlightLine is null).Is.True();
    }

    // Regression test: a CapturedPatchFrame captured before a player reloaded their hot-patch
    // assembly still holds a live MethodBase against that *previous* generation's Assembly
    // instance (Assembly.Load(byte[]) never actually goes away on the classic .NET/Mono runtime
    // RimWorld embeds). Reloading a path replaces _loadedByPath's entry for the path with the new
    // generation, so GetPathForAssembly must still be able to map the stale generation's instance
    // back to its on-disk path instead of falsely reporting it as "not currently loaded".
    [Test]
    public static void DecompilePatchSucceedsForAPreReloadGenerationOfAHotPatchAssembly()
    {
        FrameDecompiler.ResetCacheForTests();
        var dir = Path.Combine(Path.GetTempPath(), "DebugAssistanceTests");
        _ = Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"DAStaleGenerationFixture_{Guid.NewGuid():N}.dll");

        BuildReloadableFixtureAt(path, constant: 1);
        var firstLoad = DebugAssistanceMod.LiveAssemblyLoader.Load(path).Assembly;
        var firstMethod = firstLoad.GetType("Fixture.Target")!.GetMethod("AddConstant")!;

        File.Delete(path);
        BuildReloadableFixtureAt(path, constant: 2);
        _ = DebugAssistanceMod.LiveAssemblyLoader.Load(path);

        var patch = new CapturedPatchFrame(
            "ilyvion.debugassistance.hotpatch",
            "prefix",
            firstMethod
        );
        var result = FrameDecompiler.Decompile(patch);

        // Resolution succeeds and points at the right on-disk path — decompilation itself always
        // reads whatever is currently on disk there (see
        // InvalidateCacheForAssemblyLocationForcesADecompileOfTheReloadedBuild above), so this
        // shows the reloaded build's content ("+ 2"), not the stale generation's, regardless of
        // which generation's MethodBase resolved it.
        AssertSucceeded(result);
        Assert.That(result.AssemblyPath).Is.EqualTo(path);
        Assert.That(result.Code!.Contains("+ 2", StringComparison.Ordinal)).Is.True();
    }
}
