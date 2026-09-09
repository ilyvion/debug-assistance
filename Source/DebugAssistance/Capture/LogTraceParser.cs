using System.Text.RegularExpressions;

namespace DebugAssistance.Capture;

// Parses exception type/message/frames directly out of Unity log text for LogCaptureHook's
// fallback path (no ring-buffer correlation was found — see RawCaptureCorrelator). Tolerates
// HarmonyMod's enhanced first-sighting form (a "[Ref {hash:X}]" prefix followed by full frame
// text), its collapsed repeat form ("[Ref {hash:X}] Duplicate stacktrace, see ref for original",
// with no frame detail at all), and plain undecorated Mono/CLR-style stack trace text.
internal static class LogTraceParser
{
    private const string DuplicateStacktraceMarker = "Duplicate stacktrace, see ref for original";

    private static readonly Regex HarmonyRefHashRegex = new(
        @"\[Ref (?<hash>[0-9A-Fa-f]{1,8})\]",
        RegexOptions.Compiled
    );

    // Mono/.NET stack frame line: "  at Type.Method (params) [0x1f] in file:33" or
    // "   at Type.Method(params) in file:line 33" — the [0x..] offset and "in ..." location are
    // both optional depending on format/build.
    private static readonly Regex FrameLineRegex = new(
        @"^\s*at\s+(?<member>.+?)\s*\([^()]*\)\s*(?:\[0x(?<ilOffset>[0-9A-Fa-f]+)\]\s*)?(?:in\s+(?<location>.+))?\s*$",
        RegexOptions.Compiled
    );

    private static readonly Regex ErrorHeaderRegex = new(
        @"(?<type>[A-Za-z_][\w.]*(?:Exception|Error))\s*:\s*(?<message>.*)",
        RegexOptions.Compiled | RegexOptions.Singleline
    );

    // Mono's "no debug info available" marker: a module GUID instead of a real path, paired with
    // line 0 — as opposed to a genuine (if unusable) shipped-PDB file:line.
    private static readonly Regex NoFileInfoLocationRegex = new(
        @"^<[0-9A-Fa-f]+>:0$",
        RegexOptions.Compiled
    );

    private static readonly Regex FileLineLocationRegex = new(
        @"^(?<file>.+):(?:line\s+)?(?<line>\d+)$",
        RegexOptions.Compiled
    );

    internal static int? ParseHarmonyRefHash(string logText)
    {
        var match = HarmonyRefHashRegex.Match(logText);
        return match.Success
            ? int.Parse(
                match.Groups["hash"].Value,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture
            )
            : null;
    }

    internal static bool IsCollapsedDuplicate(string logText) =>
        logText.Contains(DuplicateStacktraceMarker, StringComparison.Ordinal);

    internal static ParsedError Parse(string logText)
    {
        var lines = logText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var frameStart = Array.FindIndex(lines, IsFrameLine);
        var headerLines = frameStart < 0 ? lines : lines.Take(frameStart);
        var frameLines = frameStart < 0 ? [] : lines.Skip(frameStart).TakeWhile(IsFrameLine);

        var header = string.Join("\n", headerLines).Trim();
        var headerMatch = ErrorHeaderRegex.Match(header);
        var errorTypeName = headerMatch.Success ? headerMatch.Groups["type"].Value : "";
        var message = headerMatch.Success ? headerMatch.Groups["message"].Value.Trim() : header;

        return new ParsedError(errorTypeName, message, [.. frameLines.Select(ParseFrameLine)]);
    }

    private static bool IsFrameLine(string line) => FrameLineRegex.IsMatch(line);

    private static ParsedFrame ParseFrameLine(string line)
    {
        var match = FrameLineRegex.Match(line);
        var (declaringTypeName, methodName) = SplitMember(match.Groups["member"].Value);
        var (fileName, lineNumber) = ParseLocation(match.Groups["location"]);
        var ilOffsetGroup = match.Groups["ilOffset"];
        var ilOffset = ilOffsetGroup.Success
            ? int.Parse(ilOffsetGroup.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : (int?)null;
        return new ParsedFrame(
            line.Trim(),
            declaringTypeName,
            methodName,
            fileName,
            lineNumber,
            ilOffset
        );
    }

    private static (string? FileName, int? LineNumber) ParseLocation(Group locationGroup)
    {
        if (!locationGroup.Success)
        {
            return (null, null);
        }

        var location = locationGroup.Value.Trim();
        if (NoFileInfoLocationRegex.IsMatch(location))
        {
            return (null, null);
        }

        var fileLineMatch = FileLineLocationRegex.Match(location);
        return fileLineMatch.Success
            ? (
                fileLineMatch.Groups["file"].Value,
                int.Parse(fileLineMatch.Groups["line"].Value, CultureInfo.InvariantCulture)
            )
            : (null, null);
    }

    private static (string? DeclaringTypeName, string? MethodName) SplitMember(string member)
    {
        var lastDot = member.LastIndexOf('.');
        return lastDot < 0 ? (null, member) : (member[..lastDot], member[(lastDot + 1)..]);
    }
}
