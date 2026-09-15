namespace DebugAssistance.Web.Dtos;

// Wire-format shapes for the /api/probes routes, mirroring ErrorDtos but for captured probe hits
// (a deduplicated stack that invoked a manually probed method) rather than captured errors.
internal sealed record ProbeListEntryDto
{
    public required string DedupeKey { get; init; }
    public required string TargetDeclaringTypeName { get; init; }
    public required string TargetMethodName { get; init; }
    public required string TargetDisplayName { get; init; }
    public required int OccurrenceCount { get; init; }
    public required string FirstSeen { get; init; }
    public required string LastSeen { get; init; }
    public string? TopFrameModName { get; init; }
}

internal sealed record ProbeListResponseDto
{
    public required IReadOnlyList<ProbeListEntryDto> Probes { get; init; }
}

internal sealed record ProbeDetailDto
{
    public required string DedupeKey { get; init; }
    public required string TargetDeclaringTypeName { get; init; }
    public required string TargetMethodName { get; init; }
    public required string TargetDisplayName { get; init; }
    public required string RawStackTrace { get; init; }
    public required int OccurrenceCount { get; init; }
    public required string FirstSeen { get; init; }
    public required string LastSeen { get; init; }
    public required IReadOnlyList<FrameDto> Frames { get; init; }
}

internal sealed record ClearProbesResultDto
{
    public required int ClearedCount { get; init; }
}

internal sealed record AddProbeRequestDto
{
    public required MethodRefDto Target { get; init; }
}

internal sealed record ActiveProbeDto
{
    public required string Id { get; init; }
    public required string TargetDeclaringTypeName { get; init; }
    public required string TargetMethodName { get; init; }
    public required string TargetDisplayName { get; init; }
    public required string AppliedAt { get; init; }
    public required long TotalInvocationCount { get; init; }
    public required int UniqueHitCount { get; init; }
    public required bool IsActive { get; init; }
    public required string CapReason { get; init; }
}

internal sealed record AddProbeResultDto
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public ActiveProbeDto? Probe { get; init; }
}

internal sealed record ActiveProbeListDto
{
    public required IReadOnlyList<ActiveProbeDto> Probes { get; init; }
}

internal sealed record RemoveProbeResultDto
{
    public required bool Removed { get; init; }
}
