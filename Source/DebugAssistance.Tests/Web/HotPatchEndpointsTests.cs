using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection.Emit;
using System.Text;
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

        var emptyType = moduleBuilder.DefineType(
            "Fixture.EmptyType",
            TypeAttributes.Public | TypeAttributes.Class
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

        Assert.That(HotPatchEndpoints.CountMethods(assembly, type, null)).Is.EqualTo(2);
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
