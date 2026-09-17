export interface ErrorListEntry {
    dedupeKey: string;
    errorTypeName: string;
    message: string;
    occurrenceCount: number;
    firstSeen: string;
    lastSeen: string;
    harmonyRefHash: number | null;
    topFrameModName: string | null;
}

export interface PatchInfo {
    index: number;
    ownerModId: string;
    patchKind: string;
    declaringTypeName: string | null;
    methodName: string | null;
    displayName: string | null;
}

export interface FrameInfo {
    index: number;
    rawText: string;
    declaringTypeName: string | null;
    methodName: string | null;
    displayName: string | null;
    fileName: string | null;
    lineNumber: number | null;
    columnNumber: number | null;
    ilOffset: number | null;
    resolvedModName: string | null;
    resolvedAssemblyShortName: string | null;
    patches: PatchInfo[];
    patchTarget: BrowsedMethod | null;
}

export interface ErrorCause {
    errorTypeName: string;
    message: string;
    rawStackTrace: string;
    frames: FrameInfo[];
}

export interface ErrorDetail {
    dedupeKey: string;
    errorTypeName: string;
    message: string;
    rawStackTrace: string;
    occurrenceCount: number;
    firstSeen: string;
    lastSeen: string;
    harmonyRefHash: number | null;
    frames: FrameInfo[];
    innerCauses: ErrorCause[];
}

export interface DecompileResult {
    code?: string;
    highlightLine?: number | null;
    error?: string;
}

export interface SaveFileEntry {
    fileName: string;
    lastWriteTime: string;
}

export interface FileBrowserEntry {
    name: string;
    path: string;
    isDirectory: boolean;
    size: number | null;
    lastWriteTime: string | null;
}

export interface FileBrowserShortcut {
    kind: string;
    path: string;
}

export interface FileBrowserList {
    currentPath: string | null;
    parentPath: string | null;
    entries: FileBrowserEntry[];
    shortcuts: FileBrowserShortcut[];
}

export interface BrowsedMethod {
    assemblyName: string;
    assemblyFullName: string;
    metadataToken: number;
    declaringTypeName: string;
    namespace: string;
    methodName: string;
    signature: string;
    isStatic: boolean;
}

export interface AssemblyEntry {
    name: string;
    fullName: string;
    typeCount: number;
}

export interface NamespaceEntry {
    name: string;
    typeCount: number;
}

export interface TypeEntry {
    name: string;
    fullName: string;
    methodCount: number;
}

export interface MethodRef {
    assemblyFullName: string;
    metadataToken: number;
}

export type HarmonyPatchTypeName =
    'Prefix' | 'Postfix' | 'Transpiler' | 'Finalizer' | 'Replace';

// Narrows the patch-method picker to methods PatchCompatibility.IsCompatible accepts for a given
// target method + patch type.
export interface CompatibleWith {
    target: MethodRef;
    patchType: HarmonyPatchTypeName;
}

// One method PatchAttributeScanner found already carrying a resolvable [HarmonyPatch] target plus
// a [HarmonyPrefix]/[HarmonyPostfix]/[HarmonyTranspiler]/[HarmonyFinalizer] attribute -- the same
// target/patchMethod/patchType shape applyHotPatch expects, so applying one is a direct
// pass-through to it.
export interface DiscoveredPatch {
    target: MethodRef;
    targetDescription: string;
    patchMethod: MethodRef;
    patchMethodDescription: string;
    patchType: HarmonyPatchTypeName;
}

// When needsConfirmation is true, the load has not happened yet -- only
// patchesFromPreviousLoad is set, listing the previous generation's active patches so the player
// can pick which ones a removePatchIds follow-up call should remove. Otherwise the load already
// happened and the remaining fields describe its result. discoveredPatches is unset/empty unless
// the freshly loaded assembly had at least one
// [HarmonyPrefix]/[HarmonyPostfix]/[HarmonyTranspiler]/[HarmonyFinalizer]-attributed method with a
// resolvable target.
export interface LoadAssemblyResult {
    needsConfirmation: boolean;
    patchesFromPreviousLoad?: ActivePatch[];
    assemblyName?: string;
    assemblyFullName?: string;
    generation?: number;
    removedPatchDescriptions?: string[];
    discoveredPatches?: DiscoveredPatch[];
}

// One assembly path already loaded this session, at its current generation -- offered by the
// "already loaded" picker so switching patch assemblies never needs a fresh load from disk.
export interface LoadedAssemblyEntry {
    path: string;
    assemblyName: string;
    generation: number;
}

export interface ActivePatch {
    id: string;
    targetDescription: string;
    patchMethodDescription: string;
    patchType: string;
    sourceAssemblyPath: string;
    sourceAssemblyName: string;
    sourceAssemblyGeneration: number;
}

export interface ApplyPatchResult {
    success: boolean;
    error?: string;
    patch?: ActivePatch;
}

export interface ScaffoldResult {
    success: boolean;
    error?: string;
    projectDirectory?: string;
    expectedAssemblyPath?: string;
}

export interface SuggestedProjectName {
    projectName: string;
}

export interface ModSettings {
    aiPromptGeneratorEnabled: boolean;
    errorCaptureEnabled: boolean;
}

export interface ProbeListEntry {
    dedupeKey: string;
    targetDeclaringTypeName: string;
    targetMethodName: string;
    targetDisplayName: string;
    occurrenceCount: number;
    firstSeen: string;
    lastSeen: string;
    topFrameModName: string | null;
}

export interface ProbeDetail {
    dedupeKey: string;
    targetDeclaringTypeName: string;
    targetMethodName: string;
    targetDisplayName: string;
    rawStackTrace: string;
    occurrenceCount: number;
    firstSeen: string;
    lastSeen: string;
    frames: FrameInfo[];
}

export interface AddProbeRequest {
    target: MethodRef;
}

export interface ActiveProbe {
    id: string;
    targetDeclaringTypeName: string;
    targetMethodName: string;
    targetDisplayName: string;
    appliedAt: string;
    totalInvocationCount: number;
    uniqueHitCount: number;
    isActive: boolean;
    capReason: 'None' | 'InvocationCapReached' | 'ManuallyRemoved';
}

export interface AddProbeResult {
    success: boolean;
    error?: string;
    probe?: ActiveProbe;
}
