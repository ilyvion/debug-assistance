namespace DebugAssistance.Web.Dtos;

// Wire-format shape for a decompile route's result. A successful decompile carries Code (and
// HighlightLine when the frame had an IL offset to resolve); a failed one carries only the
// message in ErrorDto instead — the two are never mixed into a single DTO with nullable
// success/failure fields, mirroring DecompiledMethod's own Succeeded/Failed split.
internal sealed record DecompileResultDto
{
    public required string Code { get; init; }
    public int? HighlightLine { get; init; }
}
