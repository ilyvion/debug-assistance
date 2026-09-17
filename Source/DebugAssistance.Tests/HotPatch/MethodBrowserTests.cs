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
    // defines Dispose(), an "AfterBroken" type declared after it (to prove enumeration recovers
    // and continues rather than stopping at the first failure), and a "Ranked" type with methods
    // chosen to exercise Browse's match-quality ranking.
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

        // For ranking tests: "Kill" is an exact match for the filter "kill", "DoKillSideEffects"
        // only matches it at a camelCase word boundary, and "StartingPawnUtility" only matches a
        // "pawn" filter via a substring buried mid-word ("Spawn").
        var rankedType = moduleBuilder.DefineType(
            "Fixture.Ranked",
            TypeAttributes.Public | TypeAttributes.Class
        );
        DefineParameterlessMethod(
            rankedType,
            "Kill",
            MethodAttributes.Public | MethodAttributes.Static
        );
        DefineParameterlessMethod(
            rankedType,
            "DoKillSideEffects",
            MethodAttributes.Public | MethodAttributes.Static
        );
        _ = rankedType.CreateType();

        // For scope tests: a same-named "Kill" method on an unrelated type, so a scope that
        // narrows to Ranked can be checked to rank Ranked.Kill above this equally-scoring one.
        var otherType = moduleBuilder.DefineType(
            "Fixture.Other",
            TypeAttributes.Public | TypeAttributes.Class
        );
        DefineParameterlessMethod(
            otherType,
            "Kill",
            MethodAttributes.Public | MethodAttributes.Static
        );
        _ = otherType.CreateType();

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

        // "Good" gets an implicit default constructor from CreateType() alongside its two declared
        // methods, and MethodBrowser now reports constructors too.
        Assert.ThatCollection(results).Has.Count(3);
        var methodNames = results.Select(r => r.Method.Name).ToList();
        Assert.ThatCollection(methodNames).Does.Contain("Alpha");
        Assert.ThatCollection(methodNames).Does.Contain("PrivateBeta");
        Assert.ThatCollection(methodNames).Does.Contain(".ctor");
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

    // Constructors are named ".ctor" by the CLR, not the type's own name -- a dotted filter like
    // "Good.Good" must still match one, the same as a player typing a constructor call would
    // expect, matched against the same declaring-type name FormatSignature displays for it.
    [Test]
    public static void BrowseWithADotFindsAConstructorViaTheDeclaringTypeName()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly], "Good.Good");

        Assert.ThatCollection(results).Has.Count(1);
        Assert.That(results[0].Method.Name).Is.EqualTo(".ctor");
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
    public static void BrowseRanksAnExactMethodNameMatchAboveAWordBoundarySubstringMatch()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly], "kill");

        var killIndex = results.FindIndex(r => r.Method.Name == "Kill");
        var sideEffectsIndex = results.FindIndex(r => r.Method.Name == "DoKillSideEffects");
        Assert.That(killIndex).Is.LessThan(sideEffectsIndex);
    }

    [Test]
    public static void BrowseWithADotStillRanksAnExactMethodMatchAboveASubstringMatch()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        // Both "Kill" and "DoKillSideEffects" satisfy the dotted "Ranked.Kill" filter (the method
        // part is still a plain substring check), but the exact match should still come first.
        var results = MethodBrowser.Browse([assembly], "Ranked.Kill");

        Assert.ThatCollection(results).Has.Count(2);
        Assert.That(results[0].Method.Name).Is.EqualTo("Kill");
        Assert.That(results[1].Method.Name).Is.EqualTo("DoKillSideEffects");
    }

    [Test]
    public static void BrowseRanksATypeScopedMatchAboveAnEquallyScoringMatchOutsideIt()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse(
            [assembly],
            "Kill",
            new BrowseScope(assembly.FullName, null, "Fixture.Ranked")
        );

        var rankedIndex = results.FindIndex(r =>
            r.DeclaringType.FullName == "Fixture.Ranked" && r.Method.Name == "Kill"
        );
        var otherIndex = results.FindIndex(r =>
            r.DeclaringType.FullName == "Fixture.Other" && r.Method.Name == "Kill"
        );
        Assert.That(rankedIndex).Is.GreaterThan(-1);
        Assert.That(otherIndex).Is.GreaterThan(-1);
        Assert.That(rankedIndex).Is.LessThan(otherIndex);
    }

    [Test]
    public static void BrowseRanksAnAssemblyScopedMatchAboveAnEquallyScoringMatchInAnotherAssembly()
    {
        var assemblyA = LoadFixture(BuildFixtureAssembly());
        var assemblyB = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse(
            [assemblyA, assemblyB],
            "Alpha",
            new BrowseScope(assemblyB.FullName, null, null)
        );

        Assert.ThatCollection(results).Has.Count(2);
        Assert.That(results[0].Assembly.FullName).Is.EqualTo(assemblyB.FullName);
        Assert.That(results[1].Assembly.FullName).Is.EqualTo(assemblyA.FullName);
    }

    [Test]
    public static void BrowseWithNoScopeSelectedDoesNotReorderEquallyScoringMatches()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var results = MethodBrowser.Browse([assembly], "Kill");

        // Same fixture as the type-scoped test above, but with a default (unset) BrowseScope --
        // both "Kill" methods are exact matches, so their relative order falls back to the existing
        // declaring-type-name tiebreaker ("Other" before "Ranked" alphabetically) rather than
        // whichever scope happened to be selected.
        var otherIndex = results.FindIndex(r =>
            r.DeclaringType.FullName == "Fixture.Other" && r.Method.Name == "Kill"
        );
        var rankedIndex = results.FindIndex(r =>
            r.DeclaringType.FullName == "Fixture.Ranked" && r.Method.Name == "Kill"
        );
        Assert.That(otherIndex).Is.LessThan(rankedIndex);
    }

    [Test]
    public static void BrowseNamespacesGroupsTypesByNamespaceWithCounts()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var namespaces = MethodBrowser.BrowseNamespaces(assembly);

        var fixtureNamespace = namespaces.Single(ns => ns.Name == "Fixture");
        Assert.That(fixtureNamespace.TypeCount).Is.EqualTo(7);
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
        Assert.ThatCollection(methods.Select(m => m.Method.Name)).Does.Contain(".ctor");
        Assert.ThatCollection(methods).Has.Count(3);
    }

    [Test]
    public static void BrowseMethodsIncludesConstructorsAsConstructorInfo()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());
        var goodType = assembly.GetType("Fixture.Good");

        var methods = MethodBrowser.BrowseMethods(assembly, goodType);

        var ctor = methods.Single(m => m.Method.Name == ".ctor");
        Assert.That(ctor.Method is ConstructorInfo).Is.True();
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

#pragma warning disable IDE0290,IDE0060
    private sealed class WithConstructorParams
    {
        public WithConstructorParams(int index, string label) { }
    }
#pragma warning restore IDE0290,IDE0060

    // Constructors are named ".ctor" by the CLR -- showing that raw name in the picker would be
    // unreadable, so this uses the declaring type's own name instead, the way a player would write
    // a constructor call.
    [Test]
    public static void FormatSignatureUsesTheDeclaringTypeNameForAConstructor()
    {
        var ctor = typeof(WithConstructorParams).GetConstructor([typeof(int), typeof(string)]);

        var signature = MethodBrowser.FormatSignature(ctor);

        Assert.That(signature).Is.EqualTo("WithConstructorParams(Int32 index, String label)");
    }

#pragma warning disable CA1810 // an explicit static constructor is the point of this fixture
    private sealed class WithStaticConstructor
    {
        static WithStaticConstructor() { }
    }
#pragma warning restore CA1810

    // A type's static constructor and its parameterless instance constructor would otherwise
    // format identically ("WithStaticConstructor()" both), making them impossible to tell apart in
    // the picker -- "static" is what distinguishes them.
    [Test]
    public static void FormatSignatureMarksAStaticConstructorAsStatic()
    {
        var cctor = typeof(WithStaticConstructor).GetConstructor(
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null
        );

        var signature = MethodBrowser.FormatSignature(cctor);

        Assert.That(signature).Is.EqualTo("static WithStaticConstructor()");
    }
}
