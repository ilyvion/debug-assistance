using System.Reflection.Emit;
using DebugAssistance.UI;

namespace DebugAssistance.Patches;

// Adds a button to the debug toolbar that opens the web Error Inspector in the system browser,
// using a transpiler that inserts a call to our own Draw(WidgetRow) right before the original
// reads WidgetRow.FinalX, then pushes the private widgetRow field back onto the stack so the
// original getter call still runs unmodified.
[HarmonyPatch(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.DrawButtons))]
internal static class DebugWindowsOpenerPatch
{
    private static readonly FieldInfo FieldDebugWindowsOpenerWidgetRow = AccessTools.Field(
        typeof(DebugWindowsOpener),
        "widgetRow"
    );
    private static readonly MethodInfo MethodWidgetRowFinalXGet = AccessTools.PropertyGetter(
        typeof(WidgetRow),
        nameof(WidgetRow.FinalX)
    );
    private static readonly MethodInfo MethodDraw = SymbolExtensions.GetMethodInfo(() =>
        Draw(default!)
    );

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    private static IEnumerable<CodeInstruction> Transpiler(
#pragma warning restore CA1859 // Use concrete types when possible for improved performance
        IEnumerable<CodeInstruction> instructions
    )
    {
        var codeMatcher = new CodeMatcher(instructions);

        _ = codeMatcher.SearchForward(i =>
            i.opcode == OpCodes.Callvirt
            && i.operand is MethodInfo m
            && m == MethodWidgetRowFinalXGet
        );
        if (!codeMatcher.IsValid)
        {
            Log.Error(
                "[DebugAssistance] Could not patch DebugWindowsOpener.DrawButtons, IL does not match expectations: call to get value of WidgetRow.FinalX was not found."
            );
            return codeMatcher.Instructions();
        }
        _ = codeMatcher.Insert([
            new CodeInstruction(OpCodes.Call, MethodDraw),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, FieldDebugWindowsOpenerWidgetRow),
        ]);

        return codeMatcher.Instructions();
    }

    private static void Draw(WidgetRow row)
    {
        var settings = DebugAssistanceMod.Settings;
        var tooltip = settings.ServerEnabled
            ? "DebugAssistance.Inspector.OpenTooltip".Translate()
            : "DebugAssistance.Inspector.OpenTooltipDisabled".Translate();

        if (row.ButtonIcon(Icons.Inspector, tooltip) && settings.ServerEnabled)
        {
            Application.OpenURL($"http://localhost:{settings.Port}");
        }
    }
}
