namespace DebugAssistance.Web.Dtos;

internal sealed record FileBrowserEntryDto
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public string? LastWriteTime { get; init; }
}

internal sealed record FileBrowserListDto
{
    public string? CurrentPath { get; init; }
    public string? ParentPath { get; init; }
    public required IReadOnlyList<FileBrowserEntryDto> Entries { get; init; }
    public required IReadOnlyList<FileBrowserShortcutDto> Shortcuts { get; init; }
}

internal sealed record FileBrowserShortcutDto
{
    public required string Kind { get; init; }
    public required string Path { get; init; }
}
