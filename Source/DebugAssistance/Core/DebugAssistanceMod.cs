[assembly: InternalsVisibleTo("DebugAssistance.Tests")]

namespace DebugAssistance;

internal partial class DebugAssistanceMod
{
    partial void Construct()
    {
        new Harmony(Constants.Id).PatchAll(Assembly.GetExecutingAssembly());
        _ = GetSettings<Settings>();
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        base.DoSettingsWindowContents(inRect);
        Settings.DoSettingsWindowContents(inRect);
    }

    protected override bool HasSettings => true;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
internal sealed class HotSwappableAttribute : Attribute { }
