import type {
    ActivePatch,
    ApplyPatchResult,
    AssemblyEntry,
    BrowsedMethod,
    CompatibleWith,
    DecompileResult,
    ErrorDetail,
    ErrorListEntry,
    FileBrowserList,
    HarmonyPatchTypeName,
    LoadAssemblyResult,
    LoadedAssemblyEntry,
    MethodRef,
    ModSettings,
    NamespaceEntry,
    SaveFileEntry,
    ScaffoldResult,
    SuggestedProjectName,
    TypeEntry,
} from './types';

export class ApiError extends Error {
    constructor(
        message: string,
        public readonly status: number,
    ) {
        super(message);
    }
}

interface ErrorBody {
    error?: string;
}

async function parseJson<T>(res: Response): Promise<T & ErrorBody> {
    return (await res.json().catch(() => ({}))) as T & ErrorBody;
}

async function getJson<T>(path: string): Promise<T> {
    const res = await fetch(path);
    const body = await parseJson<T>(res);
    if (!res.ok) {
        throw new ApiError(
            body.error ?? `Request failed (${String(res.status)})`,
            res.status,
        );
    }
    return body;
}

// Decompile routes always return a well-formed body (either `{ code, highlightLine }` or
// `{ error }`) even on a 404/500 — the body itself is the result, so this never throws.
async function postForResult<T>(path: string): Promise<T & ErrorBody> {
    const res = await fetch(path, { method: 'POST' });
    return parseJson<T>(res);
}

async function postJson<T>(path: string, body: unknown): Promise<T> {
    const res = await fetch(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
    });
    const json = await parseJson<T>(res);
    if (!res.ok) {
        throw new ApiError(
            json.error ?? `Request failed (${String(res.status)})`,
            res.status,
        );
    }
    return json;
}

export async function fetchErrors(): Promise<ErrorListEntry[]> {
    const data = await getJson<{ errors: ErrorListEntry[] }>('/api/errors');
    return data.errors;
}

export async function fetchErrorDetail(
    dedupeKey: string,
): Promise<ErrorDetail> {
    return getJson<ErrorDetail>(`/api/errors/${encodeURIComponent(dedupeKey)}`);
}

export async function deleteError(dedupeKey: string): Promise<void> {
    const res = await fetch(`/api/errors/${encodeURIComponent(dedupeKey)}`, {
        method: 'DELETE',
    });
    if (!res.ok) {
        const body = await parseJson<object>(res);
        throw new ApiError(
            body.error ?? `Request failed (${String(res.status)})`,
            res.status,
        );
    }
}

export async function clearErrors(): Promise<{ clearedCount: number }> {
    const res = await fetch('/api/errors', { method: 'DELETE' });
    const body = await parseJson<{ clearedCount: number }>(res);
    if (!res.ok) {
        throw new ApiError(
            body.error ?? `Request failed (${String(res.status)})`,
            res.status,
        );
    }
    return body;
}

export async function decompileFrame(
    dedupeKey: string,
    frameIndex: number,
    patched: boolean,
): Promise<DecompileResult> {
    const action = patched ? 'decompile-patched' : 'decompile';
    return postForResult<DecompileResult>(
        `/api/errors/${encodeURIComponent(dedupeKey)}/frames/${String(frameIndex)}/${action}`,
    );
}

export async function decompilePatch(
    dedupeKey: string,
    frameIndex: number,
    patchIndex: number,
): Promise<DecompileResult> {
    return postForResult<DecompileResult>(
        `/api/errors/${encodeURIComponent(dedupeKey)}/frames/${String(frameIndex)}/patches/${String(patchIndex)}/decompile`,
    );
}

export async function fetchAiPrompt(dedupeKey: string): Promise<string> {
    const res = await fetch(
        `/api/errors/${encodeURIComponent(dedupeKey)}/ai-prompt`,
        { method: 'POST' },
    );
    const body = await parseJson<{ prompt: string }>(res);
    if (!res.ok) {
        throw new ApiError(
            body.error ?? `Request failed (${String(res.status)})`,
            res.status,
        );
    }
    return body.prompt;
}

export async function fetchSaves(): Promise<SaveFileEntry[]> {
    const data = await getJson<{ saves: SaveFileEntry[] }>('/api/saves');
    return data.saves;
}

export async function saveCapture(
    fileName: string,
    overwrite: boolean,
): Promise<{ occurrenceCount: number }> {
    return postJson('/api/saves', { fileName, overwrite });
}

export async function loadCapture(
    fileName: string,
    mode: 'merge' | 'replace',
): Promise<{ loadedCount: number }> {
    return postJson(`/api/saves/${encodeURIComponent(fileName)}/load`, {
        mode,
    });
}

export async function deleteSave(fileName: string): Promise<void> {
    const res = await fetch(`/api/saves/${encodeURIComponent(fileName)}`, {
        method: 'DELETE',
    });
    if (!res.ok) {
        const body = await parseJson<object>(res);
        throw new ApiError(
            body.error ?? `Request failed (${String(res.status)})`,
            res.status,
        );
    }
}

export async function fetchFiles(
    path: string | null,
    mode: 'file' | 'dir',
    extension?: string,
): Promise<FileBrowserList> {
    const params = new URLSearchParams({ mode });
    if (path !== null) {
        params.set('path', path);
    }
    if (extension) {
        params.set('extension', extension);
    }
    return getJson<FileBrowserList>(`/api/files?${params.toString()}`);
}

export async function fetchTranslations(): Promise<Record<string, string>> {
    const data = await getJson<{ translations: Record<string, string> }>(
        '/api/translations',
    );
    return data.translations;
}

export async function fetchSettings(): Promise<ModSettings> {
    return getJson<ModSettings>('/api/settings');
}

export async function setErrorCaptureEnabled(
    enabled: boolean,
): Promise<ModSettings> {
    return postJson('/api/settings/error-capture-enabled', { enabled });
}

export async function loadHotPatchAssembly(
    path: string,
    removeOldPatches?: boolean | null,
): Promise<LoadAssemblyResult> {
    return postJson('/api/hotpatch/assembly', {
        path,
        removeOldPatches: removeOldPatches ?? null,
    });
}

export async function fetchLoadedHotPatchAssemblies(): Promise<
    LoadedAssemblyEntry[]
> {
    const data = await getJson<{ assemblies: LoadedAssemblyEntry[] }>(
        '/api/hotpatch/loaded-assemblies',
    );
    return data.assemblies;
}

function setCompatibilityParams(
    params: URLSearchParams,
    compatibleWith?: CompatibleWith | null,
) {
    if (!compatibleWith) {
        return;
    }
    params.set(
        'targetAssemblyFullName',
        compatibleWith.target.assemblyFullName,
    );
    params.set(
        'targetMetadataToken',
        String(compatibleWith.target.metadataToken),
    );
    params.set('patchType', compatibleWith.patchType);
}

export async function fetchHotPatchMethods(
    path: string | null,
    filter?: string,
    compatibleWith?: CompatibleWith | null,
): Promise<BrowsedMethod[]> {
    const params = new URLSearchParams();
    if (path !== null) {
        params.set('path', path);
    }
    if (filter) {
        params.set('filter', filter);
    }
    setCompatibilityParams(params, compatibleWith);
    const query = params.toString();
    const data = await getJson<{ methods: BrowsedMethod[] }>(
        `/api/hotpatch/methods${query ? `?${query}` : ''}`,
    );
    return data.methods;
}

export async function fetchHotPatchAssemblies(
    path: string | null,
    compatibleWith?: CompatibleWith | null,
): Promise<AssemblyEntry[]> {
    const params = new URLSearchParams();
    if (path !== null) {
        params.set('path', path);
    }
    setCompatibilityParams(params, compatibleWith);
    const query = params.toString();
    const data = await getJson<{ assemblies: AssemblyEntry[] }>(
        `/api/hotpatch/assemblies${query ? `?${query}` : ''}`,
    );
    return data.assemblies;
}

export async function fetchHotPatchNamespaces(
    assemblyFullName: string,
    compatibleWith?: CompatibleWith | null,
): Promise<NamespaceEntry[]> {
    const params = new URLSearchParams({ assemblyFullName });
    setCompatibilityParams(params, compatibleWith);
    const data = await getJson<{ namespaces: NamespaceEntry[] }>(
        `/api/hotpatch/namespaces?${params.toString()}`,
    );
    return data.namespaces;
}

export async function fetchHotPatchTypes(
    assemblyFullName: string,
    namespaceName: string,
    compatibleWith?: CompatibleWith | null,
): Promise<TypeEntry[]> {
    const params = new URLSearchParams({
        assemblyFullName,
        namespace: namespaceName,
    });
    setCompatibilityParams(params, compatibleWith);
    const data = await getJson<{ types: TypeEntry[] }>(
        `/api/hotpatch/types?${params.toString()}`,
    );
    return data.types;
}

export async function fetchHotPatchMethodsOfType(
    assemblyFullName: string,
    typeFullName: string,
    compatibleWith?: CompatibleWith | null,
): Promise<BrowsedMethod[]> {
    const params = new URLSearchParams({ assemblyFullName, typeFullName });
    setCompatibilityParams(params, compatibleWith);
    const data = await getJson<{ methods: BrowsedMethod[] }>(
        `/api/hotpatch/methods?${params.toString()}`,
    );
    return data.methods;
}

export async function applyHotPatch(
    target: MethodRef,
    patchMethod: MethodRef,
    patchType: HarmonyPatchTypeName,
    sourceAssemblyPath: string,
): Promise<ApplyPatchResult> {
    return postJson('/api/hotpatch/apply', {
        target,
        patchMethod,
        patchType,
        sourceAssemblyPath,
    });
}

export async function removeHotPatch(
    id: string,
): Promise<{ removed: boolean }> {
    const res = await fetch(`/api/hotpatch/remove/${encodeURIComponent(id)}`, {
        method: 'POST',
    });
    const body = await parseJson<{ removed: boolean }>(res);
    if (!res.ok) {
        throw new ApiError(
            body.error ?? `Request failed (${String(res.status)})`,
            res.status,
        );
    }
    return body;
}

export async function scaffoldHotPatchProject(
    directoryPath: string,
    projectName: string,
    target: MethodRef | null,
): Promise<ScaffoldResult> {
    return postJson('/api/hotpatch/scaffold', {
        directoryPath,
        projectName,
        target,
    });
}

export async function fetchHotPatchDebugPrompt(
    dedupeKey: string,
    projectDirectory: string,
    target: MethodRef | null,
): Promise<string> {
    const body = await postJson<{ prompt: string }>(
        '/api/hotpatch/debug-prompt',
        {
            dedupeKey,
            projectDirectory,
            target,
        },
    );
    return body.prompt;
}

export async function fetchSuggestedScaffoldProjectName(
    target: MethodRef | null,
): Promise<string> {
    const params = new URLSearchParams();
    if (target) {
        params.set('assemblyFullName', target.assemblyFullName);
        params.set('metadataToken', String(target.metadataToken));
    }
    const data = await getJson<SuggestedProjectName>(
        `/api/hotpatch/scaffold/suggested-name?${params.toString()}`,
    );
    return data.projectName;
}

export async function fetchActiveHotPatches(): Promise<ActivePatch[]> {
    const data = await getJson<{ patches: ActivePatch[] }>(
        '/api/hotpatch/active',
    );
    return data.patches;
}

export async function checkAlive(): Promise<boolean> {
    try {
        const res = await fetch('/api/alive');
        return res.ok;
    } catch {
        return false;
    }
}
