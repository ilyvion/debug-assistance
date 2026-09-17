namespace DebugAssistance.Web.Dtos;

internal sealed record BrowsedMethodDto
{
    public required string AssemblyName { get; init; }
    public required string AssemblyFullName { get; init; }
    public required int MetadataToken { get; init; }
    public required string DeclaringTypeName { get; init; }
    public required string Namespace { get; init; }
    public required string MethodName { get; init; }
    public required string Signature { get; init; }
    public required bool IsStatic { get; init; }
}

// TotalCount/HasMore describe the full (possibly cached) search result Methods is a page of --
// see HotPatchEndpoints.ServeMethodList and MethodSearchCache. The tree-picker leaf level (methods
// of one type) returns its full result as a single page, so HasMore is always false there.
internal sealed record MethodListResponseDto
{
    public required IReadOnlyList<BrowsedMethodDto> Methods { get; init; }
    public required int TotalCount { get; init; }
    public required bool HasMore { get; init; }
}

// The three levels above Methods in the tree-style picker (Assembly -> Namespace -> Type ->
// Member): AssemblyFullName/TypeFullName are the opaque keys the next level down is browsed by,
// Name is what's actually shown in the UI.
internal sealed record AssemblyEntryDto
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required int TypeCount { get; init; }
}

internal sealed record NamespaceEntryDto
{
    public required string Name { get; init; }
    public required int TypeCount { get; init; }
}

internal sealed record TypeEntryDto
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required int MethodCount { get; init; }
}

internal sealed record AssemblyListResponseDto
{
    public required IReadOnlyList<AssemblyEntryDto> Assemblies { get; init; }
}

internal sealed record NamespaceListResponseDto
{
    public required IReadOnlyList<NamespaceEntryDto> Namespaces { get; init; }
}

internal sealed record TypeListResponseDto
{
    public required IReadOnlyList<TypeEntryDto> Types { get; init; }
}

// RemovePatchIds distinguishes "hasn't been asked yet" from "answered with nothing to remove":
// null means the player hasn't been asked -- if the path being (re)loaded still has patches
// active from its previous generation, the server responds with NeedsConfirmation instead of
// loading. A non-null list (possibly empty) is the player's answer, naming which of those
// previous-generation patches (by id) to remove before loading; any not named are left running.
internal sealed record LoadAssemblyRequestDto
{
    public required string Path { get; init; }
    public IReadOnlyList<string>? RemovePatchIds { get; init; }
}

// When NeedsConfirmation is true, the load has *not* happened yet -- only
// PatchesFromPreviousLoad is populated, listing the previous generation's active patches so the
// player can pick which ones a RemovePatchIds follow-up request should remove. Otherwise the load
// already happened and the remaining fields describe its result, with RemovedPatchDescriptions
// empty unless RemovePatchIds named at least one patch. DiscoveredPatches is null/empty unless
// PatchAttributeScanner found at least one
// [HarmonyPrefix]/[HarmonyPostfix]/[HarmonyTranspiler]/[HarmonyFinalizer]-attributed method with a
// resolvable target in the freshly loaded assembly.
internal sealed record LoadAssemblyResultDto
{
    public required bool NeedsConfirmation { get; init; }
    public IReadOnlyList<ActivePatchDto>? PatchesFromPreviousLoad { get; init; }
    public string? AssemblyName { get; init; }
    public string? AssemblyFullName { get; init; }
    public int? Generation { get; init; }
    public IReadOnlyList<string>? RemovedPatchDescriptions { get; init; }
    public IReadOnlyList<DiscoveredPatchDto>? DiscoveredPatches { get; init; }
}

// One assembly path the player has already loaded this session, at its current generation --
// offered by the "already loaded" picker so switching between patch assemblies (or back to one
// used earlier) never needs a fresh load-from-disk, and thus never risks the reload-confirmation
// prompt above.
internal sealed record LoadedAssemblyDto
{
    public required string Path { get; init; }
    public required string AssemblyName { get; init; }
    public required int Generation { get; init; }
}

internal sealed record LoadedAssemblyListDto
{
    public required IReadOnlyList<LoadedAssemblyDto> Assemblies { get; init; }
}

// Identifies one method for the apply route: MetadataToken is scoped to a specific Module
// instance, so the caller must also say which assembly it came from. For the patch method that's
// resolved through the currently-loaded assembly at ApplyPatchRequestDto.SourceAssemblyPath
// rather than this DTO's own AssemblyFullName, since reloading the same path leaves the previous
// Assembly instance sitting in the AppDomain too (see HotPatchEndpoints.ResolveAssemblyByFullName).
internal sealed record MethodRefDto
{
    public required string AssemblyFullName { get; init; }
    public required int MetadataToken { get; init; }
}

// One method PatchAttributeScanner found already carrying a resolvable [HarmonyPatch] target plus
// a [HarmonyPrefix]/[HarmonyPostfix]/[HarmonyTranspiler]/[HarmonyFinalizer] attribute -- the same
// Target/PatchMethod/PatchType shape ApplyPatchRequestDto expects, so applying one of these is a
// direct pass-through to /api/hotpatch/apply.
internal sealed record DiscoveredPatchDto
{
    public required MethodRefDto Target { get; init; }
    public required string TargetDescription { get; init; }
    public required MethodRefDto PatchMethod { get; init; }
    public required string PatchMethodDescription { get; init; }
    public required string PatchType { get; init; }
}

internal sealed record ApplyPatchRequestDto
{
    public required MethodRefDto Target { get; init; }
    public required MethodRefDto PatchMethod { get; init; }
    public required string PatchType { get; init; }
    public required string SourceAssemblyPath { get; init; }
}

internal sealed record ApplyPatchResultDto
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public ActivePatchDto? Patch { get; init; }
}

internal sealed record ActivePatchDto
{
    public required string Id { get; init; }
    public required string TargetDescription { get; init; }
    public required string PatchMethodDescription { get; init; }
    public required string PatchType { get; init; }
    public required string SourceAssemblyPath { get; init; }
    public required string SourceAssemblyName { get; init; }
    public required int SourceAssemblyGeneration { get; init; }
}

internal sealed record ActivePatchListDto
{
    public required IReadOnlyList<ActivePatchDto> Patches { get; init; }
}

internal sealed record RemoveManyPatchesRequestDto
{
    public required IReadOnlyList<string> Ids { get; init; }
}

internal sealed record RemoveManyPatchesResultDto
{
    public required IReadOnlyList<string> RemovedIds { get; init; }
}

// Target is optional: an arbitrary-method scaffold (the from-scratch entry point) has no
// method to reflect a signature-matched stub from, and ProjectScaffolder already handles a null
// targetMethod by emitting the generic stub.
internal sealed record ScaffoldRequestDto
{
    public required string DirectoryPath { get; init; }
    public required string ProjectName { get; init; }
    public MethodRefDto? Target { get; init; }
}

internal sealed record ScaffoldResultDto
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? ProjectDirectory { get; init; }
    public string? ExpectedAssemblyPath { get; init; }
}

internal sealed record SuggestedProjectNameDto
{
    public required string ProjectName { get; init; }
}
