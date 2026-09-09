namespace DebugAssistance.Web.Dtos;

internal sealed record ErrorDto
{
    public required string Error { get; init; }
}

internal sealed record TranslationsResponseDto
{
    public required IReadOnlyDictionary<string, string> Translations { get; init; }
}

internal sealed record SettingsResponseDto
{
    public required bool AiPromptGeneratorEnabled { get; init; }
    public required bool ErrorCaptureEnabled { get; init; }
}

internal sealed record ErrorCaptureToggleRequestDto
{
    public required bool Enabled { get; init; }
}
