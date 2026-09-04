namespace DebugAssistance;

[HotSwappable]
internal class Settings : ModSettings
{
    public override void ExposeData() => base.ExposeData();

    public static void DoSettingsWindowContents(Rect inRect) => _ = inRect;
}
