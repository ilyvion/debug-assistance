using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using DebugAssistance.HotPatch;
using DebugAssistance.Web;
using RimTestRedux;

namespace DebugAssistance.Tests.Web;

[TestSuite]
internal static class HotPatchEndpointsTests
{
    private readonly record struct Fixture(string AssemblyPath, string AssemblyName);

    private static int FindFreeTcpPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // Regression test for BUG-3: ApplyPatchRequestDto's Target/PatchMethod/SourceAssemblyPath are
    // C# `required`, but Newtonsoft.Json 13.0.1 (pinned in the .csproj, predates the C# 11
    // `required` keyword) doesn't enforce that at deserialization, so an omitted field deserializes
    // to null. Without an explicit null check, dereferencing them (or passing a null path into
    // LiveAssemblyLoader.GetLoaded, whose Dictionary lookup throws on a null key) threw an NRE/
    // ArgumentNullException caught only by the server's generic handler, yielding a 500 instead of
    // a clean 400. Goes through a real HttpListener/HttpClient round trip since HttpListenerContext
    // has no usable public constructor to build a request against ServeApplyPatch directly.
    [Test]
    public static void ApplyPatchReturnsBadRequestInsteadOfCrashingWhenRequiredFieldsAreMissing() =>
        Assert
            .ThatFunc(() =>
            {
                var port = FindFreeTcpPort();
                using var server = new DebugAssistanceServer(
                    port,
                    Path.GetTempPath(),
                    allowExternalConnections: false
                );

                var thread = new Thread(server.Start);
                thread.Start();
                Thread.Sleep(200);

                try
                {
                    using var client = new HttpClient();
                    var uri = new Uri($"http://localhost:{port}/api/hotpatch/apply");
                    using var response = client
                        .PostAsync(uri, new StringContent("{}", Encoding.UTF8, "application/json"))
                        .GetAwaiter()
                        .GetResult();

                    if (response.StatusCode != HttpStatusCode.BadRequest)
                    {
                        throw new InvalidOperationException(
                            $"Expected 400 Bad Request, got {(int)response.StatusCode}"
                        );
                    }
                }
                finally
                {
                    server.Stop();
                    _ = thread.Join(TimeSpan.FromSeconds(5));
                }
            })
            .Does.Not.Throw();

    // Mirrors MethodBrowserTests' own fixture-building approach (Reflection.Emit +
    // AssemblyBuilderAccess.Save, then a fresh Assembly.Load(byte[]) — the same kind of Assembly
    // instance LiveAssemblyLoader produces for a player's compiled DLL) so ResolveMethod is
    // exercised against a real Module rather than a hand-rolled fake.
    private static Fixture BuildFixtureAssembly(string? nameOverride = null)
    {
        var name = nameOverride ?? $"DAHotPatchEndpointsFixture_{Guid.NewGuid():N}";
        var assemblyName = new AssemblyName(name);
        var dir = Path.Combine(Path.GetTempPath(), "DebugAssistanceTests");
        _ = Directory.CreateDirectory(dir);
        var fileName = name + ".dll";

        var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Save,
            dir
        );
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name, fileName);

        var type = moduleBuilder.DefineType(
            "Fixture.Target",
            TypeAttributes.Public | TypeAttributes.Class
        );
        var method = type.DefineMethod(
            "DoTheThing",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            Type.EmptyTypes
        );
        method.GetILGenerator().Emit(OpCodes.Ret);
        _ = type.CreateType();

        assemblyBuilder.Save(fileName);

        return new Fixture(Path.Combine(dir, fileName), name);
    }

    private static Assembly LoadFixture(Fixture fixture) =>
        Assembly.Load(File.ReadAllBytes(fixture.AssemblyPath));

    // A second assembly, distinct from BuildFixtureAssembly's single-method one, with a type that
    // mixes a compatible (void-returning) and an incompatible (int-returning) method, a second type
    // with only an incompatible one, and a third with no methods at all -- what
    // CountMethods/HasCompatibleMethod/CountCompatibleTypes below need to tell a "some compatible",
    // a "none compatible", and an "empty" type apart from each other.
    private static Fixture BuildCompatibilityFixtureAssembly()
    {
        var name = $"DAHotPatchEndpointsCompatFixture_{Guid.NewGuid():N}";
        var assemblyName = new AssemblyName(name);
        var dir = Path.Combine(Path.GetTempPath(), "DebugAssistanceTests");
        _ = Directory.CreateDirectory(dir);
        var fileName = name + ".dll";

        var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Save,
            dir
        );
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name, fileName);

        var mixedType = moduleBuilder.DefineType(
            "Fixture.MixedType",
            TypeAttributes.Public | TypeAttributes.Class
        );
        DefineParameterlessStaticMethod(mixedType, "CompatibleMethod", typeof(void));
        DefineParameterlessStaticMethod(mixedType, "IncompatibleMethod", typeof(int));
        _ = mixedType.CreateType();

        var allIncompatibleType = moduleBuilder.DefineType(
            "Fixture.AllIncompatibleType",
            TypeAttributes.Public | TypeAttributes.Class
        );
        DefineParameterlessStaticMethod(allIncompatibleType, "OnlyIncompatible", typeof(int));
        _ = allIncompatibleType.CreateType();

        // An interface rather than a class: TypeBuilder auto-adds a default constructor to any
        // class left without one, so a class here would no longer be genuinely method-less now that
        // MethodBrowser reports constructors too. Interfaces can't have instance constructors, so
        // this keeps a true zero-member type to test the "nothing to pick" pruning against.
        var emptyType = moduleBuilder.DefineType(
            "Fixture.EmptyType",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract
        );
        _ = emptyType.CreateType();

        assemblyBuilder.Save(fileName);
        return new Fixture(Path.Combine(dir, fileName), name);
    }

    private static void DefineParameterlessStaticMethod(
        TypeBuilder type,
        string methodName,
        Type returnType
    )
    {
        var method = type.DefineMethod(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            returnType,
            Type.EmptyTypes
        );
        var il = method.GetILGenerator();
        if (returnType == typeof(void))
        {
            il.Emit(OpCodes.Ret);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
        }
    }

    [Test]
    public static void ResolveMethodFindsTheMethodTheTokenWasTakenFrom()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());
        var expected = assembly.GetType("Fixture.Target")!.GetMethod("DoTheThing")!;

        var resolved = HotPatchEndpoints.ResolveMethod(assembly, expected.MetadataToken);

        Assert.That(resolved is not null).Is.True();
        Assert.That(resolved!.Name).Is.EqualTo("DoTheThing");
        Assert.That(resolved.DeclaringType!.FullName).Is.EqualTo("Fixture.Target");
    }

    [Test]
    public static void ResolveMethodReturnsNullForAnUnknownTokenInsteadOfThrowing()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var resolved = HotPatchEndpoints.ResolveMethod(assembly, unchecked((int)0xFFFFFFFF));

        Assert.That(resolved is null).Is.True();
    }

    [Test]
    public static void ResolveMethodReturnsNullWhenTheAssemblyItselfIsNull() =>
        Assert.That(HotPatchEndpoints.ResolveMethod(null, 0) is null).Is.True();

    [Test]
    public static void ResolveAssemblyByFullNameFindsAnExactMatch()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());

        var resolved = HotPatchEndpoints.ResolveAssemblyByFullName(assembly.FullName);

        Assert.That(resolved == assembly).Is.True();
    }

    // The scenario ApplyPatchRequestDto's target resolution depends on: reloading a player's
    // assembly never unloads the previous Assembly instance on this runtime, so a stale load can
    // still be sitting in the AppDomain's assembly list under the exact same FullName as the fresh
    // one. AppDomain.GetAssemblies() appends new assemblies at the end, so picking the last match
    // must resolve to the one loaded second, not the first.
    [Test]
    public static void ResolveAssemblyByFullNamePrefersTheMostRecentlyLoadedMatch()
    {
        var sharedName = $"DAHotPatchEndpointsReload_{Guid.NewGuid():N}";
        var first = LoadFixture(BuildFixtureAssembly(sharedName));
        var second = LoadFixture(BuildFixtureAssembly(sharedName));
        Assert.That(first.FullName).Is.EqualTo(second.FullName);

        var resolved = HotPatchEndpoints.ResolveAssemblyByFullName(first.FullName);

        Assert.That(resolved == second).Is.True();
    }

    [Test]
    public static void ResolveAssemblyByFullNameReturnsNullWhenNothingMatches() =>
        Assert
            .That(
                HotPatchEndpoints.ResolveAssemblyByFullName($"NoSuchAssembly_{Guid.NewGuid():N}")
                    is null
            )
            .Is.True();

    [Test]
    public static void TryParsePatchTypeParsesEachSupportedName()
    {
        Assert.That(HotPatchEndpoints.TryParsePatchType("Prefix", out var prefix)).Is.True();
        Assert.That(prefix).Is.EqualTo(OnTheFlyPatchType.Prefix);

        Assert.That(HotPatchEndpoints.TryParsePatchType("Postfix", out var postfix)).Is.True();
        Assert.That(postfix).Is.EqualTo(OnTheFlyPatchType.Postfix);

        Assert
            .That(HotPatchEndpoints.TryParsePatchType("Transpiler", out var transpiler))
            .Is.True();
        Assert.That(transpiler).Is.EqualTo(OnTheFlyPatchType.Transpiler);

        Assert.That(HotPatchEndpoints.TryParsePatchType("Finalizer", out var finalizer)).Is.True();
        Assert.That(finalizer).Is.EqualTo(OnTheFlyPatchType.Finalizer);

        Assert.That(HotPatchEndpoints.TryParsePatchType("Replace", out var replace)).Is.True();
        Assert.That(replace).Is.EqualTo(OnTheFlyPatchType.Replace);
    }

    [Test]
    public static void TryParsePatchTypeRejectsAnUnsupportedOrMissingValue()
    {
        Assert.That(HotPatchEndpoints.TryParsePatchType("All", out _)).Is.False();
        Assert.That(HotPatchEndpoints.TryParsePatchType(null, out _)).Is.False();
    }

    [Test]
    public static void ToDtoShapesABrowsedMethodsFieldsForTheWireFormat()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());
        var type = assembly.GetType("Fixture.Target");
        var method = type.GetMethod("DoTheThing");
        var browsed = new BrowsedMethod(assembly, type, method);

        var dto = HotPatchEndpoints.ToDto(browsed);

        Assert.That(dto.AssemblyName).Is.EqualTo(assembly.GetName().Name);
        Assert.That(dto.AssemblyFullName).Is.EqualTo(assembly.FullName);
        Assert.That(dto.MetadataToken).Is.EqualTo(method.MetadataToken);
        Assert.That(dto.DeclaringTypeName).Is.EqualTo("Fixture.Target");
        Assert.That(dto.Namespace).Is.EqualTo("Fixture");
        Assert.That(dto.MethodName).Is.EqualTo("DoTheThing");
        Assert.That(dto.IsStatic).Is.True();
    }

    // A constructor's real Method.Name is ".ctor", which the frontend's match highlighting could
    // never find inside the "TypeName(...)" signature FormatSignature displays for it -- MethodName
    // must carry that same declaring-type name instead, not the raw CLR name.
    [Test]
    public static void ToDtoUsesTheDeclaringTypeNameAsMethodNameForAConstructor()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());
        var type = assembly.GetType("Fixture.Target");
        var ctor = type.GetConstructor(Type.EmptyTypes);
        var browsed = new BrowsedMethod(assembly, type, ctor);

        var dto = HotPatchEndpoints.ToDto(browsed);

        Assert.That(dto.MethodName).Is.EqualTo("Target");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SomeTarget() { }

#pragma warning disable IDE0051 // Used as a Harmony patch method by reflection in OnTheFlyPatch
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SomePostfix() { }
#pragma warning restore IDE0051

    [Test]
    public static void ToDtoShapesAnActivePatchesFieldsForTheWireFormat()
    {
        var target = AccessTools.Method(typeof(HotPatchEndpointsTests), nameof(SomeTarget));
        var patchMethod = AccessTools.Method(typeof(HotPatchEndpointsTests), nameof(SomePostfix));
        var patch = OnTheFlyPatch.Create(
            target,
            patchMethod,
            patchMethod,
            OnTheFlyPatchType.Postfix,
            "fixture.dll",
            2
        );

        var dto = HotPatchEndpoints.ToDto(patch);

        Assert.That(dto.Id).Is.EqualTo(patch.Id.ToString());
        Assert
            .That(dto.TargetDescription)
            .Is.EqualTo($"{typeof(HotPatchEndpointsTests).FullName}.SomeTarget");
        Assert
            .That(dto.PatchMethodDescription)
            .Is.EqualTo($"{typeof(HotPatchEndpointsTests).FullName}.SomePostfix");
        Assert.That(dto.PatchType).Is.EqualTo("Postfix");
        Assert.That(dto.SourceAssemblyPath).Is.EqualTo("fixture.dll");
        Assert
            .That(dto.SourceAssemblyName)
            .Is.EqualTo(typeof(HotPatchEndpointsTests).Assembly.GetName().Name);
        Assert.That(dto.SourceAssemblyGeneration).Is.EqualTo(2);
    }

    [Test]
    public static void ResolveCompatibilityFilterResolvesATargetAndPatchTypeWhenAllThreeQueryValuesAreValid()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());
        var target = assembly.GetType("Fixture.Target")!.GetMethod("DoTheThing")!;
        var query = new NameValueCollection
        {
            ["targetAssemblyFullName"] = assembly.FullName,
            ["targetMetadataToken"] = target.MetadataToken.ToString(CultureInfo.InvariantCulture),
            ["patchType"] = "Postfix",
        };

        var resolved = HotPatchEndpoints.ResolveCompatibilityFilter(query);

        Assert.That(resolved is not null).Is.True();
        Assert.That(resolved!.Value.Target == target).Is.True();
        Assert.That(resolved.Value.PatchType).Is.EqualTo(OnTheFlyPatchType.Postfix);
    }

    [Test]
    public static void ResolveCompatibilityFilterReturnsNullWhenAnyQueryValueIsMissingOrMalformed()
    {
        var assembly = LoadFixture(BuildFixtureAssembly());
        var target = assembly.GetType("Fixture.Target").GetMethod("DoTheThing");

        Assert
            .That(
                HotPatchEndpoints.ResolveCompatibilityFilter(
                    new NameValueCollection
                    {
                        ["targetMetadataToken"] = target.MetadataToken.ToString(
                            CultureInfo.InvariantCulture
                        ),
                        ["patchType"] = "Postfix",
                    }
                )
                    is null
            )
            .Is.True();
        Assert
            .That(
                HotPatchEndpoints.ResolveCompatibilityFilter(
                    new NameValueCollection
                    {
                        ["targetAssemblyFullName"] = assembly.FullName,
                        ["targetMetadataToken"] = "not-a-number",
                        ["patchType"] = "Postfix",
                    }
                )
                    is null
            )
            .Is.True();
        Assert
            .That(
                HotPatchEndpoints.ResolveCompatibilityFilter(
                    new NameValueCollection
                    {
                        ["targetAssemblyFullName"] = assembly.FullName,
                        ["targetMetadataToken"] = target.MetadataToken.ToString(
                            CultureInfo.InvariantCulture
                        ),
                        ["patchType"] = "NotAPatchType",
                    }
                )
                    is null
            )
            .Is.True();
        Assert
            .That(
                HotPatchEndpoints.ResolveCompatibilityFilter(
                    new NameValueCollection
                    {
                        ["targetAssemblyFullName"] = $"NoSuchAssembly_{Guid.NewGuid():N}",
                        ["targetMetadataToken"] = target.MetadataToken.ToString(
                            CultureInfo.InvariantCulture
                        ),
                        ["patchType"] = "Postfix",
                    }
                )
                    is null
            )
            .Is.True();
    }

    [Test]
    public static void CountMethodsReturnsTheTotalMethodCountWhenNoCompatibilityFilterIsGiven()
    {
        var assembly = LoadFixture(BuildCompatibilityFixtureAssembly());
        var type = assembly.GetType("Fixture.MixedType");

        // CompatibleMethod, IncompatibleMethod, and the implicit default constructor
        // TypeBuilder.CreateType() adds since MixedType never declares one of its own.
        Assert.That(HotPatchEndpoints.CountMethods(assembly, type, null)).Is.EqualTo(3);
    }

    [Test]
    public static void CountMethodsCountsOnlyCompatibleMethodsWhenAFilterIsGiven()
    {
        var assembly = LoadFixture(BuildCompatibilityFixtureAssembly());
        var type = assembly.GetType("Fixture.MixedType");
        var target = type.GetMethod("CompatibleMethod");

        var count = HotPatchEndpoints.CountMethods(
            assembly,
            type,
            (target, OnTheFlyPatchType.Prefix)
        );

        Assert.That(count).Is.EqualTo(1);
    }

    // A constructor can never serve as a Prefix/Postfix/Transpiler/Finalizer, so it must never be
    // offered by the patch-method picker regardless of what PatchCompatibility.IsCompatible would
    // otherwise say about a same-signature ordinary method (implicitly covered above too, since
    // MixedType's default constructor is excluded from that count(1), but this asserts it directly).
    [Test]
    public static void CountMethodsExcludesConstructorsWhenAFilterIsGiven()
    {
        var assembly = LoadFixture(BuildCompatibilityFixtureAssembly());
        var type = assembly.GetType("Fixture.MixedType");
        var target = type.GetMethod("CompatibleMethod");

        var methods = MethodBrowser.BrowseMethods(assembly, type);
        Assert.ThatCollection(methods.Select(m => m.Method.Name)).Does.Contain(".ctor");

        var count = HotPatchEndpoints.CountMethods(
            assembly,
            type,
            (target, OnTheFlyPatchType.Prefix)
        );

        Assert.That(count).Is.EqualTo(1);
    }

    [Test]
    public static void HasCompatibleMethodIsFalseForATypeWithNoCompatibleMethod()
    {
        var assembly = LoadFixture(BuildCompatibilityFixtureAssembly());
        var incompatibleType = assembly.GetType("Fixture.AllIncompatibleType");
        var target = assembly.GetType("Fixture.MixedType").GetMethod("CompatibleMethod");

        Assert
            .That(
                HotPatchEndpoints.HasCompatibleMethod(
                    assembly,
                    incompatibleType,
                    (target, OnTheFlyPatchType.Prefix)
                )
            )
            .Is.False();
    }

    [Test]
    public static void HasCompatibleMethodIsTrueForATypeWithAtLeastOneCompatibleMethod()
    {
        var assembly = LoadFixture(BuildCompatibilityFixtureAssembly());
        var mixedType = assembly.GetType("Fixture.MixedType");
        var target = mixedType.GetMethod("CompatibleMethod");

        Assert
            .That(
                HotPatchEndpoints.HasCompatibleMethod(
                    assembly,
                    mixedType,
                    (target, OnTheFlyPatchType.Prefix)
                )
            )
            .Is.True();
    }

    [Test]
    public static void CountCompatibleTypesExcludesTypesWithNoMethodsEvenWithoutAFilter()
    {
        var assembly = LoadFixture(BuildCompatibilityFixtureAssembly());

        // MixedType and AllIncompatibleType each have a method; EmptyType has none and is never
        // counted, filter or no filter -- there's nothing on it a player could ever pick.
        Assert.That(HotPatchEndpoints.CountCompatibleTypes(assembly, null)).Is.EqualTo(2);
    }

    [Test]
    public static void HasCompatibleMethodIsFalseForATypeWithNoMethodsAtAllEvenWithoutAFilter()
    {
        var assembly = LoadFixture(BuildCompatibilityFixtureAssembly());
        var emptyType = assembly.GetType("Fixture.EmptyType");

        Assert.That(HotPatchEndpoints.HasCompatibleMethod(assembly, emptyType, null)).Is.False();
    }

    [Test]
    public static void CountCompatibleTypesCountsOnlyTypesWithAtLeastOneCompatibleMethod()
    {
        var assembly = LoadFixture(BuildCompatibilityFixtureAssembly());
        var target = assembly.GetType("Fixture.MixedType").GetMethod("CompatibleMethod");

        var count = HotPatchEndpoints.CountCompatibleTypes(
            assembly,
            (target, OnTheFlyPatchType.Prefix)
        );

        Assert.That(count).Is.EqualTo(1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveManyEndpointTarget() { }

#pragma warning disable IDE0051 // Used as a Harmony patch method by reflection in HotPatchManager
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveManyEndpointPostfix() { }
#pragma warning restore IDE0051

    // Goes through a real HttpListener/HttpClient round trip, same reasoning as
    // ApplyPatchReturnsBadRequestInsteadOfCrashingWhenRequiredFieldsAreMissing above. Applies a real
    // patch via the module-wide DebugAssistanceMod.HotPatchManager (the same instance
    // ServeRemoveManyPatches reads from), then confirms the "Remove selected" round trip removes
    // exactly the requested id, leaves the other one active, and reports the unknown id as simply
    // not among RemovedIds instead of failing the whole request.
    [Test]
    public static void RemoveManyRemovesOnlyTheRequestedIdsAndSkipsUnknownOnes() =>
        Assert
            .ThatFunc(() =>
            {
                var target = AccessTools.Method(
                    typeof(HotPatchEndpointsTests),
                    nameof(RemoveManyEndpointTarget)
                );
                var patchMethod = AccessTools.Method(
                    typeof(HotPatchEndpointsTests),
                    nameof(RemoveManyEndpointPostfix)
                );
                var (patchToRemove, errorToRemove) = DebugAssistanceMod.HotPatchManager.Apply(
                    target,
                    patchMethod,
                    OnTheFlyPatchType.Postfix,
                    "fixture.dll",
                    1
                );
                var (patchToKeep, errorToKeep) = DebugAssistanceMod.HotPatchManager.Apply(
                    AccessTools.Method(typeof(HotPatchEndpointsTests), nameof(SomeTarget)),
                    AccessTools.Method(typeof(HotPatchEndpointsTests), nameof(SomePostfix)),
                    OnTheFlyPatchType.Postfix,
                    "fixture.dll",
                    1
                );
                if (errorToRemove is not null || errorToKeep is not null)
                {
                    throw new InvalidOperationException(
                        $"Setup patches failed to apply: {errorToRemove} / {errorToKeep}"
                    );
                }

                var port = FindFreeTcpPort();
                using var server = new DebugAssistanceServer(
                    port,
                    Path.GetTempPath(),
                    allowExternalConnections: false
                );

                var thread = new Thread(server.Start);
                thread.Start();
                Thread.Sleep(200);

                try
                {
                    using var client = new HttpClient();
                    var uri = new Uri($"http://localhost:{port}/api/hotpatch/remove-many");
                    var requestBody =
                        $$"""{"ids":["{{patchToRemove!.Id}}","{{Guid.NewGuid()}}"]}""";
                    using var response = client
                        .PostAsync(
                            uri,
                            new StringContent(requestBody, Encoding.UTF8, "application/json")
                        )
                        .GetAwaiter()
                        .GetResult();

                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new InvalidOperationException(
                            $"Expected 200 OK, got {(int)response.StatusCode}"
                        );
                    }

                    var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!json.Contains(patchToRemove.Id.ToString(), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Expected removedIds to contain {patchToRemove.Id}, got: {json}"
                        );
                    }

                    var stillActive = DebugAssistanceMod.HotPatchManager.ActivePatches;
                    if (stillActive.Any(p => p.Id == patchToRemove.Id))
                    {
                        throw new InvalidOperationException(
                            "Expected the requested patch to no longer be active"
                        );
                    }
                    if (!stillActive.Any(p => p.Id == patchToKeep!.Id))
                    {
                        throw new InvalidOperationException(
                            "Expected the patch not named in the request to remain active"
                        );
                    }
                }
                finally
                {
                    _ = DebugAssistanceMod.HotPatchManager.Remove(patchToKeep!);
                    server.Stop();
                    _ = thread.Join(TimeSpan.FromSeconds(5));
                }
            })
            .Does.Not.Throw();

    // A dozen distinctively-named methods for ServeMethodList's pagination test below -- more than
    // SearchPageSize (10), so a filter matching all of them exercises both a full first page and a
    // partial second page. Sits in this test assembly rather than a built fixture DLL since a
    // path-less search already covers every currently loaded assembly, this one included.
    private static class PaginationFixture
    {
        public static void DAPaginationFixtureMethod0() { }

        public static void DAPaginationFixtureMethod1() { }

        public static void DAPaginationFixtureMethod2() { }

        public static void DAPaginationFixtureMethod3() { }

        public static void DAPaginationFixtureMethod4() { }

        public static void DAPaginationFixtureMethod5() { }

        public static void DAPaginationFixtureMethod6() { }

        public static void DAPaginationFixtureMethod7() { }

        public static void DAPaginationFixtureMethod8() { }

        public static void DAPaginationFixtureMethod9() { }

        public static void DAPaginationFixtureMethod10() { }

        public static void DAPaginationFixtureMethod11() { }
    }

    [Test]
    public static void ServeMethodListPaginatesSearchResultsAndReportsTotalCountAndHasMore() =>
        Assert
            .ThatFunc(() =>
            {
                MethodSearchCache.Clear();
                var port = FindFreeTcpPort();
                using var server = new DebugAssistanceServer(
                    port,
                    Path.GetTempPath(),
                    allowExternalConnections: false
                );

                var thread = new Thread(server.Start);
                thread.Start();
                Thread.Sleep(200);

                try
                {
                    using var client = new HttpClient();

                    var firstPageJson = client
                        .GetStringAsync(
                            new Uri(
                                $"http://localhost:{port}/api/hotpatch/methods?filter=DAPaginationFixtureMethod"
                            )
                        )
                        .GetAwaiter()
                        .GetResult();

                    var firstPageMatchCount = Regex
                        .Matches(firstPageJson, "\"methodName\":\"DAPaginationFixtureMethod")
                        .Count;
                    if (firstPageMatchCount != HotPatchEndpoints.SearchPageSize)
                    {
                        throw new InvalidOperationException(
                            $"Expected {HotPatchEndpoints.SearchPageSize} methods on the first page, got {firstPageMatchCount}: {firstPageJson}"
                        );
                    }
                    if (!firstPageJson.Contains("\"totalCount\":12", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Expected totalCount 12, got: {firstPageJson}"
                        );
                    }
                    if (!firstPageJson.Contains("\"hasMore\":true", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Expected hasMore true, got: {firstPageJson}"
                        );
                    }

                    var secondPageJson = client
                        .GetStringAsync(
                            new Uri(
                                $"http://localhost:{port}/api/hotpatch/methods?filter=DAPaginationFixtureMethod&offset={HotPatchEndpoints.SearchPageSize}"
                            )
                        )
                        .GetAwaiter()
                        .GetResult();

                    var secondPageMatchCount = Regex
                        .Matches(secondPageJson, "\"methodName\":\"DAPaginationFixtureMethod")
                        .Count;
                    if (secondPageMatchCount != 2)
                    {
                        throw new InvalidOperationException(
                            $"Expected 2 methods on the second page, got {secondPageMatchCount}: {secondPageJson}"
                        );
                    }
                    if (!secondPageJson.Contains("\"hasMore\":false", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Expected hasMore false, got: {secondPageJson}"
                        );
                    }
                }
                finally
                {
                    server.Stop();
                    _ = thread.Join(TimeSpan.FromSeconds(5));
                }
            })
            .Does.Not.Throw();

    // Regression coverage for the cache key: an identical search must reuse MethodSearchCache's
    // entry across requests (verified directly against BuildSearchCacheKey here since the HTTP
    // endpoint gives no externally visible sign of a cache hit vs. miss), while a different filter,
    // path, or compatibility target must not collide with it.
    [Test]
    public static void BuildSearchCacheKeyMatchesOnlyForIdenticalSearchParameters()
    {
        var target = AccessTools.Method(typeof(HotPatchEndpointsTests), nameof(SomeTarget));

        var key = HotPatchEndpoints.BuildSearchCacheKey("/dev/patch.dll", "foo", null);
        var sameKey = HotPatchEndpoints.BuildSearchCacheKey("/dev/patch.dll", "foo", null);
        var differentFilter = HotPatchEndpoints.BuildSearchCacheKey(
            "/dev/patch.dll",
            "foobar",
            null
        );
        var differentPath = HotPatchEndpoints.BuildSearchCacheKey("/dev/other.dll", "foo", null);
        var withCompat = HotPatchEndpoints.BuildSearchCacheKey(
            "/dev/patch.dll",
            "foo",
            (target, OnTheFlyPatchType.Prefix)
        );

        Assert.That(key).Is.EqualTo(sameKey);
        Assert.That(key).Is.Not.EqualTo(differentFilter);
        Assert.That(key).Is.Not.EqualTo(differentPath);
        Assert.That(key).Is.Not.EqualTo(withCompat);
    }

    [Test]
    public static void DescribePatchCombinesThePatchTypeAndTheTargetsDescription()
    {
        var target = AccessTools.Method(typeof(HotPatchEndpointsTests), nameof(SomeTarget));
        var patchMethod = AccessTools.Method(typeof(HotPatchEndpointsTests), nameof(SomePostfix));
        var patch = OnTheFlyPatch.Create(
            target,
            patchMethod,
            patchMethod,
            OnTheFlyPatchType.Prefix,
            "fixture.dll",
            1
        );

        Assert
            .That(HotPatchEndpoints.DescribePatch(patch))
            .Is.EqualTo($"Prefix on {typeof(HotPatchEndpointsTests).FullName}.SomeTarget");
    }
}
