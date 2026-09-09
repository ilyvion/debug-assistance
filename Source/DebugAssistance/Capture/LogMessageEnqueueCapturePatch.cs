namespace DebugAssistance.Capture;

// Verse.Log.Error/Warning/Message each build a LogMessage bundling the logged text together with
// StackTraceUtility.ExtractStackTrace()'s own trace text, then call LogMessageQueue.Enqueue(msg,
// out _) before finally calling Debug.LogError/LogWarning/Log(text) — the call that actually
// reaches Application.logMessageReceivedThreaded. This is the mod's capture point for these calls:
// other mods can rewrite log text between here and whatever eventually reaches that Unity event, so
// LogCaptureHook.Handle (which listens to that event) never tries to correlate against it — this
// patch hands the message and its already-correlated trace straight to LogCaptureHook instead.
[HarmonyPatch]
[HarmonyPriority(Priority.First)]
internal static class LogMessageEnqueueCapturePatch
{
    private static CaptureStore? _store;
    private static RawCaptureRingBuffer? _ringBuffer;
    private static Func<bool>? _captureEnabled;

    internal static void Initialize(
        CaptureStore store,
        RawCaptureRingBuffer ringBuffer,
        Func<bool>? captureEnabled = null
    )
    {
        _store = store;
        _ringBuffer = ringBuffer;
        _captureEnabled = captureEnabled ?? (() => DebugAssistanceMod.Settings.ErrorCaptureEnabled);
    }

#pragma warning disable IDE0051 // Used by reflection
    private static MethodInfo TargetMethod() =>
        AccessTools.Method(
            typeof(LogMessageQueue),
            nameof(LogMessageQueue.Enqueue),
            [typeof(LogMessage), typeof(bool).MakeByRefType()]
        );

    private static void Prefix(LogMessage msg)
    {
        if (_store is null || _ringBuffer is null || msg.type != LogMessageType.Error)
        {
            return;
        }

        LogCaptureHook.CaptureFromLogMessage(_store, _ringBuffer, msg.text, _captureEnabled);
    }
#pragma warning restore IDE0051
}
