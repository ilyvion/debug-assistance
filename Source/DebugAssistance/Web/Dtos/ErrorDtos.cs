namespace DebugAssistance.Web.Dtos;

// Wire-format shapes for the /api/errors routes, kept separate from CapturedError and
// friends so the capture domain model (Scribe persistence, transient MethodBase/Assembly refs)
// never has to carry web-serialization concerns of its own.
internal sealed record ErrorListEntryDto
{
    public required string DedupeKey { get; init; }
    public required string ErrorTypeName { get; init; }
    public required string Message { get; init; }
    public required int OccurrenceCount { get; init; }
    public required string FirstSeen { get; init; }
    public required string LastSeen { get; init; }
    public int? HarmonyRefHash { get; init; }
    public string? TopFrameModName { get; init; }
}

internal sealed record ErrorListResponseDto
{
    public required IReadOnlyList<ErrorListEntryDto> Errors { get; init; }
}

internal sealed record ClearErrorsResultDto
{
    public required int ClearedCount { get; init; }
}

internal sealed record PatchDto
{
    public required int Index { get; init; }
    public required string OwnerModId { get; init; }
    public required string PatchKind { get; init; }
    public string? DeclaringTypeName { get; init; }
    public string? MethodName { get; init; }
}

internal sealed record FrameDto
{
    public required int Index { get; init; }
    public required string RawText { get; init; }
    public string? DeclaringTypeName { get; init; }
    public string? MethodName { get; init; }
    public string? FileName { get; init; }
    public int? LineNumber { get; init; }
    public int? ColumnNumber { get; init; }
    public int? IlOffset { get; init; }
    public string? ResolvedModName { get; init; }
    public string? ResolvedAssemblyShortName { get; init; }
    public required IReadOnlyList<PatchDto> Patches { get; init; }

    // The frame's own method, pre-resolved into hot-patch target form for the "Patch this method"
    // entry point — null under the same conditions decompiling this frame would fail under
    // (unresolved reference, or a .dax-loaded frame whose assembly isn't currently loaded).
    public BrowsedMethodDto? PatchTarget { get; init; }
}

internal sealed record ErrorDetailDto
{
    public required string DedupeKey { get; init; }
    public required string ErrorTypeName { get; init; }
    public required string Message { get; init; }
    public required string RawStackTrace { get; init; }
    public required int OccurrenceCount { get; init; }
    public required string FirstSeen { get; init; }
    public required string LastSeen { get; init; }
    public int? HarmonyRefHash { get; init; }
    public required IReadOnlyList<FrameDto> Frames { get; init; }
}
