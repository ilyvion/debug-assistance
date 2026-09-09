using System.Reflection.Emit;
using DebugAssistance.Capture;
using DebugAssistance.Decompilation;
using DebugAssistance.PromptGeneration;
using RimTestRedux;

namespace DebugAssistance.Tests.PromptGeneration;

[TestSuite]
internal static class PromptTemplateTests
{
    private readonly record struct Fixture(
        string AssemblyPath,
        string TypeName,
        string MethodName,
        int IlOffset
    );

    // Builds a tiny real on-disk assembly with one method, so Build's per-frame
    // decompile-and-snippet step has a real IL-offset -> line mapping to resolve against instead
    // of a synthetic/mocked one.
    private static Fixture BuildFixture()
    {
        var assemblyName = new AssemblyName($"DAPromptFixture_{Guid.NewGuid():N}");
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

        var methodBuilder = typeBuilder.DefineMethod(
            "Broken",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            [typeof(int)]
        );

        var il = methodBuilder.GetILGenerator();
        _ = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc_0);
        var ilOffset = il.ILOffset;
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);

        _ = typeBuilder.CreateType();
        assemblyBuilder.Save(fileName);

        return new Fixture(Path.Combine(dir, fileName), "Fixture.Target", "Broken", ilOffset);
    }

    private static CapturedStackFrame FrameFor(
        Fixture fixture,
        Assembly assembly,
        string modName
    ) =>
        new(
            $"at {fixture.TypeName}.{fixture.MethodName} (Int32) [0x{fixture.IlOffset:x}]",
            fixture.TypeName,
            fixture.MethodName,
            fileName: null,
            lineNumber: null,
            ilOffset: fixture.IlOffset
        )
        {
            Assembly = assembly,
            ResolvedModName = modName,
            ResolvedAssemblyShortName = assembly.GetName().Name,
        };

    [Test]
    public static void BuildOmitsFrameDetailsSectionWhenThereAreNoFrames()
    {
        var exception = new CapturedError(
            "System.NullReferenceException",
            "Object reference not set",
            "at Foo.Bar()",
            [],
            DateTime.UtcNow
        );

        var prompt = PromptTemplate.Build(exception);

        Assert.That(prompt.Contains("## Error", StringComparison.Ordinal)).Is.True();
        Assert
            .That(prompt.Contains("System.NullReferenceException", StringComparison.Ordinal))
            .Is.True();
        Assert.That(prompt.Contains("## Frame details", StringComparison.Ordinal)).Is.False();
        Assert
            .That(prompt.Contains("## What I'd like help with", StringComparison.Ordinal))
            .Is.True();
    }

    [Test]
    public static void BuildAnnotatesAResolvedFrameAndIncludesADecompiledSnippetAroundTheHighlight()
    {
        FrameDecompiler.ResetCacheForTests();
        var fixture = BuildFixture();
        var assembly = Assembly.LoadFrom(fixture.AssemblyPath);
        var exception = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [FrameFor(fixture, assembly, "MyMod")],
            DateTime.UtcNow
        );

        var prompt = PromptTemplate.Build(exception);

        Assert
            .That(
                prompt.Contains($"[MyMod, {assembly.GetName().Name}.dll]", StringComparison.Ordinal)
            )
            .Is.True();
        Assert.That(prompt.Contains("## Frame details", StringComparison.Ordinal)).Is.True();
        Assert.That(prompt.Contains($"`{assembly.Location}`", StringComparison.Ordinal)).Is.True();
        Assert.That(prompt.Contains("```csharp", StringComparison.Ordinal)).Is.True();
        Assert.That(prompt.Contains(">>> ", StringComparison.Ordinal)).Is.True();
    }

    // Fallback path where FrameModResolver couldn't attribute the frame to any mod/assembly
    // (e.g. a dynamic-method/opaque frame). The prompt must still show something sensible rather
    // than a blank/null annotation, and decompilation must fail gracefully, not throw.
    [Test]
    public static void BuildAnnotatesAnUnresolvedFrameWithTheUnresolvableLabelAndNoAssembly()
    {
        var frame = new CapturedStackFrame(
            "at SomeDynamicMethod()",
            declaringTypeName: null,
            methodName: null,
            fileName: null,
            lineNumber: null
        )
        {
            ResolvedModName = FrameModResolver.UnresolvableLabel,
        };
        var exception = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [frame],
            DateTime.UtcNow
        );

        var prompt = PromptTemplate.Build(exception);

        Assert
            .That(
                prompt.Contains(
                    $"[{FrameModResolver.UnresolvableLabel}, ?]",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(prompt.Contains("Could not decompile this frame", StringComparison.Ordinal))
            .Is.True();
    }

    // Fallback: a frame whose resolved assembly isn't currently loaded (e.g. the mod was
    // removed/updated since capture) must produce a clear "not currently available" note rather
    // than crashing the whole prompt build.
    [Test]
    public static void BuildReportsSourceNotCurrentlyAvailableForAFrameWhoseAssemblyIsntLoaded()
    {
        var frame = new CapturedStackFrame(
            "at Some.Type.Method()",
            "Some.Type",
            "Method",
            fileName: null,
            lineNumber: null
        )
        {
            ResolvedModName = "SomeMod",
            ResolvedAssemblyShortName = "ThisAssemblyDoesNotExistAnywhere",
        };
        var exception = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [frame],
            DateTime.UtcNow
        );

        var prompt = PromptTemplate.Build(exception);

        Assert
            .That(prompt.Contains("Could not decompile this frame", StringComparison.Ordinal))
            .Is.True();
        Assert
            .That(prompt.Contains("not currently loaded", StringComparison.OrdinalIgnoreCase))
            .Is.True();
    }

    [Test]
    public static void BuildSnippetIncludesContextLinesAroundTheHighlightAndMarksItWithArrows()
    {
        var code = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line{i}"));

        var snippet = PromptTemplate.BuildSnippet(code, 5);

        var lines = snippet.TrimEnd('\n').Split('\n');
        // Lines 2..8 (5 minus/plus 3 lines of context), inclusive.
        Assert.That(lines.Length).Is.EqualTo(7);
        Assert.That(lines[3].StartsWith(">>> ", StringComparison.Ordinal)).Is.True();
        Assert.That(lines[3].Contains("line5", StringComparison.Ordinal)).Is.True();
    }

    [Test]
    public static void BuildSnippetClampsToTheStartAndEndOfTheCodeNearAnEdge()
    {
        var code = string.Join("\n", Enumerable.Range(1, 3).Select(i => $"line{i}"));

        var snippet = PromptTemplate.BuildSnippet(code, 1);

        var lines = snippet.TrimEnd('\n').Split('\n');
        Assert.That(lines.Length).Is.EqualTo(3);
        Assert.That(lines[0].StartsWith(">>> ", StringComparison.Ordinal)).Is.True();
    }

    [Test]
    public static void BuildSnippetFallsBackToTheStartOfTheCodeWhenNoHighlightLineResolved()
    {
        var code = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line{i}"));

        var snippet = PromptTemplate.BuildSnippet(code, null);

        Assert.That(snippet.Contains("line1", StringComparison.Ordinal)).Is.True();
        Assert.That(snippet.Contains(">>> ", StringComparison.Ordinal)).Is.False();
    }

    // The Hot Patch panel's debug-prompt button (only reachable once a project has actually been
    // scaffolded): same report body as Build, but ending on a concrete task pointing at that
    // project's directory instead of a blank heading for the player to fill in.
    [Test]
    public static void BuildHotPatchPromptIncludesTheReportBodyAndPointsAtTheGeneratedProject()
    {
        FrameDecompiler.ResetCacheForTests();
        var fixture = BuildFixture();
        var assembly = Assembly.LoadFrom(fixture.AssemblyPath);
        var exception = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [FrameFor(fixture, assembly, "MyMod")],
            DateTime.UtcNow
        );

        var prompt = PromptTemplate.BuildHotPatchPrompt(
            exception,
            "/dev/patches/Generated",
            targetMethodDescription: null
        );

        Assert.That(prompt.Contains("## Error", StringComparison.Ordinal)).Is.True();
        Assert.That(prompt.Contains("## Frame details", StringComparison.Ordinal)).Is.True();
        Assert.That(prompt.Contains("```csharp", StringComparison.Ordinal)).Is.True();
        Assert.That(prompt.Contains("## Task", StringComparison.Ordinal)).Is.True();
        Assert
            .That(prompt.Contains("`/dev/patches/Generated`", StringComparison.Ordinal))
            .Is.True();
        Assert
            .That(prompt.Contains("## What I'd like help with", StringComparison.Ordinal))
            .Is.False();
    }

    [Test]
    public static void BuildHotPatchPromptMentionsTheTargetMethodWhenOneWasGiven()
    {
        var exception = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            DateTime.UtcNow
        );

        var prompt = PromptTemplate.BuildHotPatchPrompt(
            exception,
            "/dev/patches/Generated",
            "Some.Type.Method"
        );

        Assert.That(prompt.Contains("`Some.Type.Method`", StringComparison.Ordinal)).Is.True();
    }

    [Test]
    public static void BuildHotPatchPromptOmitsTheTargetMethodMentionWhenNoneWasGiven()
    {
        var exception = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            DateTime.UtcNow
        );

        var prompt = PromptTemplate.BuildHotPatchPrompt(
            exception,
            "/dev/patches/Generated",
            targetMethodDescription: null
        );

        Assert
            .That(prompt.Contains("already set up to target", StringComparison.Ordinal))
            .Is.False();
    }
}
