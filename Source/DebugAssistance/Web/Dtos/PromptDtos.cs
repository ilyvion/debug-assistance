namespace DebugAssistance.Web.Dtos;

// Wire-format shape for both AI-prompt routes' result — the whole Markdown document as a single
// string, since the frontend only ever copies it verbatim to the clipboard.
internal sealed record AiPromptResultDto
{
    public required string Prompt { get; init; }
}

// Target is optional, mirroring ScaffoldRequestDto's own Target — the Hot Patch panel's
// target-method picker may have been left empty even though a project was still scaffolded (the
// generic, unmatched-signature stub).
internal sealed record HotPatchDebugPromptRequestDto
{
    public required string DedupeKey { get; init; }
    public required string ProjectDirectory { get; init; }
    public MethodRefDto? Target { get; init; }
}
