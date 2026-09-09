using System.Diagnostics;
using DebugAssistance.Capture;
using DebugAssistance.Decompilation;
using RimTestRedux;

namespace DebugAssistance.Tests.Decompilation;

// Covers FrameDecompiler.DecompilePatched: decompiling the actual merged Harmony replacement
// (PatchedMethodBuilder/PatchedMethodAssemblyWriter) for a genuinely patched method, rather than
// the plain original FrameDecompiler.Decompile shows. Uses a real Harmony patch (like
// FrameModResolverTests) since there's no way to fake a merged prefix/postfix method body.
[TestSuite]
internal static class PatchedMethodDecompilationTests
{
    private const string HarmonyId = "test.debugassistance.patchedmethoddecompilationtests";

    // Returns normally rather than throwing — the merged replacement's postfix call sits after
    // the copied original body, and ICSharpCode.Decompiler drops genuinely unreachable code (e.g.
    // everything following an unconditional throw) from its output entirely, which would hide
    // the postfix's call from the decompiled code no matter how correctly it was built.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MethodToPatch() { }

    private static StackFrame[]? _capturedFrames;

#pragma warning disable IDE0051 // Used as a Harmony patch method, invoked by reflection
    // Captures the live call stack from inside the trampoline's own prefix call — this is the
    // frame FrameModResolver.ResolveLiveFrame needs, and its IL offset is measured against the
    // trampoline's own IL layout (the call to this prefix), which is exactly what
    // FrameDecompiler.DecompilePatched must resolve correctly.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CapturingPrefix() =>
        _capturedFrames = new StackTrace(fNeedFileInfo: false).GetFrames();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NoOpPostfix() { }
#pragma warning restore IDE0051

    private static CapturedStackFrame ResolvePatchedFrame(Harmony harmony)
    {
        var original = AccessTools.Method(
            typeof(PatchedMethodDecompilationTests),
            nameof(MethodToPatch)
        );
        _ = harmony.Patch(
            original,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(PatchedMethodDecompilationTests), nameof(CapturingPrefix))
            ),
            postfix: new HarmonyMethod(
                AccessTools.Method(typeof(PatchedMethodDecompilationTests), nameof(NoOpPostfix))
            )
        );

        _capturedFrames = null;
        MethodToPatch();
        // CA1508 false positive: the analyzer can't see CapturingPrefix writing this field, since
        // Harmony calls it dynamically as the patch runs, not through any visible call graph.
#pragma warning disable CA1508
        var capturedFrames = _capturedFrames ?? [];
#pragma warning restore CA1508

        var patchedFrame =
            capturedFrames.FirstOrDefault(candidate =>
                ReferenceEquals(Harmony.GetOriginalMethodFromStackframe(candidate), original)
            ) ?? throw new InvalidOperationException("No captured frame mapped back to original.");

        var frame = new CapturedStackFrame(
            "raw",
            null,
            null,
            fileName: null,
            lineNumber: null,
            ilOffset: patchedFrame.GetILOffset()
        );
        FrameModResolver.ResolveLiveFrame(frame, patchedFrame);
        return frame;
    }

    [Test]
    public static void DecompilePatchedProducesCodeContainingTheAppliedPatchMethods()
    {
        var harmony = new Harmony(HarmonyId);
        try
        {
            var frame = ResolvePatchedFrame(harmony);

            var result = FrameDecompiler.DecompilePatched(frame);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Expected DecompilePatched to succeed but it failed: {result.Error}"
                );
            }
            // The merged replacement's own body calls both patch methods directly — decompiling
            // the plain original (FrameDecompiler.Decompile) would show neither, since they're
            // only present in the merged trampoline PatchedMethodBuilder reconstructs.
            Assert
                .That(result.Code!.Contains(nameof(CapturingPrefix), StringComparison.Ordinal))
                .Is.True();
            Assert
                .That(result.Code!.Contains(nameof(NoOpPostfix), StringComparison.Ordinal))
                .Is.True();
        }
        finally
        {
            harmony.UnpatchAll(HarmonyId);
        }
    }

    // Deliberately never patched by anything, in this test or any other — MethodToPatch isn't
    // reused here since Harmony.UnpatchAll (in the test above) may leave it with a registered but
    // empty PatchInfo rather than none at all, which would not exercise this failure path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NeverPatchedMethod() { }

    [Test]
    public static void DecompilePatchedFailsGracefullyWhenTheMethodIsNotPatched()
    {
        var frame = new CapturedStackFrame(
            "raw",
            typeof(PatchedMethodDecompilationTests).FullName,
            nameof(NeverPatchedMethod),
            fileName: null,
            lineNumber: null
        )
        {
            Assembly = typeof(PatchedMethodDecompilationTests).Assembly,
        };

        var result = FrameDecompiler.DecompilePatched(frame);

        Assert.That(result.Succeeded).Is.False();
        Assert.That(string.IsNullOrEmpty(result.Error)).Is.False();
    }
}
