using System.Diagnostics.CodeAnalysis;
using DebugAssistance.Persistence;

namespace DebugAssistance.Web;

// Pure request-validation logic for SavesEndpoints' save/load routes, kept separate from the
// HttpListenerContext-handling endpoints themselves so it's unit-testable without a real HTTP
// request (mirrors ErrorRouteResolver's own reason for existing as a separate class).
internal static class SavesRequestValidator
{
    // Decompilation/NotNullWhenAttribute.cs declares its own
    // System.Diagnostics.CodeAnalysis.NotNullWhenAttribute so nullable-flow-analysis attributes
    // work despite 0Harmony's own embedded copy of it also becoming public via the whole-assembly
    // Publicize; CS0436 (source declaration shadowing an imported type of the same name) is the
    // expected, harmless result.
#pragma warning disable CS0436 // Type conflicts with imported type
    internal static bool IsValidFileName([NotNullWhen(true)] string? fileName) =>
#pragma warning restore CS0436 // Type conflicts with imported type
        fileName is not null && CaptureFileIO.IsValidFileName(fileName);

    internal static bool ConflictsWithExistingSave(bool overwrite, bool existingFileExists) =>
        !overwrite && existingFileExists;

    internal static bool IsValidLoadMode(string? mode) => mode is "merge" or "replace";
}
