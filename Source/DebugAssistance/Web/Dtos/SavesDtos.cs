namespace DebugAssistance.Web.Dtos;

internal sealed record SaveFileEntryDto
{
    public required string FileName { get; init; }
    public required string LastWriteTime { get; init; }
}

internal sealed record SavesListResponseDto
{
    public required IReadOnlyList<SaveFileEntryDto> Saves { get; init; }
}

internal sealed record SaveCaptureRequestDto
{
    public string? FileName { get; init; }
    public bool Overwrite { get; init; }
}

internal sealed record SaveCaptureResultDto
{
    public required int OccurrenceCount { get; init; }
}

internal sealed record LoadCaptureRequestDto
{
    public string? Mode { get; init; }
}

internal sealed record LoadCaptureResultDto
{
    public required int LoadedCount { get; init; }
}

internal sealed record DeleteResultDto
{
    public required bool Deleted { get; init; }
}
