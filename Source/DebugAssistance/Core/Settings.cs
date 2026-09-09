namespace DebugAssistance;

[HotSwappable]
internal class Settings : ModSettings
{
    internal const int DefaultPort = 3294;

    public bool ServerEnabled = true;
    public int Port = DefaultPort;
    public bool AiPromptGeneratorEnabled;
    public bool AllowExternalConnections;
    public int MaxCapturedEntries = Constants.DefaultMaxCapturedEntries;
    public bool ErrorCaptureEnabled = true;

    private static string _portBuffer = DefaultPort.ToString(CultureInfo.InvariantCulture);
    private static string _maxCapturedEntriesBuffer = Constants.DefaultMaxCapturedEntries.ToString(
        CultureInfo.InvariantCulture
    );

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref ServerEnabled, "serverEnabled", true);
        Scribe_Values.Look(ref Port, "port", DefaultPort);
        Scribe_Values.Look(ref AiPromptGeneratorEnabled, "aiPromptGeneratorEnabled", false);
        Scribe_Values.Look(ref AllowExternalConnections, "allowExternalConnections", false);
        Scribe_Values.Look(
            ref MaxCapturedEntries,
            "maxCapturedEntries",
            Constants.DefaultMaxCapturedEntries
        );
        // Scribe key kept as "exceptionCaptureEnabled" so existing players' saved preference carries over.
        Scribe_Values.Look(ref ErrorCaptureEnabled, "exceptionCaptureEnabled", true);
    }

    public static void DoSettingsWindowContents(Rect inRect)
    {
        var settings = DebugAssistanceMod.Settings;
        var listing = new Listing_Standard();
        listing.Begin(inRect);
        listing.verticalSpacing = 5f;

        var serverEnabled = settings.ServerEnabled;
        listing.CheckboxLabeled(
            "DebugAssistance.Settings.ServerEnabled".Translate(),
            ref serverEnabled
        );
        if (serverEnabled != settings.ServerEnabled)
        {
            settings.ServerEnabled = serverEnabled;
            settings.Write();
            if (serverEnabled)
            {
                DebugAssistanceMod.WebServer.Start(
                    settings.Port,
                    settings.AllowExternalConnections
                );
            }
            else
            {
                DebugAssistanceMod.WebServer.Stop();
            }
        }

        listing.TextFieldNumericLabeled(
            "DebugAssistance.Settings.Port".Translate(),
            ref settings.Port,
            ref _portBuffer,
            0,
            ushort.MaxValue
        );

        if (listing.ButtonText("DebugAssistance.Settings.RestartServer".Translate()))
        {
            settings.Write();
            DebugAssistanceMod.WebServer.ChangePort(
                settings.Port,
                settings.AllowExternalConnections
            );
        }

        listing.Gap();

        var allowExternalConnections = settings.AllowExternalConnections;
        listing.CheckboxLabeled(
            "DebugAssistance.Settings.AllowExternalConnections".Translate(),
            ref allowExternalConnections,
            "DebugAssistance.Settings.AllowExternalConnectionsTooltip".Translate()
        );
        if (allowExternalConnections != settings.AllowExternalConnections)
        {
            settings.AllowExternalConnections = allowExternalConnections;
            settings.Write();
            if (settings.ServerEnabled)
            {
                DebugAssistanceMod.WebServer.ChangePort(
                    settings.Port,
                    settings.AllowExternalConnections
                );
            }
        }

        listing.Gap();

        listing.CheckboxLabeled(
            "DebugAssistance.Settings.AiPromptGeneratorEnabled".Translate(),
            ref settings.AiPromptGeneratorEnabled,
            "DebugAssistance.Settings.AiPromptGeneratorEnabledTooltip".Translate()
        );

        listing.Gap();

        listing.CheckboxLabeled(
            "DebugAssistance.Settings.ErrorCaptureEnabled".Translate(),
            ref settings.ErrorCaptureEnabled,
            "DebugAssistance.Settings.ErrorCaptureEnabledTooltip".Translate()
        );

        listing.Gap();

        listing.TextFieldNumericLabeled(
            "DebugAssistance.Settings.MaxCapturedEntries".Translate(),
            ref settings.MaxCapturedEntries,
            ref _maxCapturedEntriesBuffer,
            1,
            1_000_000
        );

        listing.End();
    }
}
