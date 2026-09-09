using DebugAssistance.Capture;
using DebugAssistance.HotPatch;
using DebugAssistance.Web;

[assembly: InternalsVisibleTo("DebugAssistance.Tests")]

namespace DebugAssistance;

internal partial class DebugAssistanceMod
{
    internal static CaptureStore CaptureStore { get; } = new();

    internal static HotPatchManager HotPatchManager { get; } = new();

    internal static LiveAssemblyLoader LiveAssemblyLoader { get; } = new();

    internal static DebugAssistanceWebServer WebServer { get; private set; } = null!;

    private static readonly RawCaptureRingBuffer RawCaptureRingBuffer = new();

    internal const string MonospaceFontKey = "monospace_14";

    partial void Construct()
    {
        new Harmony(PackageId).PatchAll(Assembly.GetExecutingAssembly());
        StackTraceCapturePatch.Initialize(RawCaptureRingBuffer);
        LogCaptureHook.Initialize(CaptureStore, RawCaptureRingBuffer);
        LogMessageEnqueueCapturePatch.Initialize(CaptureStore, RawCaptureRingBuffer);

        WebServer = new DebugAssistanceWebServer(Content);
        if (Settings.ServerEnabled)
        {
            WebServer.Start(Settings.Port, Settings.AllowExternalConnections);
        }

        CustomFontManager.EnableFeature();
        CustomFontManager.Instance.AddFont(
            MonospaceFontKey,
            14,
            "Cascadia Mono",
            "Consolas",
            "DejaVu Sans Mono",
            "SF Mono",
            "Courier New"
        );
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
internal sealed class HotSwappableAttribute : Attribute { }
