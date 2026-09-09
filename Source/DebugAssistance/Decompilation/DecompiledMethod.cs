namespace DebugAssistance.Decompilation;

// Outcome of FrameDecompiler.Decompile: either the decompiled C# for the frame's method plus a
// resolved highlight line (nullable — the target IL offset may precede every recorded sequence
// point, e.g. compiler-generated prologue IL), or a player-facing explanation of why decompilation
// couldn't happen. Never thrown as an exception — degrades gracefully (missing mod assembly,
// method not found, decompiler failure) instead of crashing the inspector window.
internal sealed class DecompiledMethod
{
    public string? Code { get; }
    public int? HighlightLine { get; }
    public string? Error { get; }

    // The on-disk path of the assembly the method was decompiled from (the same path
    // FrameDecompiler.TryResolveMethodForDecompile resolved to read its IL from), so a consumer that
    // wants to point elsewhere (e.g. another tool) at the exact file doesn't have to re-resolve it.
    public string? AssemblyPath { get; }

    public bool Succeeded => Error is null;

    private DecompiledMethod(string? code, int? highlightLine, string? error, string? assemblyPath)
    {
        Code = code;
        HighlightLine = highlightLine;
        Error = error;
        AssemblyPath = assemblyPath;
    }

    internal static DecompiledMethod Ok(string code, int? highlightLine, string assemblyPath) =>
        new(code, highlightLine, null, assemblyPath);

    internal static DecompiledMethod Failed(string error) => new(null, null, error, null);
}
