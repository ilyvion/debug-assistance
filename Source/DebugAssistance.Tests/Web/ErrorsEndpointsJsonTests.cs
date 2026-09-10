using DebugAssistance.Capture;
using DebugAssistance.Web;
using RimTestRedux;

namespace DebugAssistance.Tests.Web;

[TestSuite]
internal static class ErrorsEndpointsJsonTests
{
    private sealed class GenericDisplayNameFixture<T>
    {
#pragma warning disable CA1822 // Mark members as static -- deliberately an instance method
        public void DoTheThing() { }
#pragma warning restore CA1822
    }

    [Test]
    public static void ToListEntryJsonShapesTheErrorsOwnFieldsAndTheTopFramesModName()
    {
        var frame = new CapturedStackFrame(
            "raw",
            declaringTypeName: null,
            methodName: null,
            fileName: null,
            lineNumber: null
        )
        {
            ResolvedModName = "SomeMod",
        };
        var exception = new CapturedError(
            "System.InvalidOperationException",
            "boom",
            "trace",
            [frame],
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        )
        {
            HarmonyRefHash = 0xABC,
        };

        var dto = ErrorsEndpoints.ToListEntryJson(exception);

        Assert.That(dto.DedupeKey).Is.EqualTo(exception.DedupeKey);
        Assert.That(dto.ErrorTypeName).Is.EqualTo("System.InvalidOperationException");
        Assert.That(dto.Message).Is.EqualTo("boom");
        Assert.That(dto.OccurrenceCount).Is.EqualTo(1);
        Assert.That(dto.HarmonyRefHash).Is.EqualTo(0xABC);
        Assert.That(dto.TopFrameModName).Is.EqualTo("SomeMod");
    }

    [Test]
    public static void ToListEntryJsonHasNoTopFrameModNameWhenThereAreNoFrames()
    {
        var exception = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            DateTime.UtcNow
        );

        var dto = ErrorsEndpoints.ToListEntryJson(exception);

        Assert.That(dto.TopFrameModName is null).Is.True();
    }

    [Test]
    public static void ToFrameJsonShapesTheFramesOwnFieldsAndIncludesItsIndex()
    {
        var frame = new CapturedStackFrame(
            "raw",
            declaringTypeName: "Some.Type",
            methodName: "Method",
            fileName: "Some.cs",
            lineNumber: 42,
            columnNumber: 7,
            ilOffset: 0x10
        )
        {
            ResolvedModName = "SomeMod",
            ResolvedAssemblyShortName = "SomeMod.dll",
        };

        var dto = ErrorsEndpoints.ToFrameJson(frame, 3);

        Assert.That(dto.Index).Is.EqualTo(3);
        Assert.That(dto.DeclaringTypeName).Is.EqualTo("Some.Type");
        Assert.That(dto.MethodName).Is.EqualTo("Method");
        Assert.That(dto.FileName).Is.EqualTo("Some.cs");
        Assert.That(dto.LineNumber).Is.EqualTo(42);
        Assert.That(dto.ColumnNumber).Is.EqualTo(7);
        Assert.That(dto.IlOffset).Is.EqualTo(0x10);
        Assert.That(dto.ResolvedModName).Is.EqualTo("SomeMod");
        Assert.That(dto.ResolvedAssemblyShortName).Is.EqualTo("SomeMod.dll");
        Assert.ThatCollection(dto.Patches).Has.Count(0);
    }

    [Test]
    public static void ToFrameJsonNestsPatchesWithTheirOwnIndices()
    {
        var patchMethod = typeof(ErrorsEndpointsJsonTests).GetMethod(
            nameof(ToFrameJsonNestsPatchesWithTheirOwnIndices)
        );
        var patch = new CapturedPatchFrame("SomeMod", "prefix", patchMethod);
        var frame = new CapturedStackFrame(
            "raw",
            declaringTypeName: null,
            methodName: null,
            fileName: null,
            lineNumber: null
        );
        frame.SetPatches([patch]);

        var dto = ErrorsEndpoints.ToFrameJson(frame, 0);

        Assert.ThatCollection(dto.Patches).Has.Count(1);
        Assert.That(dto.Patches[0].Index).Is.EqualTo(0);
        Assert.That(dto.Patches[0].OwnerModId).Is.EqualTo("SomeMod");
        Assert.That(dto.Patches[0].PatchKind).Is.EqualTo("prefix");
    }

    [Test]
    public static void ToFrameJsonIncludesAPatchTargetWhenTheFramesMethodResolvesToALiveMethod()
    {
        var frame = new CapturedStackFrame(
            "raw",
            declaringTypeName: typeof(ErrorsEndpointsJsonTests).FullName,
            methodName: nameof(
                ToFrameJsonIncludesAPatchTargetWhenTheFramesMethodResolvesToALiveMethod
            ),
            fileName: null,
            lineNumber: null
        )
        {
            Assembly = typeof(ErrorsEndpointsJsonTests).Assembly,
        };

        var dto = ErrorsEndpoints.ToFrameJson(frame, 0);

        Assert.That(dto.PatchTarget is not null).Is.True();
        Assert
            .That(dto.PatchTarget!.DeclaringTypeName)
            .Is.EqualTo(typeof(ErrorsEndpointsJsonTests).FullName);
        Assert
            .That(dto.PatchTarget.MethodName)
            .Is.EqualTo(
                nameof(ToFrameJsonIncludesAPatchTargetWhenTheFramesMethodResolvesToALiveMethod)
            );
    }

    // Regression coverage for the same CLR-notation problem fb408e1/CSharpTypeFormatter fixed
    // elsewhere: DeclaringTypeName (built from Type.FullName) is unreadable backtick/bracket
    // notation for a generic declaring type, but a resolved frame's DisplayName goes through
    // CSharpTypeFormatter instead and should come out as valid, readable C#.
    [Test]
    public static void ToFrameJsonDisplayNameRendersAGenericDeclaringTypeAsValidCSharp()
    {
        var declaringType = typeof(GenericDisplayNameFixture<string>);
        var frame = new CapturedStackFrame(
            "raw",
            declaringTypeName: declaringType.FullName,
            methodName: nameof(GenericDisplayNameFixture<>.DoTheThing),
            fileName: null,
            lineNumber: null
        )
        {
            Assembly = declaringType.Assembly,
        };

        var dto = ErrorsEndpoints.ToFrameJson(frame, 0);

        Assert
            .That(dto.DisplayName)
            .Is.EqualTo(
                "DebugAssistance.Tests.Web.ErrorsEndpointsJsonTests.GenericDisplayNameFixture<string>.DoTheThing"
            );
    }

    [Test]
    public static void ToFrameJsonHasNoPatchTargetWhenTheFrameHasNoDeclaringTypeOrMethod()
    {
        var frame = new CapturedStackFrame(
            "raw",
            declaringTypeName: null,
            methodName: null,
            fileName: null,
            lineNumber: null
        );

        var dto = ErrorsEndpoints.ToFrameJson(frame, 0);

        Assert.That(dto.PatchTarget is null).Is.True();
        Assert.That(dto.DisplayName is null).Is.True();
    }

    // Mirrors FrameDecompilerTests' "not currently loaded" case: a frame loaded from a save whose
    // resolved assembly short name no longer matches anything in the AppDomain must not offer a
    // patch target, exactly like it can't be decompiled either.
    [Test]
    public static void ToFrameJsonHasNoPatchTargetWhenTheFramesAssemblyIsNotCurrentlyLoaded()
    {
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

        var dto = ErrorsEndpoints.ToFrameJson(frame, 0);

        Assert.That(dto.PatchTarget is null).Is.True();
    }

    [Test]
    public static void ToPatchJsonShapesThePatchesOwnFieldsAndIncludesItsIndex()
    {
        var patchMethod = typeof(ErrorsEndpointsJsonTests).GetMethod(
            nameof(ToPatchJsonShapesThePatchesOwnFieldsAndIncludesItsIndex)
        );
        var patch = new CapturedPatchFrame("SomeMod", "postfix", patchMethod);

        var dto = ErrorsEndpoints.ToPatchJson(patch, 2);

        Assert.That(dto.Index).Is.EqualTo(2);
        Assert.That(dto.OwnerModId).Is.EqualTo("SomeMod");
        Assert.That(dto.PatchKind).Is.EqualTo("postfix");
        Assert.That(dto.DeclaringTypeName).Is.EqualTo(typeof(ErrorsEndpointsJsonTests).FullName);
        Assert
            .That(dto.MethodName)
            .Is.EqualTo(nameof(ToPatchJsonShapesThePatchesOwnFieldsAndIncludesItsIndex));
        Assert
            .That(dto.DisplayName)
            .Is.EqualTo(
                $"{typeof(ErrorsEndpointsJsonTests).FullName}.{nameof(ToPatchJsonShapesThePatchesOwnFieldsAndIncludesItsIndex)}"
            );
    }

    // Same generic-declaring-type regression as ToFrameJsonDisplayNameRendersAGenericDeclaringTypeAsValidCSharp,
    // exercised through the patch (CapturedPatchFrame.Method) path instead of the frame-resolution one.
    [Test]
    public static void ToPatchJsonDisplayNameRendersAGenericDeclaringTypeAsValidCSharp()
    {
        var declaringType = typeof(GenericDisplayNameFixture<string>);
        var patchMethod = declaringType.GetMethod(nameof(GenericDisplayNameFixture<>.DoTheThing));
        var patch = new CapturedPatchFrame("SomeMod", "prefix", patchMethod);

        var dto = ErrorsEndpoints.ToPatchJson(patch, 0);

        Assert
            .That(dto.DisplayName)
            .Is.EqualTo(
                "DebugAssistance.Tests.Web.ErrorsEndpointsJsonTests.GenericDisplayNameFixture<string>.DoTheThing"
            );
    }
}
