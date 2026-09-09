using System.Text;
using LudeonTK;

namespace DebugAssistance.Core;

// Manual-verification aids for the Capture pipeline: trigger a real exception and inspect
// what CaptureStore actually recorded for it.
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
public static class DebugActions
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
{
    [IlyvionDebugAction(
        "Debug Assistance",
        "Throw test exception",
        displayPriority: 9999,
        allowedGameStates = AllowedGameStates.Entry
    )]
#pragma warning disable IDE0051 // Used by reflection
    private static void ThrowTestException() =>
        throw new InvalidOperationException(
            "DebugAssistance test exception (safe to ignore) — triggered via debug action"
        );

    [IlyvionDebugAction(
        "Debug Assistance",
        "Log test error",
        displayPriority: 9999,
        allowedGameStates = AllowedGameStates.Entry
    )]
#pragma warning disable IDE0051 // Used by reflection
    private static void LogTestError() =>
        Log.Error("DebugAssistance test error (safe to ignore) — triggered via debug action");

    [DebugOutput("Debug Assistance", false)]
    private static void DumpCaptureStore()
    {
        var entries = DebugAssistanceMod.CaptureStore.Snapshot();
        if (entries.Count == 0)
        {
            DebugAssistanceMod.Instance.LogMessage("No errors have been captured yet.");
            return;
        }

        var stringBuilder = new StringBuilder();
        _ = stringBuilder.AppendLine($"{entries.Count} captured error(s):");
        foreach (var entry in entries)
        {
            _ = stringBuilder
                .AppendLine($"- {entry.ErrorTypeName}: {entry.Message}")
                .AppendLine($"    Dedupe key: {entry.DedupeKey}")
                .AppendLine(
                    $"    Harmony ref hash: {(entry.HarmonyRefHash is { } hash ? hash.ToString("X", CultureInfo.InvariantCulture) : "(none)")}"
                )
                .AppendLine(
                    $"    Occurrences: {entry.OccurrenceCount} (first {entry.FirstSeen:O}, last {entry.LastSeen:O})"
                )
                .AppendLine($"    Frames: {entry.Frames.Count}");
            foreach (var frame in entry.Frames)
            {
                _ = stringBuilder.AppendLine(
                    $"      {frame.RawText}"
                        + $" -> mod: {frame.ResolvedModName ?? "(unresolved)"}"
                        + $", assembly: {frame.ResolvedAssemblyShortName ?? "(unknown)"}"
                        + $", file: {frame.FileName ?? "(none)"}:{frame.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                );
            }
        }
        DebugAssistanceMod.Instance.LogMessage(stringBuilder.ToString());
    }
#pragma warning restore IDE0051
}
