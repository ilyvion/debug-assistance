using DebugAssistance.Capture;
using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class ProjectScaffolderTests
{
    private sealed class SignatureFixtureMethods
    {
        public static void StaticVoidNoParams() { }

        public static bool StaticNonVoid(int index, ref bool flag) => flag && index == 0;

        public static List<string> StaticReturningAGenericType(List<int> indices) =>
            indices.ConvertAll(_ => "");

#pragma warning disable CA1822 // Mark members as static -- deliberately an instance method
        public bool InstanceNonVoid(string label) => label.Length == 0;
#pragma warning restore CA1822

        // Only ever reached as a generic argument (List<GenericArgumentType>), never a parameter
        // or return type directly -- exercises FlattenType's walk into generic type arguments.
        public sealed class GenericArgumentType;

#pragma warning disable IDE0060 // Remove unused parameter -- only its type matters to the test
        public static void StaticWithGenericArgumentTypeParameter(
            List<GenericArgumentType> items
        ) { }
#pragma warning restore IDE0060
    }

    // Short enough (unlike SignatureFixtureMethods above) that a suggested project name built from
    // it fits within GenText.IsValidFilename's 40-character cap without truncation.
    private sealed class Short
    {
        public static void Go() { }
    }

    private static string UniqueFixtureDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            "DebugAssistanceTests",
            $"ProjectScaffolder_{Guid.NewGuid():N}"
        );

    private static CapturedError FixtureError() =>
        new(
            "System.NullReferenceException",
            "Object reference not set to an instance of an object.",
            "at Fixture.Method () [0x00000] in <filename unknown>:0",
            [],
            DateTime.UtcNow
        );

    private static CapturedError FixtureErrorWithShortTypeName() =>
        new(
            "System.Foo",
            "Object reference not set to an instance of an object.",
            "at Fixture.Method () [0x00000] in <filename unknown>:0",
            [],
            DateTime.UtcNow
        );

    [Test]
    public static void ScaffoldRefusesAndWritesNothingWhenTheProjectSubdirectoryAlreadyHasFilesInIt()
    {
        var dir = UniqueFixtureDirectory();
        var projectDir = Path.Combine(dir, "MyPatch");
        _ = Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "existing.txt"), "keep me");

        var result = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        Assert.That(result.Success).Is.False();
        Assert.That(result.Error is not null).Is.True();
        Assert.ThatCollection(Directory.GetFileSystemEntries(projectDir)).Has.Count(1);
    }

    [Test]
    public static void ScaffoldSucceedsEvenWhenTheChosenDirectoryAlreadyHasOtherFilesInIt()
    {
        var dir = UniqueFixtureDirectory();
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "unrelated.txt"), "leave me alone");

        var result = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        Assert.That(result.Success).Is.True();
        Assert.That(File.Exists(Path.Combine(dir, "MyPatch", "MyPatch.csproj"))).Is.True();
        Assert.That(File.Exists(Path.Combine(dir, "unrelated.txt"))).Is.True();
    }

    [Test]
    public static void ScaffoldCreatesTheProjectSubdirectoryWhenItDoesNotExistYet()
    {
        var dir = UniqueFixtureDirectory();

        var result = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        Assert.That(result.Success).Is.True();
        Assert.That(Directory.Exists(Path.Combine(dir, "MyPatch"))).Is.True();
    }

    [Test]
    public static void ScaffoldReturnsTheProjectSubdirectoryItWroteInto()
    {
        var dir = UniqueFixtureDirectory();

        var result = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        Assert.That(result.ProjectDirectory).Is.EqualTo(Path.Combine(dir, "MyPatch"));
    }

    [Test]
    public static void ScaffoldReturnsWhereTheBuiltAssemblyWillEndUp()
    {
        var dir = UniqueFixtureDirectory();

        var result = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        Assert
            .That(result.ExpectedAssemblyPath)
            .Is.EqualTo(Path.Combine(dir, "MyPatch", "bin", "Debug", "net481", "MyPatch.dll"));
    }

    [Test]
    public static void ScaffoldRefusesAndWritesNothingForAnInvalidProjectName()
    {
        var dir = UniqueFixtureDirectory();

        var result = ProjectScaffolder.Scaffold(dir, "Invalid/Name*", null, null);

        Assert.That(result.Success).Is.False();
        Assert.That(Directory.Exists(dir)).Is.False();
    }

    [Test]
    public static void ScaffoldWritesTheFourExpectedFiles()
    {
        var dir = UniqueFixtureDirectory();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);
        var projectDir = Path.Combine(dir, "MyPatch");

        Assert.That(File.Exists(Path.Combine(projectDir, "MyPatch.csproj"))).Is.True();
        Assert.That(File.Exists(Path.Combine(projectDir, "GlobalUsings.cs"))).Is.True();
        Assert.That(File.Exists(Path.Combine(projectDir, ".gitignore"))).Is.True();
        Assert.That(File.Exists(Path.Combine(projectDir, "Patches.cs"))).Is.True();
    }

    [Test]
    public static void ScaffoldedCsprojReferencesEveryRequiredPackageAtItsPinnedVersion()
    {
        var dir = UniqueFixtureDirectory();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        var csproj = File.ReadAllText(Path.Combine(dir, "MyPatch", "MyPatch.csproj"));
        Assert
            .That(
                csproj.Contains(
                    "<TargetFramework>net481</TargetFramework>",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                csproj.Contains(
                    "<PackageReference Include=\"Krafs.Rimworld.Ref\" Version=\"1.6.*\">",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                csproj.Contains(
                    "<PackageReference Include=\"Lib.Harmony\" Version=\"2.4.2\">",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                csproj.Contains(
                    "<PackageReference Include=\"Krafs.Publicizer\" Version=\"2.2.1\">",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                csproj.Contains(
                    "<PackageReference Include=\"PolySharp\" Version=\"1.14.1\">",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                csproj.Contains("<ExcludeAssets>runtime</ExcludeAssets>", StringComparison.Ordinal)
            )
            .Is.True();
    }

    // DebugAssistance's own assembly is always referenced (so [ConveniencePatch] is available even
    // without a target method), but nothing target-dependent should be added without one.
    [Test]
    public static void ScaffoldedCsprojReferencesOnlyDebugAssistanceWithoutATargetMethod()
    {
        var dir = UniqueFixtureDirectory();
        var debugAssistanceAssembly = typeof(ProjectScaffolder).Assembly;

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        var csproj = File.ReadAllText(Path.Combine(dir, "MyPatch", "MyPatch.csproj"));
        var referenceTag = $"<Reference Include=\"{debugAssistanceAssembly.GetName().Name}\">";
        Assert.That(csproj.Contains(referenceTag, StringComparison.Ordinal)).Is.True();

        var index = 0;
        var count = 0;
        while (
            (index = csproj.IndexOf("<Reference Include=", index, StringComparison.Ordinal)) >= 0
        )
        {
            count++;
            index++;
        }
        Assert.That(count).Is.EqualTo(1);
    }

    // SignatureFixtureMethods lives in this test assembly, which -- like any third-party mod
    // assembly -- isn't covered by Krafs.Rimworld.Ref/Lib.Harmony, so its declaring type alone is
    // enough to require an explicit <Reference> for the scaffolded project to build.
    [Test]
    public static void ScaffoldedCsprojReferencesTheTargetsOwnThirdPartyAssembly()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticVoidNoParams)
        );
        var thisAssembly = typeof(SignatureFixtureMethods).Assembly;

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var csproj = File.ReadAllText(Path.Combine(dir, "MyPatch", "MyPatch.csproj"));
        Assert
            .That(
                csproj.Contains(
                    $"<Reference Include=\"{thisAssembly.GetName().Name}\">",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                csproj.Contains(
                    $"<HintPath>{thisAssembly.Location}</HintPath>",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert.That(File.Exists(thisAssembly.Location)).Is.True();
    }

    // The referenced mod assembly is already loaded into the running game, so it must not be
    // copied into the patch project's own output directory alongside the patch assembly itself.
    [Test]
    public static void ScaffoldedCsprojDoesNotCopyTheReferencedThirdPartyAssemblyToOutput()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticVoidNoParams)
        );

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var csproj = File.ReadAllText(Path.Combine(dir, "MyPatch", "MyPatch.csproj"));
        Assert
            .That(csproj.Contains("<Private>false</Private>", StringComparison.Ordinal))
            .Is.True();
    }

    // A mod type only ever reached through a generic argument (List<GenericArgumentType>, not a
    // parameter or return type directly) still needs its assembly referenced -- otherwise this
    // would need only a single <Reference> anyway, since it's the same assembly as the declaring
    // type, so this specifically counts occurrences to confirm the walk isn't emitting a duplicate.
    [Test]
    public static void ScaffoldedCsprojReferencesEachThirdPartyAssemblyOnlyOnce()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticWithGenericArgumentTypeParameter)
        );
        var thisAssembly = typeof(SignatureFixtureMethods).Assembly;

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var csproj = File.ReadAllText(Path.Combine(dir, "MyPatch", "MyPatch.csproj"));
        var referenceTag = $"<Reference Include=\"{thisAssembly.GetName().Name}\">";
        var occurrences = 0;
        var index = 0;
        while ((index = csproj.IndexOf(referenceTag, index, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
            index += referenceTag.Length;
        }
        Assert.That(occurrences).Is.EqualTo(1);
    }

    [Test]
    public static void ScaffoldedCsprojHasNoPublicizeItemGroupWithoutATargetMethod()
    {
        var dir = UniqueFixtureDirectory();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        var csproj = File.ReadAllText(Path.Combine(dir, "MyPatch", "MyPatch.csproj"));
        Assert.That(csproj.Contains("<Publicize Include=", StringComparison.Ordinal)).Is.False();
    }

    // Math and its Abs overload are both public BCL members, so referencing them needs no
    // Publicize entry.
    [Test]
    public static void ScaffoldedCsprojHasNoPublicizeItemGroupForAPublicTarget()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(Math).GetMethod(nameof(Math.Abs), [typeof(int)]);

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var csproj = File.ReadAllText(Path.Combine(dir, "MyPatch", "MyPatch.csproj"));
        Assert.That(csproj.Contains("<Publicize Include=", StringComparison.Ordinal)).Is.False();
    }

    // SignatureFixtureMethods is a private nested class, so its declaring type isn't visible
    // outside this test assembly -- the scaffolded project needs a whole-assembly <Publicize>
    // entry to compile a Prefix/Postfix that names it (e.g. as the __instance parameter type).
    [Test]
    public static void ScaffoldedCsprojPublicizesTheTargetsOwnAssemblyWhenItsTypeIsNotPublic()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticVoidNoParams)
        );
        var assemblyName = typeof(SignatureFixtureMethods).Assembly.GetName().Name;

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var csproj = File.ReadAllText(Path.Combine(dir, "MyPatch", "MyPatch.csproj"));
        Assert
            .That(
                csproj.Contains(
                    $"<Publicize Include=\"{assemblyName}\" />",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
    }

    // The declaring type and the generic argument type are both non-public and both live in this
    // same assembly, so the whole-assembly entry must appear once, not twice.
    [Test]
    public static void ScaffoldedCsprojPublicizesEachAssemblyOnlyOnce()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticWithGenericArgumentTypeParameter)
        );
        var assemblyName = typeof(SignatureFixtureMethods).Assembly.GetName().Name;

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var csproj = File.ReadAllText(Path.Combine(dir, "MyPatch", "MyPatch.csproj"));
        var publicizeTag = $"<Publicize Include=\"{assemblyName}\" />";
        var occurrences = 0;
        var index = 0;
        while ((index = csproj.IndexOf(publicizeTag, index, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
            index += publicizeTag.Length;
        }
        Assert.That(occurrences).Is.EqualTo(1);
    }

    [Test]
    public static void ScaffoldedGlobalUsingsCoversTheSameNamespacesAsTheRestOfTheRepo()
    {
        var dir = UniqueFixtureDirectory();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        var globalUsings = File.ReadAllText(Path.Combine(dir, "MyPatch", "GlobalUsings.cs"));
        Assert
            .That(globalUsings.Contains("global using HarmonyLib;", StringComparison.Ordinal))
            .Is.True();
        Assert
            .That(globalUsings.Contains("global using RimWorld;", StringComparison.Ordinal))
            .Is.True();
        Assert
            .That(globalUsings.Contains("global using UnityEngine;", StringComparison.Ordinal))
            .Is.True();
        Assert
            .That(globalUsings.Contains("global using Verse;", StringComparison.Ordinal))
            .Is.True();
    }

    [Test]
    public static void ScaffoldedGitignoreExcludesBuildOutputDirectories()
    {
        var dir = UniqueFixtureDirectory();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        var gitignore = File.ReadAllText(Path.Combine(dir, "MyPatch", ".gitignore"));
        Assert.That(gitignore.Contains("bin/", StringComparison.Ordinal)).Is.True();
        Assert.That(gitignore.Contains("obj/", StringComparison.Ordinal)).Is.True();
    }

    [Test]
    public static void ScaffoldedPatchesFileHasAGenericStubWhenNoTargetMethodIsGiven()
    {
        var dir = UniqueFixtureDirectory();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert
            .That(patches.Contains("internal static bool Prefix()", StringComparison.Ordinal))
            .Is.True();
        Assert
            .That(patches.Contains("internal static void Postfix()", StringComparison.Ordinal))
            .Is.True();
        Assert
            .That(
                patches.Contains(
                    "internal static IEnumerable<CodeInstruction> Transpiler(",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                patches.Contains(
                    "internal static Exception? Finalizer(Exception? __exception)",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
    }

    [Test]
    public static void ScaffoldedPatchesFileHasNoHeaderCommentWithoutACapturedError()
    {
        var dir = UniqueFixtureDirectory();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert
            .That(patches.Contains("// Generated from a captured", StringComparison.Ordinal))
            .Is.False();
    }

    [Test]
    public static void ScaffoldedPatchesFileNamesTheOriginatingErrorWhenGivenAContext()
    {
        var dir = UniqueFixtureDirectory();
        var context = FixtureError();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, context);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert
            .That(
                patches.Contains(
                    "// Generated from a captured System.NullReferenceException: Object reference not set to an instance of an object.",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
    }

    [Test]
    public static void ScaffoldedPrefixAndPostfixReflectAStaticVoidTargetsRealParameters()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticNonVoid)
        );

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert
            .That(
                patches.Contains(
                    "internal static bool Prefix(int index, ref bool flag)",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                patches.Contains(
                    "internal static void Postfix(int index, ref bool flag, ref bool __result)",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
    }

    [Test]
    public static void ScaffoldedSignaturesRenderGenericParameterAndReturnTypesAsValidCSharp()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticReturningAGenericType)
        );

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert
            .That(
                patches.Contains(
                    "internal static bool Prefix(System.Collections.Generic.List<int> indices)",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                patches.Contains(
                    "internal static void Postfix(System.Collections.Generic.List<int> indices, ref System.Collections.Generic.List<string> __result)",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
    }

    [Test]
    public static void ScaffoldedPrefixOmitsResultButPostfixIncludesItForANonVoidReturn()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticNonVoid)
        );

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert.That(patches.Contains("ref bool __result)", StringComparison.Ordinal)).Is.True();
        Assert
            .That(
                patches.Contains(
                    "internal static bool Prefix(int index, ref bool flag, ref",
                    StringComparison.Ordinal
                )
            )
            .Is.False();
    }

    [Test]
    public static void ScaffoldedPrefixAndPostfixOmitResultForAVoidReturningTarget()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticVoidNoParams)
        );

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert
            .That(patches.Contains("internal static bool Prefix()", StringComparison.Ordinal))
            .Is.True();
        Assert
            .That(patches.Contains("internal static void Postfix()", StringComparison.Ordinal))
            .Is.True();
        Assert.That(patches.Contains("__result", StringComparison.Ordinal)).Is.False();
    }

    [Test]
    public static void ScaffoldedPrefixAndPostfixIncludeInstanceForAnInstanceTarget()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.InstanceNonVoid)
        );
        var declaringTypeName = typeof(SignatureFixtureMethods).FullName.Replace('+', '.');

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert
            .That(
                patches.Contains(
                    $"internal static bool Prefix({declaringTypeName} __instance, string label)",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
        Assert
            .That(
                patches.Contains(
                    $"internal static void Postfix({declaringTypeName} __instance, string label, ref bool __result)",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
    }

    [Test]
    public static void ScaffoldedPatchesFileHasNoHarmonyPatchAttributeWithoutATargetMethod()
    {
        var dir = UniqueFixtureDirectory();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert.That(patches.Contains("[HarmonyPatch(", StringComparison.Ordinal)).Is.False();
    }

    [Test]
    public static void ScaffoldedPatchesFileCarriesAClassLevelHarmonyPatchAttributeForAMethodTarget()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticVoidNoParams)
        );
        var declaringTypeName = typeof(SignatureFixtureMethods).FullName.Replace('+', '.');

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert
            .That(
                patches.Contains(
                    $"[HarmonyPatch(typeof({declaringTypeName}), nameof({declaringTypeName}.StaticVoidNoParams))]",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
    }

    [Test]
    public static void ScaffoldedPatchesFileCarriesAConstructorTargetedHarmonyPatchAttribute()
    {
        var dir = UniqueFixtureDirectory();
        var target = typeof(SignatureFixtureMethods).GetConstructor(Type.EmptyTypes);
        var declaringTypeName = typeof(SignatureFixtureMethods).FullName.Replace('+', '.');

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", target, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert
            .That(
                patches.Contains(
                    $"[HarmonyPatch(typeof({declaringTypeName}), MethodType.Constructor)]",
                    StringComparison.Ordinal
                )
            )
            .Is.True();
    }

    [Test]
    public static void ScaffoldedPatchesFileCarriesAllFourHarmonyPatchTypeAttributesRegardlessOfTarget()
    {
        var dir = UniqueFixtureDirectory();

        _ = ProjectScaffolder.Scaffold(dir, "MyPatch", null, null);

        var patches = File.ReadAllText(Path.Combine(dir, "MyPatch", "Patches.cs"));
        Assert.That(patches.Contains("[HarmonyPrefix]", StringComparison.Ordinal)).Is.True();
        Assert.That(patches.Contains("[HarmonyPostfix]", StringComparison.Ordinal)).Is.True();
        Assert.That(patches.Contains("[HarmonyTranspiler]", StringComparison.Ordinal)).Is.True();
        Assert.That(patches.Contains("[HarmonyFinalizer]", StringComparison.Ordinal)).Is.True();
    }

    [Test]
    public static void SuggestProjectNameReturnsDefaultWithoutATargetMethod() =>
        Assert
            .That(ProjectScaffolder.SuggestProjectName(null, null))
            .Is.EqualTo(ProjectScaffolder.DefaultProjectName);

    [Test]
    public static void SuggestProjectNameReturnsDefaultWithoutATargetMethodEvenWithContext() =>
        Assert
            .That(ProjectScaffolder.SuggestProjectName(null, FixtureError()))
            .Is.EqualTo(ProjectScaffolder.DefaultProjectName);

    [Test]
    public static void SuggestProjectNameIncludesTheSanitizedTypeAndMethodNameForAFrame()
    {
        var target = typeof(Short).GetMethod(nameof(Short.Go));

        var name = ProjectScaffolder.SuggestProjectName(target, null);

        Assert.That(name).Is.EqualTo("DebugAssistancePatch_Short_Go");
    }

    [Test]
    public static void SuggestProjectNameNeverExceedsGenTextsFortyCharacterLimit()
    {
        var target = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.StaticVoidNoParams)
        );

        var name = ProjectScaffolder.SuggestProjectName(target, FixtureError());

        Assert.That(name.Length <= 40).Is.True();
        Assert.That(GenText.IsValidFilename(name)).Is.True();
        Assert.That(name.EndsWith('_')).Is.False();
    }

    [Test]
    public static void SuggestProjectNameAppendsTheShortErrorTypeNameWhenGivenAContext()
    {
        var target = typeof(Short).GetMethod(nameof(Short.Go));

        var name = ProjectScaffolder.SuggestProjectName(target, FixtureErrorWithShortTypeName());

        Assert.That(name).Is.EqualTo("DebugAssistancePatch_Short_Go_Foo");
    }
}
