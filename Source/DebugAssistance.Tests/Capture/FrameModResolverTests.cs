using System.Diagnostics;
using DebugAssistance.Capture;
using RimTestRedux;

namespace DebugAssistance.Tests.Capture;

[TestSuite]
internal static class FrameModResolverTests
{
    [Test]
    public static void ResolveLiveFrameFallsBackToAssemblyResolutionForAnUnpatchedMethod()
    {
        var stackFrame = new StackTrace(fNeedFileInfo: false).GetFrame(0)!;
        var liveFrame = new CapturedStackFrame("raw", null, null, fileName: null, lineNumber: null);
        var parsedFrame = new CapturedStackFrame(
            "raw",
            typeof(FrameModResolverTests).FullName,
            nameof(ResolveLiveFrameFallsBackToAssemblyResolutionForAnUnpatchedMethod),
            fileName: null,
            lineNumber: null
        );

        FrameModResolver.ResolveLiveFrame(liveFrame, stackFrame);
        FrameModResolver.ResolveParsedFrame(parsedFrame);

        // A method Harmony has never patched must resolve the same way through both paths —
        // in particular, never the empty string GetPatchInfo(unpatched-method)?.Owners produces.
        Assert.That(string.IsNullOrEmpty(liveFrame.ResolvedModName)).Is.False();
        Assert.That(liveFrame.ResolvedModName).Is.EqualTo(parsedFrame.ResolvedModName!);
    }

    // NoInlining: a trivial one-liner like this is a prime JIT-inlining candidate, and an inlined
    // call site bypasses Harmony's patch entirely — the JIT resolves it against the original IL
    // before harmony.Patch ever runs, collapsing the exception's captured stack down to just the
    // caller and leaving no trampoline frame for GetOriginalMethodFromStackframe to find.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MethodToPatch() =>
        throw new InvalidOperationException("FrameModResolverTests probe exception");

#pragma warning disable IDE0051 // Used as a Harmony patch method, invoked by reflection
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NoOpPrefix() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NoOpPostfix() { }
#pragma warning restore IDE0051

    [Test]
    public static void ResolveLiveFrameAttributesAPatchedMethodToItsOriginalNotTheTrampoline()
    {
        var harmony = new Harmony("test.debugassistance.framemodresolvertests");
        try
        {
            var original = AccessTools.Method(typeof(FrameModResolverTests), nameof(MethodToPatch));
            _ = harmony.Patch(
                original,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(FrameModResolverTests), nameof(NoOpPrefix))
                ),
                postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(FrameModResolverTests), nameof(NoOpPostfix))
                )
            );

            StackFrame[] capturedFrames = [];
            try
            {
                MethodToPatch();
            }
            catch (InvalidOperationException ex)
            {
                capturedFrames = new StackTrace(ex, fNeedFileInfo: false).GetFrames() ?? [];
            }

            // The merged prefix/original/postfix trampoline is whichever captured frame Harmony
            // itself maps back to `original` — not necessarily frame 0, and not identifiable by
            // name/DeclaringType alone, since that's exactly the information this frame is
            // missing before resolution.
            var patchedFrame =
                capturedFrames.FirstOrDefault(candidate =>
                    ReferenceEquals(Harmony.GetOriginalMethodFromStackframe(candidate), original)
                )
                ?? throw new InvalidOperationException(
                    "No captured frame mapped back to the patched original method. Frames seen: "
                        + string.Join(
                            " | ",
                            capturedFrames.Select(f =>
                                $"{f.GetMethod()?.DeclaringType?.FullName}.{f.GetMethod()?.Name}"
                            )
                        )
                );

            var frame = new CapturedStackFrame(
                "raw",
                null,
                null,
                fileName: null,
                lineNumber: null,
                ilOffset: patchedFrame.GetILOffset()
            );
            FrameModResolver.ResolveLiveFrame(frame, patchedFrame);

            Assert.That(frame.DeclaringTypeName).Is.EqualTo(typeof(FrameModResolverTests).FullName);
            Assert.That(frame.MethodName).Is.EqualTo(nameof(MethodToPatch));
            // Resolving to the original method's identity must not discard the captured offset,
            // even though it was measured against the trampoline's own IL layout and may
            // therefore highlight the wrong line — a plausibly-wrong highlight beats none.
            Assert.That(frame.IlOffset).Is.EqualTo(patchedFrame.GetILOffset());
            // Attribution belongs to the original method's own assembly, never to whichever
            // mod(s) patched it — that's what the Patches entries below are for instead.
            Assert
                .That(frame.ResolvedModName != "test.debugassistance.framemodresolvertests")
                .Is.True();
            Assert.That(frame.Patches.Any(p => p.PatchKind == "prefix")).Is.True();
            Assert.That(frame.Patches.Any(p => p.PatchKind == "postfix")).Is.True();
            Assert
                .That(
                    frame.Patches.All(p =>
                        p.OwnerModId == "test.debugassistance.framemodresolvertests"
                    )
                )
                .Is.True();
        }
        finally
        {
            harmony.UnpatchAll("test.debugassistance.framemodresolvertests");
        }
    }

    [Test]
    public static void BuildAssemblyToModNameMapMapsEachAssemblyToItsOwningMod()
    {
        var modAssembly = typeof(FrameModResolverTests).Assembly;
        var otherAssembly = typeof(object).Assembly;
        (string ModName, IEnumerable<Assembly> Assemblies)[] mods =
        [
            ("Debug Assistance", [modAssembly]),
            ("mscorlib", [otherAssembly]),
        ];

        var map = FrameModResolver.BuildAssemblyToModNameMap(mods);

        Assert.That(map[modAssembly]).Is.EqualTo("Debug Assistance");
        Assert.That(map[otherAssembly]).Is.EqualTo("mscorlib");
    }

    [Test]
    public static void BuildAssemblyToModNameMapFirstModWinsWhenAnAssemblyIsClaimedTwice()
    {
        var assembly = typeof(FrameModResolverTests).Assembly;
        (string ModName, IEnumerable<Assembly> Assemblies)[] mods =
        [
            ("First", [assembly]),
            ("Second", [assembly]),
        ];

        var map = FrameModResolver.BuildAssemblyToModNameMap(mods);

        Assert.That(map[assembly]).Is.EqualTo("First");
    }

    [Test]
    public static void BuildAssemblyToModNameMapIsEmptyForNoMods()
    {
        var map = FrameModResolver.BuildAssemblyToModNameMap([]);

        Assert.ThatCollection(map).Is.Empty();
    }

    [Test]
    public static void ClassifyFrameworkAssemblyResolvesTheBaseGameAssemblyToRimWorld()
    {
        var name = FrameModResolver.ClassifyFrameworkAssembly(typeof(Game).Assembly);

        Assert.That(name).Is.EqualTo("RimWorld");
    }

    [Test]
    public static void ClassifyFrameworkAssemblyResolvesAnyUnityEngineModuleToUnity()
    {
        var name = FrameModResolver.ClassifyFrameworkAssembly(typeof(Rect).Assembly);

        Assert.That(name).Is.EqualTo("Unity");
    }

    [Test]
    public static void ClassifyFrameworkAssemblyResolvesTheBclToDotNetRuntime()
    {
        var name = FrameModResolver.ClassifyFrameworkAssembly(typeof(object).Assembly);

        Assert.That(name).Is.EqualTo(".NET Runtime");
    }

    [Test]
    public static void ClassifyFrameworkAssemblyReturnsNullForAnOrdinaryModAssembly()
    {
        var name = FrameModResolver.ClassifyFrameworkAssembly(
            typeof(FrameModResolverTests).Assembly
        );

        Assert.That(name is null).Is.True();
    }

    [Test]
    public static void FindTypeByNameResolvesARealTypeFromAnAlreadyLoadedAssembly()
    {
        var type = FrameModResolver.FindTypeByName(typeof(CaptureStore).FullName);

        Assert.That(ReferenceEquals(type, typeof(CaptureStore))).Is.True();
    }

    [Test]
    public static void FindTypeByNameReturnsNullForAnUnknownTypeName()
    {
        var type = FrameModResolver.FindTypeByName("DebugAssistance.Capture.ThisTypeDoesNotExist");

        Assert.That(type is null).Is.True();
    }

    [Test]
    public static void FindTypeByNameReturnsNullForNullOrEmptyInput()
    {
        Assert.That(FrameModResolver.FindTypeByName(null) is null).Is.True();
        Assert.That(FrameModResolver.FindTypeByName("") is null).Is.True();
    }

    [Test]
    public static void ResolveParsedFrameResolvesThisModsOwnAssembly()
    {
        var frame = new CapturedStackFrame(
            "raw",
            typeof(CaptureStore).FullName,
            "Add",
            fileName: null,
            lineNumber: null
        );

        FrameModResolver.ResolveParsedFrame(frame);

        Assert.That(frame.ResolvedAssemblyShortName).Is.EqualTo("DebugAssistance");
        Assert.That(frame.ResolvedModName != FrameModResolver.UnresolvableLabel).Is.True();
    }

    [Test]
    public static void ResolveParsedFrameIsUnresolvableWhenTheDeclaringTypeCannotBeFound()
    {
        var frame = new CapturedStackFrame(
            "raw",
            "Some.Totally.Unknown.Type",
            "Method",
            fileName: null,
            lineNumber: null
        );

        FrameModResolver.ResolveParsedFrame(frame);

        Assert.That(frame.ResolvedModName).Is.EqualTo(FrameModResolver.UnresolvableLabel);
    }
}
