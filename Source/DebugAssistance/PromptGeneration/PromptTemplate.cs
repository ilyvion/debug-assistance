using System.Text;
using DebugAssistance.Capture;
using DebugAssistance.Decompilation;

namespace DebugAssistance.PromptGeneration;

// Builds structured-Markdown "hand this to an AI agent" prompts from a single captured error:
// a summary, the full raw stack trace annotated with each frame's resolved mod/assembly, then a
// decompiled snippet per frame. Build ends on a blank heading for the player to fill in
// themselves; BuildHotPatchPrompt ends on a concrete task pointing at a Hot Patch panel scaffolded
// project instead. Both eagerly decompile every frame (unlike the on-demand per-frame decompile
// routes) since the whole point of either button is one complete document assembled in a single
// request, not a starting point the player then has to expand frame-by-frame themselves.
internal static class PromptTemplate
{
    // Lines of decompiled source shown above and below the resolved highlight line — enough to see
    // the involved statement in context without pulling the whole (potentially long) method into
    // what's meant to be a focused, pasteable prompt.
    private const int ContextLines = 3;

    internal static string Build(CapturedError error)
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("# RimWorld error report");
        _ = sb.AppendLine();
        AppendReportBody(sb, error);
        _ = sb.AppendLine("## What I'd like help with");
        _ = sb.AppendLine();

        return sb.ToString();
    }

    // Same error report as Build above, but aimed at the Hot Patch panel's scaffolded starter
    // project instead of ending on a blank heading for the player to fill in themselves: the task
    // is already known (find the bug, write the fix as a hot patch), and where to put it (the
    // project Scaffold just generated) is already known too.
    internal static string BuildHotPatchPrompt(
        CapturedError error,
        string projectDirectory,
        string? targetMethodDescription
    )
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("# DebugAssistance hot patch task");
        _ = sb.AppendLine();
        AppendReportBody(sb, error);
        AppendHotPatchTask(sb, projectDirectory, targetMethodDescription);

        return sb.ToString();
    }

    private static void AppendReportBody(StringBuilder sb, CapturedError error)
    {
        AppendErrorSummary(sb, error);
        AppendStackTrace(sb, error);
        AppendFrameDetails(sb, error);
    }

    private static void AppendHotPatchTask(
        StringBuilder sb,
        string projectDirectory,
        string? targetMethodDescription
    )
    {
        _ = sb.AppendLine("## Task");
        _ = sb.AppendLine(
            $"Analyze the error above and write the Harmony patch most likely to get to the "
                + $"bottom of it, in the starter project already generated at `{projectDirectory}` "
                + "(see its `Patches.cs`)."
        );
        if (targetMethodDescription is { } target)
        {
            _ = sb.AppendLine($"Its stub is already set up to target `{target}`.");
        }
        _ = sb.AppendLine(
            "Keep whichever of the stub's Prefix/Postfix/Transpiler/Finalizer methods best fit the "
                + "diagnosis (a combination is fine), remove the rest, and explain your reasoning."
        );
        _ = sb.AppendLine();
    }

    private static void AppendErrorSummary(StringBuilder sb, CapturedError error)
    {
        _ = sb.AppendLine("## Error");
        _ = sb.AppendLine($"- **Type:** `{error.ErrorTypeName}`");
        _ = sb.AppendLine($"- **Message:** {error.Message}");
        _ = sb.AppendLine(
            $"- **Occurrences:** {error.OccurrenceCount} (first {error.FirstSeen:u}, last {error.LastSeen:u})"
        );
        _ = sb.AppendLine();
    }

    private static void AppendStackTrace(StringBuilder sb, CapturedError error)
    {
        _ = sb.AppendLine("## Stack trace");
        _ = sb.AppendLine("```");
        foreach (var frame in error.Frames)
        {
            _ = sb.AppendLine($"{frame.RawText} {FrameAnnotation(frame)}");
        }
        _ = sb.AppendLine("```");
        _ = sb.AppendLine();
    }

    private static string FrameAnnotation(CapturedStackFrame frame)
    {
        var modName = frame.ResolvedModName ?? FrameModResolver.UnresolvableLabel;
        var assemblyName = frame.ResolvedAssemblyShortName is { } shortName
            ? $"{shortName}.dll"
            : "?";
        return $"[{modName}, {assemblyName}]";
    }

    private static void AppendFrameDetails(StringBuilder sb, CapturedError error)
    {
        if (error.Frames.Count == 0)
        {
            return;
        }

        _ = sb.AppendLine("## Frame details");
        _ = sb.AppendLine();
        for (var i = 0; i < error.Frames.Count; i++)
        {
            AppendFrameDetail(sb, i, error.Frames[i]);
        }
    }

    private static void AppendFrameDetail(StringBuilder sb, int index, CapturedStackFrame frame)
    {
        var heading = frame.DeclaringTypeName is { } declaringTypeName
            ? $"{declaringTypeName}.{frame.MethodName}"
            : frame.RawText;
        _ = sb.AppendLine($"### Frame {index}: {heading} {FrameAnnotation(frame)}");

        if (frame.FileName is { } fileName)
        {
            var line = frame.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "?";
            _ = sb.AppendLine(
                $"_Originally reported at {fileName}:{line} (from the original build machine, comes from the DLL's associated PDB; it is not necessarily the same as the line highlighted below, which comes from decompilation)._"
            );
        }
        _ = sb.AppendLine();

        var decompiled = FrameDecompiler.Decompile(frame);
        if (!decompiled.Succeeded)
        {
            _ = sb.AppendLine($"_Could not decompile this frame: {decompiled.Error}_");
            _ = sb.AppendLine();
            return;
        }

        if (decompiled.AssemblyPath is { } assemblyPath)
        {
            _ = sb.AppendLine($"_Assembly: `{assemblyPath}`_");
            _ = sb.AppendLine();
        }

        _ = sb.AppendLine("```csharp");
        _ = sb.Append(BuildSnippet(decompiled.Code!, decompiled.HighlightLine));
        _ = sb.AppendLine("```");
        _ = sb.AppendLine();
    }

    // Only a few lines of context around the resolved highlight line, not the whole decompiled
    // method — see ContextLines above. Falls back to the method's first few lines when no highlight
    // line resolved (the frame's IL offset preceded every recorded sequence point). The ">>> "
    // marker is plain-text-only, unlike the web panel's Prism-based highlighting (CodePanel.vue) —
    // there's no renderer here to hand a data-line attribute to, this text goes straight into a
    // Markdown code fence.
    internal static string BuildSnippet(string code, int? highlightLine)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var centerIndex =
            highlightLine is { } line && line >= 1 && line <= lines.Length ? line - 1 : 0;

        var start = Math.Max(0, centerIndex - ContextLines);
        var end = Math.Min(lines.Length - 1, centerIndex + ContextLines);

        var sb = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var marker = highlightLine is { } hl && i == hl - 1 ? ">>> " : "    ";
            _ = sb.AppendLine(marker + lines[i]);
        }
        return sb.ToString();
    }
}
