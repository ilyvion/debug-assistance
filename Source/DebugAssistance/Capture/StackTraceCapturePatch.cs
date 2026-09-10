using System.Diagnostics;

namespace DebugAssistance.Capture;

// AppDomain.CurrentDomain.FirstChanceException does not fire in RimWorld's embedded Mono runtime
// (confirmed empirically), so pristine frame capture happens here instead, by patching the same
// method HarmonyMod itself patches to render (and cache-collapse) stack trace text:
// System.Environment.GetStackTrace(Exception, bool). HarmonyPriority.First plus HarmonyBefore
// guarantee this prefix runs ahead of HarmonyMod's own patch on the same method, so the live
// Exception is always captured before HarmonyMod's text cache has a chance to collapse a repeat
// occurrence into a frame-free "Duplicate stacktrace" placeholder. This only needs to outrun
// HarmonyMod specifically (a soft dependency, not referenced by type), so it targets a stable BCL
// method rather than any of HarmonyMod's own internals.
[HarmonyPatch(typeof(Environment), "GetStackTrace")]
[HarmonyPriority(Priority.First)]
[HarmonyBefore("net.pardeike.rimworld.lib.harmony")]
internal static class StackTraceCapturePatch
{
    private static RawCaptureRingBuffer? _ringBuffer;

    internal static void Initialize(RawCaptureRingBuffer ringBuffer) => _ringBuffer = ringBuffer;

#pragma warning disable IDE0051 // Used by reflection
    private static void Prefix(Exception e)
    {
        if (e is null || _ringBuffer is null)
        {
            return;
        }

        var stackTrace = new StackTrace(e, fNeedFileInfo: true);
        var frames = stackTrace.GetFrames();
        if (frames is null)
        {
            return;
        }

        var innerCauses = new List<RawExceptionCause>();
        for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
        {
            var innerTrace = new StackTrace(inner, fNeedFileInfo: true);
            innerCauses.Add(
                new RawExceptionCause(
                    inner.GetType().FullName ?? inner.GetType().Name,
                    inner.Message,
                    innerTrace.GetFrames() ?? [],
                    innerTrace.ToString()
                )
            );
        }

        _ringBuffer.Store(
            new RawCapture(
                e.GetType().FullName ?? e.GetType().Name,
                e.Message,
                frames,
                stackTrace.ToString(),
                DateTime.UtcNow,
                innerCauses
            )
        );
    }
#pragma warning restore IDE0051
}
