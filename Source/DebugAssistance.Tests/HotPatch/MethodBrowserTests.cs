using System.Reflection.Emit;
using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class MethodBrowserTests
{
    private readonly record struct Fixture(string AssemblyPath);

    // Builds a tiny real assembly on disk with: a base/derived pair (to check DeclaredOnly
    // behavior), a "Good" type with both a public static and a private instance method (to check
    // the BindingFlags cover both), a "Broken" type that claims to implement IDisposable but never
    // defines Dispose(), and an "AfterBroken" type declared after it (to prove enumeration
    // recovers and continues rather than stopping at the first failure).
    //
    // AssemblyBuilderAccess.Save (not RunAndSave, unlike FrameDecompilerTests' fixture) is
    // deliberate: a Save-only builder never needs to load Broken as a runnable type in this
    // process, so CreateType() only has to emit valid metadata for it, not pass CLR type-load
    // verification — that verification (and the ReflectionTypeLoadException MethodBrowser must
    // tolerate) only happens once the saved file is loaded fresh via Assembly.Load below.
    private static Fixture BuildFixtureAssembly()
    {
        var assemblyName = new AssemblyName($"DAMethodBrowserFixture_{Guid.NewGuid():N}");
        var dir = Path.Combine(Path.GetTempPath(), "DebugAssistanceTests");
        _ = Directory.CreateDirectory(dir);
        var fileName = assemblyName.Name + ".dll";

        var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Save,
            dir
        );
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name, fileName);

        var baseType = moduleBuilder.DefineType(
            "Fixture.Base",
            TypeAttributes.Public | TypeAttributes.Class
        );
        DefineParameterlessMethod(baseType, "BaseOnly", MethodAttributes.Public);
        var createdBaseType = baseType.CreateType();

        var derivedType = moduleBuilder.DefineType(
            "Fixture.Derived",
            TypeAttributes.Public | TypeAttributes.Class,
            createdBaseType
        );
        DefineParameterlessMethod(derivedType, "DerivedOnly", MethodAttributes.Public);
        _ = derivedType.CreateType();

        var goodType = moduleBuilder.DefineType(
            "Fixture.Good",
            TypeAttributes.Public | TypeAttributes.Class
        );
        DefineParameterlessMethod(
            goodType,
            "Alpha",
            MethodAttributes.Public | MethodAttributes.Static
        );
        DefineParameterlessMethod(goodType, "PrivateBeta", MethodAttributes.Private);
        _ = goodType.CreateType();

        var brokenType = moduleBuilder.DefineType(
            "Fixture.Broken",
            TypeAttributes.Public | TypeAttributes.Class
        );
        brokenType.AddInterfaceImplementation(typeof(IDisposable));
        _ = brokenType.CreateType();

        var afterBrokenType = moduleBuilder.DefineType(
            "Fixture.AfterBroken",
            TypeAttributes.Public | TypeAttributes.Class
        );
        DefineParameterlessMethod(
            afterBrokenType,
            "Gamma",
            MethodAttributes.Public | MethodAttributes.Static
        );
        _ = afterBrokenType.CreateType();

        assemblyBuilder.Save(fileName);

        return new Fixture(Path.Combine(dir, fileName));
    }

    private static void DefineParameterlessMethod(
        TypeBuilder type,
        string name,
        MethodAttributes attributes
    )
    {
        var method = type.DefineMethod(name, attributes, typeof(void), Type.EmptyTypes);
        method.GetILGenerator().Emit(OpCodes.Ret);
    }

    // Mirrors how LiveAssemblyLoader loads a player's assembly — Assembly.Load(byte[]), not
    // LoadFrom — so this exercises MethodBrowser against the same kind of Assembly instance it
    // will actually be given at runtime.
    private static Assembly LoadFixture(Fixture fixture) =>
        Assembly.Load(File.ReadAllBytes(fixture.AssemblyPath));

    [Test]
    public static void BrowseFindsPublicStaticAndPrivateInstanceMethods()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly]);

        Assert
            .That(results.Any(r => r.DeclaringType.Name == "Good" && r.Method.Name == "Alpha"))
            .Is.True();
        Assert
            .That(
                results.Any(r => r.DeclaringType.Name == "Good" && r.Method.Name == "PrivateBeta")
            )
            .Is.True();
    }

    [Test]
    public static void BrowseAppliesNameSubstringFilterCaseInsensitively()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly], "alpha");

        Assert.ThatCollection(results).Has.Count(1);
        Assert.That(results[0].Method.Name).Is.EqualTo("Alpha");
    }

    [Test]
    public static void BrowseWithoutADotAlsoMatchesTheDeclaringTypeName()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly], "Good");

        Assert.ThatCollection(results).Has.Count(2);
        var methodNames = results.Select(r => r.Method.Name).ToList();
        Assert.ThatCollection(methodNames).Does.Contain("Alpha");
        Assert.ThatCollection(methodNames).Does.Contain("PrivateBeta");
    }

    [Test]
    public static void BrowseWithADotRequiresBothTheTypeAndMethodPartToMatch()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly], "Good.Alpha");

        Assert.ThatCollection(results).Has.Count(1);
        Assert.That(results[0].DeclaringType.Name).Is.EqualTo("Good");
        Assert.That(results[0].Method.Name).Is.EqualTo("Alpha");
    }

    [Test]
    public static void BrowseWithADotFindsNothingWhenTheTypePartDoesNotMatch()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly], "Derived.Alpha");

        Assert.ThatCollection(results).Has.Count(0);
    }

    [Test]
    public static void BrowseOnlyReportsEachTypesOwnDeclaredMethodsNotInheritedOnes()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly]);

        Assert
            .That(
                results.Any(r => r.DeclaringType.Name == "Derived" && r.Method.Name == "BaseOnly")
            )
            .Is.False();
        Assert
            .That(results.Any(r => r.DeclaringType.Name == "Base" && r.Method.Name == "BaseOnly"))
            .Is.True();
    }

    // On this project's Mono runtime (tests only ever run in-game), a type that merely claims to
    // implement an interface without providing the method loads fine at GetTypes() time; the
    // failure only surfaces once something actually resolves
    // its vtable (confirmed empirically — .NET Framework fails immediately at GetTypes() for the
    // same kind of malformed type, Mono does not). So GetLoadableTypes must not throw either way,
    // but this can't assert Broken is filtered out here — Browse (below) is what actually needs to
    // tolerate its later failure, once something enumerates its members.
    [Test]
    public static void GetLoadableTypesReturnsEveryTypeInTheAssemblyWithoutThrowing()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var types = MethodBrowser.GetLoadableTypes(assembly).Select(t => t.Name).ToList();

        Assert.ThatCollection(types).Does.Contain("Good");
        Assert.ThatCollection(types).Does.Contain("AfterBroken");
        Assert.ThatCollection(types).Does.Contain("Base");
        Assert.ThatCollection(types).Does.Contain("Derived");
    }

    // The type-load failure for Broken (see BuildFixtureAssembly) only actually surfaces once
    // something enumerates its members (vtable resolution) — this is the real place MethodBrowser
    // must recover from it, one type at a time, without losing every other type's methods.
    [Test]
    public static void BrowseSkipsATypeThatFailsToLoadButKeepsMethodsFromOtherTypes()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly]);

        Assert.That(results.Any(r => r.DeclaringType.Name == "Broken")).Is.False();
        Assert
            .That(results.Any(r => r.DeclaringType.Name == "Good" && r.Method.Name == "Alpha"))
            .Is.True();
        Assert
            .That(
                results.Any(r => r.DeclaringType.Name == "AfterBroken" && r.Method.Name == "Gamma")
            )
            .Is.True();
    }

    [Test]
    public static void BrowseCombinesResultsFromMultipleAssemblies()
    {
        var assemblyA = LoadFixture(BuildFixtureAssembly());
        var assemblyB = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assemblyA, assemblyB], "Alpha");

        Assert.ThatCollection(results).Has.Count(2);
    }

    [Test]
    public static void BrowseNamespacesGroupsTypesByNamespaceWithCounts()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var namespaces = MethodBrowser.BrowseNamespaces(assembly);

        var fixtureNamespace = namespaces.Single(ns => ns.Name == "Fixture");
        Assert.That(fixtureNamespace.TypeCount).Is.EqualTo(5);
    }

    [Test]
    public static void BrowseTypesReturnsOnlyTypesDeclaredDirectlyUnderThatNamespace()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var types = MethodBrowser.BrowseTypes(assembly, "Fixture").Select(t => t.Name).ToList();

        Assert.ThatCollection(types).Does.Contain("Good");
        Assert.ThatCollection(types).Does.Contain("Base");
        Assert.ThatCollection(types).Does.Not.Contain("NoSuchType");
    }

    [Test]
    public static void BrowseTypesReturnsNothingForANamespaceWithNoTypes()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var types = MethodBrowser.BrowseTypes(assembly, "NoSuchNamespace");

        Assert.ThatCollection(types).Is.Empty();
    }

    [Test]
    public static void BrowseMethodsListsOnlyTheGivenTypesOwnDeclaredMethods()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());
        var goodType = assembly.GetType("Fixture.Good");

        var methods = MethodBrowser.BrowseMethods(assembly, goodType);

        Assert.ThatCollection(methods.Select(m => m.Method.Name)).Does.Contain("Alpha");
        Assert.ThatCollection(methods.Select(m => m.Method.Name)).Does.Contain("PrivateBeta");
        Assert.ThatCollection(methods).Has.Count(2);
    }

    private sealed class CustomNamespaceMarker;

    private static class SignatureFixtureMethods
    {
        public static bool WithNamedParams(int index, string label) =>
            index == 0 && label.Length == 0;

        public static void WithByRefParam(ref bool flag) => flag = !flag;

        public static void WithCustomTypeParam(CustomNamespaceMarker marker) => _ = marker;
    }

    [Test]
    public static void FormatSignatureIncludesEachParametersOwnName()
    {
        var method = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.WithNamedParams)
        );

        var signature = MethodBrowser.FormatSignature(method);

        Assert.That(signature).Is.EqualTo("Boolean WithNamedParams(Int32 index, String label)");
    }

    // MethodBase.ToString() renders ref parameters as "Type ByRef" -- this keeps that exact
    // wording so the picker's look doesn't change, while still naming the parameter.
    [Test]
    public static void FormatSignatureMarksAByRefParameterAndKeepsItsName()
    {
        var method = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.WithByRefParam)
        );

        var signature = MethodBrowser.FormatSignature(method);

        Assert.That(signature).Is.EqualTo("Void WithByRefParam(Boolean ByRef flag)");
    }

    [Test]
    public static void FormatSignatureKeepsTheFullNameForNonSystemParameterTypes()
    {
        var method = typeof(SignatureFixtureMethods).GetMethod(
            nameof(SignatureFixtureMethods.WithCustomTypeParam)
        );

        var signature = MethodBrowser.FormatSignature(method);

        Assert
            .That(signature)
            .Is.EqualTo($"Void WithCustomTypeParam({typeof(CustomNamespaceMarker)} marker)");
    }
}
