<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue';

import {
    addProbe,
    applyHotPatch,
    fetchActiveHotPatches,
    fetchActiveProbes,
    fetchHotPatchDebugPrompt,
    fetchLoadedHotPatchAssemblies,
    fetchSuggestedScaffoldProjectName,
    loadHotPatchAssembly,
    removeActiveProbe,
    removeHotPatch,
    removeManyHotPatches,
    scaffoldHotPatchProject,
} from '../api';
import DiscoveredPatchesDialog from '../components/DiscoveredPatchesDialog.vue';
import FileBrowser from '../components/FileBrowser.vue';
import MethodPicker from '../components/MethodPicker.vue';
import PageHeader from '../components/PageHeader.vue';
import { loadLastPath, saveLastPath } from '../lastPaths';
import { settings } from '../settings';
import { t } from '../translations';
import type {
    ActivePatch,
    ActiveProbe,
    BrowsedMethod,
    DiscoveredPatch,
    HarmonyPatchTypeName,
    LoadedAssemblyEntry,
} from '../types';

const props = defineProps<{
    // Pre-fills the target-method picker from a captured frame's own resolved method (the
    // "Patch this method" entry point); null for the topbar's from-scratch entry point. Supplied
    // by the /hotpatch route's props function, which reads it from hotPatchNav.ts.
    initialTarget?: BrowsedMethod | null;
    // Set alongside initialTarget by the same "Patch this method" entry point -- identifies which
    // captured error the debug-prompt button below should describe. Null for the topbar's
    // from-scratch entry point, where there is no error to attribute a prompt to, so that
    // button never renders at all (see the template below).
    dedupeKey?: string | null;
}>();

function describeError(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}

const assemblyPath = ref(loadLastPath('assembly'));
watch(assemblyPath, (value) => {
    saveLastPath('assembly', value);
});
const loadedPath = ref<string | null>(null);
const loadedAssemblyName = ref<string | null>(null);
const loadedGeneration = ref<number | null>(null);
// Reloading the same path still yields a brand-new Assembly instance server-side (see
// LiveAssemblyLoader.Load) -- bumped on every successful load, and on every switch to a
// different already-loaded assembly, so the patch-method picker below remounts even when
// `loadedPath` itself hasn't changed, instead of showing a stale browsed tree.
const patchMethodPickerGeneration = ref(0);
const removedNotice = ref<string[] | null>(null);
const loadError = ref<string | null>(null);
const loading = ref(false);
// Set while a reload of a path with patches still active from its previous generation is
// awaiting the player's Remove/Keep choice -- the load itself hasn't happened yet at this point.
const pendingReload = ref<{ path: string; patches: string[] } | null>(null);

const loadedAssemblies = ref<LoadedAssemblyEntry[]>([]);
const showLoadedAssemblies = ref(false);
const loadedAssembliesError = ref<string | null>(null);

// Set right after a successful load/reload when PatchAttributeScanner found at least one
// [Harmony*]-attributed method with a resolvable target -- offered as a checklist so the player
// doesn't have to pick target/patch method/patch type by hand for a project that already carries
// them.
const discoveredPatches = ref<DiscoveredPatch[]>([]);
const showDiscoveredPatches = ref(false);

async function refreshLoadedAssemblies() {
    try {
        loadedAssemblies.value = await fetchLoadedHotPatchAssemblies();
        loadedAssembliesError.value = null;
    } catch (err) {
        loadedAssembliesError.value = describeError(err);
    }
}

void refreshLoadedAssemblies();

const patchMethod = ref<BrowsedMethod | null>(null);
const patchMethodPicker = ref<InstanceType<typeof MethodPicker> | null>(null);
const targetMethod = ref<BrowsedMethod | null>(props.initialTarget ?? null);
const patchType = ref<HarmonyPatchTypeName>('Prefix');
// Lets a player who suspects PatchCompatibility.IsCompatible got their case wrong fall back to
// browsing every method in the loaded assembly, unfiltered.
const compatibilityFilterEnabled = ref(true);
// Switching patch type can turn an already-selected patch method incompatible, so drive "Change"
// the same way a successful Apply does -- the player lands back in the type they chose it from
// and can re-select it in one click if it's still valid for the new patch type.
watch(patchType, () => {
    if (patchMethod.value) {
        patchMethodPicker.value?.change();
    }
});
const applyError = ref<string | null>(null);
const applying = ref(false);

const activePatches = ref<ActivePatch[]>([]);
const activeError = ref<string | null>(null);
const pendingRemove = ref<string | null>(null);
const selectedPatchIds = ref<Set<string>>(new Set());
const pendingBulkRemove = ref(false);

const activeProbes = ref<ActiveProbe[]>([]);
const activeProbesError = ref<string | null>(null);
const addingProbe = ref(false);
const addProbeError = ref<string | null>(null);
const pendingRemoveProbe = ref<string | null>(null);

const ACTIVE_PROBES_POLL_INTERVAL_MS = 3000;

async function refreshActiveProbes() {
    try {
        activeProbes.value = await fetchActiveProbes();
        activeProbesError.value = null;
    } catch (err) {
        activeProbesError.value = describeError(err);
    }
}

let activeProbesPollHandle: number | undefined;

onMounted(() => {
    void refreshActiveProbes();
    activeProbesPollHandle = window.setInterval(
        () => void refreshActiveProbes(),
        ACTIVE_PROBES_POLL_INTERVAL_MS,
    );
});

onUnmounted(() => {
    if (activeProbesPollHandle !== undefined) {
        window.clearInterval(activeProbesPollHandle);
    }
});

async function addProbeForTarget() {
    if (!targetMethod.value) {
        return;
    }
    addingProbe.value = true;
    addProbeError.value = null;
    try {
        const result = await addProbe({
            assemblyFullName: targetMethod.value.assemblyFullName,
            metadataToken: targetMethod.value.metadataToken,
        });
        if (result.success) {
            await refreshActiveProbes();
        } else {
            addProbeError.value = result.error ?? t('HotPatch.AddProbeFailed');
        }
    } catch (err) {
        addProbeError.value = describeError(err);
    } finally {
        addingProbe.value = false;
    }
}

async function removeProbe(id: string) {
    try {
        await removeActiveProbe(id);
        pendingRemoveProbe.value = null;
        await refreshActiveProbes();
    } catch (err) {
        activeProbesError.value = describeError(err);
    }
}

const scaffoldDirectory = ref(loadLastPath('scaffold-directory'));
watch(scaffoldDirectory, (value) => {
    saveLastPath('scaffold-directory', value);
});
const scaffoldProjectName = ref('');
const scaffolding = ref(false);
const scaffoldError = ref<string | null>(null);
const scaffoldSuccess = ref<string | null>(null);
// The last successfully scaffolded project's directory -- the debug-prompt button below needs a
// real, existing project to point the AI agent at, so it stays disabled until this is set.
const scaffoldedProjectDirectory = ref<string | null>(null);

const copyingDebugPrompt = ref(false);
const debugPromptError = ref<string | null>(null);
const debugPromptCopied = ref(false);

// Seeded once from whichever target the panel opened with (the pre-filled frame, or the
// generic default for the from-scratch entry point) -- the player can freely retype it afterwards,
// it doesn't track further changes to targetMethod below. Fetched from the server rather than
// built here so it always matches ProjectScaffolder.SuggestProjectName, including its truncation
// to RimWorld's file name length limit.
const initialTarget = props.initialTarget ?? null;
void fetchSuggestedScaffoldProjectName(
    initialTarget
        ? {
              assemblyFullName: initialTarget.assemblyFullName,
              metadataToken: initialTarget.metadataToken,
          }
        : null,
).then((name) => {
    scaffoldProjectName.value = name;
});

async function refreshActive() {
    try {
        activePatches.value = await fetchActiveHotPatches();
        activeError.value = null;
        const stillActive = new Set(
            activePatches.value.map((patch) => patch.id),
        );
        selectedPatchIds.value = new Set(
            [...selectedPatchIds.value].filter((id) => stillActive.has(id)),
        );
    } catch (err) {
        activeError.value = describeError(err);
    }
}

void refreshActive();

async function loadAssembly(removeOldPatches?: boolean) {
    const path = assemblyPath.value.trim();
    if (!path) {
        return;
    }
    loading.value = true;
    loadError.value = null;
    removedNotice.value = null;
    try {
        const result = await loadHotPatchAssembly(path, removeOldPatches);
        if (result.needsConfirmation) {
            pendingReload.value = {
                path,
                patches: result.patchesFromPreviousLoadDescriptions ?? [],
            };
            return;
        }
        pendingReload.value = null;
        loadedPath.value = path;
        loadedAssemblyName.value = result.assemblyName ?? null;
        loadedGeneration.value = result.generation ?? null;
        patchMethod.value = null;
        patchMethodPickerGeneration.value++;
        await refreshLoadedAssemblies();
        if (
            result.removedPatchDescriptions &&
            result.removedPatchDescriptions.length > 0
        ) {
            removedNotice.value = result.removedPatchDescriptions;
            await refreshActive();
        }
        if (result.discoveredPatches && result.discoveredPatches.length > 0) {
            discoveredPatches.value = result.discoveredPatches;
            showDiscoveredPatches.value = true;
        }
    } catch (err) {
        loadError.value = describeError(err);
    } finally {
        loading.value = false;
    }
}

function cancelReload() {
    pendingReload.value = null;
}

// Switches straight into a patch assembly already loaded this session.
function selectLoadedAssembly(entry: LoadedAssemblyEntry) {
    assemblyPath.value = entry.path;
    loadedPath.value = entry.path;
    loadedAssemblyName.value = entry.assemblyName;
    loadedGeneration.value = entry.generation;
    patchMethod.value = null;
    patchMethodPickerGeneration.value++;
    loadError.value = null;
    removedNotice.value = null;
    showLoadedAssemblies.value = false;
}

async function apply() {
    if (!targetMethod.value || !patchMethod.value || !loadedPath.value) {
        return;
    }
    applying.value = true;
    applyError.value = null;
    try {
        const result = await applyHotPatch(
            {
                assemblyFullName: targetMethod.value.assemblyFullName,
                metadataToken: targetMethod.value.metadataToken,
            },
            {
                assemblyFullName: patchMethod.value.assemblyFullName,
                metadataToken: patchMethod.value.metadataToken,
            },
            patchType.value,
            loadedPath.value,
        );
        if (result.success) {
            await refreshActive();
            // Picking another patch method almost always means patching a different method off
            // the same type, not reapplying the one just applied -- so drive "Change" for the
            // player and land them back where this one was chosen from.
            patchMethodPicker.value?.change();
        } else {
            applyError.value = result.error ?? t('HotPatch.ApplyFailed');
        }
    } catch (err) {
        applyError.value = describeError(err);
    } finally {
        applying.value = false;
    }
}

async function scaffold() {
    const directory = scaffoldDirectory.value.trim();
    const projectName = scaffoldProjectName.value.trim();
    if (!directory || !projectName) {
        return;
    }
    scaffolding.value = true;
    scaffoldError.value = null;
    scaffoldSuccess.value = null;
    try {
        const result = await scaffoldHotPatchProject(
            directory,
            projectName,
            targetMethod.value
                ? {
                      assemblyFullName: targetMethod.value.assemblyFullName,
                      metadataToken: targetMethod.value.metadataToken,
                  }
                : null,
        );
        if (result.success) {
            scaffoldedProjectDirectory.value =
                result.projectDirectory ?? directory;
            if (result.expectedAssemblyPath) {
                assemblyPath.value = result.expectedAssemblyPath;
            }
            scaffoldSuccess.value = t(
                'HotPatch.ScaffoldSuccess',
                result.projectDirectory ?? directory,
            );
        } else {
            scaffoldError.value = result.error ?? t('HotPatch.ScaffoldFailed');
        }
    } catch (err) {
        scaffoldError.value = describeError(err);
    } finally {
        scaffolding.value = false;
    }
}

async function copyDebugPrompt() {
    if (!props.dedupeKey || !scaffoldedProjectDirectory.value) {
        return;
    }
    debugPromptError.value = null;
    debugPromptCopied.value = false;
    copyingDebugPrompt.value = true;
    try {
        const prompt = await fetchHotPatchDebugPrompt(
            props.dedupeKey,
            scaffoldedProjectDirectory.value,
            targetMethod.value
                ? {
                      assemblyFullName: targetMethod.value.assemblyFullName,
                      metadataToken: targetMethod.value.metadataToken,
                  }
                : null,
        );
        await navigator.clipboard.writeText(prompt);
        debugPromptCopied.value = true;
    } catch (err) {
        debugPromptError.value = describeError(err);
    } finally {
        copyingDebugPrompt.value = false;
    }
}

async function remove(id: string) {
    try {
        await removeHotPatch(id);
        pendingRemove.value = null;
        await refreshActive();
    } catch (err) {
        activeError.value = describeError(err);
    }
}

function togglePatchSelected(id: string) {
    const next = new Set(selectedPatchIds.value);
    if (next.has(id)) {
        next.delete(id);
    } else {
        next.add(id);
    }
    selectedPatchIds.value = next;
}

function toggleSelectAllPatches() {
    selectedPatchIds.value =
        selectedPatchIds.value.size === activePatches.value.length
            ? new Set()
            : new Set(activePatches.value.map((patch) => patch.id));
}

function patchTypeClass(patchType: string): string {
    return `patch-type-${patchType.toLowerCase()}`;
}

async function removeSelected() {
    try {
        await removeManyHotPatches([...selectedPatchIds.value]);
        pendingBulkRemove.value = false;
        selectedPatchIds.value = new Set();
        await refreshActive();
    } catch (err) {
        activeError.value = describeError(err);
    }
}
</script>

<template>
    <div class="hotpatch-view">
        <PageHeader current="hotpatch" :title="t('HotPatch.Title')">
            <p class="warning">{{ t('HotPatch.NoSandboxWarning') }}</p>
        </PageHeader>

        <div class="hotpatch-body">
            <aside class="active-patches-pane">
                <section class="active-patches-section">
                    <h4>{{ t('HotPatch.ActivePatchesTitle') }}</h4>
                    <p v-if="activeError" class="status error">
                        {{ activeError }}
                    </p>
                    <p
                        v-else-if="activePatches.length === 0"
                        class="status empty"
                    >
                        {{ t('HotPatch.NoActivePatches') }}
                    </p>
                    <template v-else>
                        <div class="active-list-toolbar">
                            <label class="select-all-label">
                                <input
                                    type="checkbox"
                                    :checked="
                                        selectedPatchIds.size ===
                                        activePatches.length
                                    "
                                    @change="toggleSelectAllPatches"
                                />
                                {{ t('HotPatch.SelectAll') }}
                            </label>
                            <button
                                type="button"
                                :disabled="selectedPatchIds.size === 0"
                                @click="pendingBulkRemove = true"
                            >
                                {{
                                    t(
                                        'HotPatch.RemoveSelected',
                                        selectedPatchIds.size,
                                    )
                                }}
                            </button>
                        </div>
                        <div v-if="pendingBulkRemove" class="confirm bulk">
                            {{
                                t(
                                    'HotPatch.RemoveSelectedConfirm',
                                    selectedPatchIds.size,
                                )
                            }}
                            <button type="button" @click="removeSelected">
                                {{ t('HotPatch.Confirm') }}
                            </button>
                            <button
                                type="button"
                                @click="pendingBulkRemove = false"
                            >
                                {{ t('HotPatch.Cancel') }}
                            </button>
                        </div>
                        <ul class="active-list">
                            <li v-for="patch in activePatches" :key="patch.id">
                                <div class="active-list-row">
                                    <input
                                        type="checkbox"
                                        class="patch-checkbox"
                                        :checked="
                                            selectedPatchIds.has(patch.id)
                                        "
                                        @change="togglePatchSelected(patch.id)"
                                    />
                                    <button
                                        type="button"
                                        class="remove-patch"
                                        :title="t('HotPatch.Remove')"
                                        @click="pendingRemove = patch.id"
                                    >
                                        🗑
                                    </button>
                                    <span
                                        class="patch-type-badge"
                                        :class="patchTypeClass(patch.patchType)"
                                        >{{ patch.patchType }}</span
                                    >
                                    <div class="target">
                                        {{ patch.targetDescription }}
                                    </div>
                                    <div class="patch-summary-method">
                                        {{ patch.patchMethodDescription }}
                                    </div>
                                    <div class="patch-summary-source">
                                        {{ patch.sourceAssemblyName }}#{{
                                            patch.sourceAssemblyGeneration
                                        }}
                                    </div>
                                </div>
                                <div
                                    v-if="pendingRemove === patch.id"
                                    class="confirm"
                                >
                                    {{ t('HotPatch.RemoveConfirm') }}
                                    <button
                                        type="button"
                                        @click="remove(patch.id)"
                                    >
                                        {{ t('HotPatch.Confirm') }}
                                    </button>
                                    <button
                                        type="button"
                                        @click="pendingRemove = null"
                                    >
                                        {{ t('HotPatch.Cancel') }}
                                    </button>
                                </div>
                            </li>
                        </ul>
                    </template>
                </section>

                <section class="active-probes-section">
                    <h4>{{ t('HotPatch.ActiveProbesTitle') }}</h4>
                    <p v-if="activeProbesError" class="status error">
                        {{ activeProbesError }}
                    </p>
                    <p
                        v-else-if="activeProbes.length === 0"
                        class="status empty"
                    >
                        {{ t('HotPatch.NoActiveProbes') }}
                    </p>
                    <ul v-else class="active-probe-list">
                        <li v-for="probe in activeProbes" :key="probe.id">
                            <div class="active-probe-row">
                                <div class="target">
                                    {{ probe.targetDisplayName }}
                                </div>
                                <div class="probe-summary">
                                    {{
                                        t(
                                            'HotPatch.ProbeSummary',
                                            probe.totalInvocationCount,
                                            probe.uniqueHitCount,
                                        )
                                    }}
                                </div>
                                <p
                                    v-if="!probe.isActive"
                                    class="status warning probe-capped"
                                >
                                    {{ t('HotPatch.ProbeCapReached') }}
                                </p>
                                <button
                                    type="button"
                                    class="remove-patch"
                                    :title="t('HotPatch.StopProbe')"
                                    @click="pendingRemoveProbe = probe.id"
                                >
                                    🗑
                                </button>
                            </div>
                            <div
                                v-if="pendingRemoveProbe === probe.id"
                                class="confirm"
                            >
                                {{ t('HotPatch.StopProbeConfirm') }}
                                <button
                                    type="button"
                                    @click="removeProbe(probe.id)"
                                >
                                    {{ t('HotPatch.Confirm') }}
                                </button>
                                <button
                                    type="button"
                                    @click="pendingRemoveProbe = null"
                                >
                                    {{ t('HotPatch.Cancel') }}
                                </button>
                            </div>
                        </li>
                    </ul>
                </section>
            </aside>

            <div class="hotpatch-main">
                <div class="setup-row">
                    <section class="scaffold-section setup-card">
                        <h4>{{ t('HotPatch.ScaffoldSectionTitle') }}</h4>
                        <label class="scaffold-name-label">
                            {{ t('HotPatch.ScaffoldProjectNameLabel') }}
                            <input
                                v-model="scaffoldProjectName"
                                type="text"
                                class="scaffold-name-input"
                            />
                        </label>
                        <FileBrowser v-model="scaffoldDirectory" mode="dir" />
                        <div class="scaffold-buttons">
                            <button
                                type="button"
                                class="primary"
                                :disabled="
                                    scaffolding ||
                                    scaffoldDirectory.trim() === '' ||
                                    scaffoldProjectName.trim() === ''
                                "
                                @click="scaffold"
                            >
                                {{ t('HotPatch.Generate') }}
                            </button>
                            <button
                                v-if="
                                    dedupeKey &&
                                    settings.aiPromptGeneratorEnabled
                                "
                                type="button"
                                class="copy-debug-prompt"
                                :disabled="
                                    copyingDebugPrompt ||
                                    !scaffoldedProjectDirectory
                                "
                                :title="
                                    scaffoldedProjectDirectory
                                        ? t('HotPatch.CopyDebugPromptTooltip')
                                        : t(
                                              'HotPatch.CopyDebugPromptNeedsScaffold',
                                          )
                                "
                                @click="copyDebugPrompt"
                            >
                                <span
                                    v-if="copyingDebugPrompt"
                                    class="spinner"
                                />
                                {{ t('HotPatch.CopyDebugPrompt') }}
                            </button>
                        </div>
                        <p v-if="scaffoldError" class="status error">
                            {{ scaffoldError }}
                        </p>
                        <p v-if="scaffoldSuccess" class="status">
                            {{ scaffoldSuccess }}
                        </p>
                        <p v-if="debugPromptCopied" class="status">
                            {{ t('HotPatch.CopyDebugPromptCopied') }}
                        </p>
                        <p v-if="debugPromptError" class="status error">
                            {{
                                t(
                                    'HotPatch.CopyDebugPromptFailed',
                                    debugPromptError,
                                )
                            }}
                        </p>
                    </section>

                    <section class="assembly-section setup-card">
                        <h4>{{ t('HotPatch.AssemblySectionTitle') }}</h4>
                        <FileBrowser
                            v-model="assemblyPath"
                            mode="file"
                            extension=".dll"
                        />
                        <button
                            type="button"
                            class="primary"
                            :disabled="loading || assemblyPath.trim() === ''"
                            @click="loadAssembly()"
                        >
                            {{
                                loadedPath !== null &&
                                loadedPath === assemblyPath.trim()
                                    ? t('HotPatch.Reload')
                                    : t('HotPatch.Load')
                            }}
                        </button>
                        <p v-if="loadError" class="status error">
                            {{ loadError }}
                        </p>
                        <p v-if="loadedAssemblyName" class="status">
                            {{
                                t(
                                    'HotPatch.Loaded',
                                    loadedAssemblyName,
                                    loadedGeneration ?? 1,
                                )
                            }}
                        </p>
                        <div v-if="pendingReload" class="reload-confirm">
                            <p>
                                {{
                                    t(
                                        'HotPatch.ReloadWillLeaveOrRemovePatches',
                                        pendingReload.patches.length,
                                    )
                                }}
                            </p>
                            <ul>
                                <li
                                    v-for="(
                                        description, index
                                    ) in pendingReload.patches"
                                    :key="index"
                                >
                                    {{ description }}
                                </li>
                            </ul>
                            <div class="reload-confirm-buttons">
                                <button
                                    type="button"
                                    @click="loadAssembly(true)"
                                >
                                    {{ t('HotPatch.RemoveOldPatches') }}
                                </button>
                                <button
                                    type="button"
                                    @click="loadAssembly(false)"
                                >
                                    {{ t('HotPatch.KeepOldPatches') }}
                                </button>
                                <button type="button" @click="cancelReload">
                                    {{ t('HotPatch.Cancel') }}
                                </button>
                            </div>
                        </div>
                        <div v-if="removedNotice" class="status warning">
                            <p>
                                {{
                                    t(
                                        'HotPatch.PatchesRemovedBeforeReload',
                                        removedNotice.length,
                                    )
                                }}
                            </p>
                            <ul>
                                <li
                                    v-for="(
                                        description, index
                                    ) in removedNotice"
                                    :key="index"
                                >
                                    {{ description }}
                                </li>
                            </ul>
                        </div>

                        <div class="loaded-assemblies">
                            <button
                                type="button"
                                @click="
                                    showLoadedAssemblies = !showLoadedAssemblies
                                "
                            >
                                {{ t('HotPatch.LoadedAssembliesTitle') }}
                            </button>
                            <div
                                v-if="showLoadedAssemblies"
                                class="panel browse-panel"
                            >
                                <p
                                    v-if="loadedAssembliesError"
                                    class="status error"
                                >
                                    {{ loadedAssembliesError }}
                                </p>
                                <p
                                    v-else-if="loadedAssemblies.length === 0"
                                    class="status empty"
                                >
                                    {{ t('HotPatch.NoLoadedAssemblies') }}
                                </p>
                                <ul v-else class="browse-list">
                                    <li
                                        v-for="entry in loadedAssemblies"
                                        :key="entry.path"
                                        class="entry"
                                        @click="selectLoadedAssembly(entry)"
                                    >
                                        <span class="name"
                                            >{{ entry.assemblyName }}#{{
                                                entry.generation
                                            }}</span
                                        >
                                        <span class="meta">{{
                                            entry.path
                                        }}</span>
                                    </li>
                                </ul>
                            </div>
                        </div>
                    </section>
                </div>

                <div class="browse-row">
                    <!-- Independent of the patch assembly below -- MethodPicker with path=null
                    browses every currently loaded assembly on its own, so the target doesn't need
                    to wait on a patch assembly being loaded first (and a pre-filled target shows
                    up immediately). -->
                    <section class="method-pane target-method-section">
                        <h4>{{ t('HotPatch.TargetMethodSectionTitle') }}</h4>
                        <MethodPicker v-model="targetMethod" :path="null" />
                        <button
                            type="button"
                            class="add-probe"
                            :disabled="addingProbe || !targetMethod"
                            :title="t('HotPatch.AddProbeTooltip')"
                            @click="addProbeForTarget"
                        >
                            <span v-if="addingProbe" class="spinner" />
                            {{ t('HotPatch.AddProbe') }}
                        </button>
                        <p v-if="addProbeError" class="status error">
                            {{ addProbeError }}
                        </p>
                    </section>

                    <div v-if="loadedPath" class="method-pane patch-pane">
                        <section class="patch-method-section">
                            <h4>{{ t('HotPatch.PatchMethodSectionTitle') }}</h4>

                            <label class="patch-type-label">
                                {{ t('HotPatch.PatchTypeLabel') }}
                                <select v-model="patchType">
                                    <option value="Prefix">Prefix</option>
                                    <option value="Postfix">Postfix</option>
                                    <option value="Transpiler">
                                        Transpiler
                                    </option>
                                    <option value="Finalizer">Finalizer</option>
                                    <option value="Replace">Replace</option>
                                </select>
                            </label>

                            <p v-if="patchType === 'Replace'" class="warning">
                                {{ t('HotPatch.ReplaceWarning') }}
                            </p>

                            <label
                                class="compatibility-filter-label"
                                :title="
                                    t(
                                        'HotPatch.OnlyShowCompatibleMethodsTooltip',
                                    )
                                "
                            >
                                <input
                                    v-model="compatibilityFilterEnabled"
                                    type="checkbox"
                                />
                                {{ t('HotPatch.OnlyShowCompatibleMethods') }}
                            </label>

                            <MethodPicker
                                ref="patchMethodPicker"
                                :key="patchMethodPickerGeneration"
                                v-model="patchMethod"
                                :path="loadedPath"
                                :target-method="
                                    compatibilityFilterEnabled
                                        ? targetMethod
                                        : null
                                "
                                :patch-type="patchType"
                            />
                        </section>

                        <section class="apply-section">
                            <button
                                type="button"
                                class="primary"
                                :disabled="
                                    applying || !targetMethod || !patchMethod
                                "
                                @click="apply"
                            >
                                {{ t('HotPatch.Apply') }}
                            </button>
                            <p v-if="applyError" class="status error">
                                {{ applyError }}
                            </p>
                        </section>
                    </div>
                    <div v-else class="method-pane placeholder-pane">
                        <p class="status empty">
                            {{ t('HotPatch.PatchMethodNeedsAssembly') }}
                        </p>
                    </div>
                </div>
            </div>
        </div>

        <DiscoveredPatchesDialog
            v-if="showDiscoveredPatches && loadedPath"
            :patches="discoveredPatches"
            :source-assembly-path="loadedPath"
            @close="showDiscoveredPatches = false"
            @applied="refreshActive"
        />
    </div>
</template>

<style scoped>
.hotpatch-view {
    display: flex;
    flex-direction: column;
    height: 100vh;
}

.warning {
    margin: 0;
    color: var(--danger);
    font-size: 13px;
}

.hotpatch-body {
    flex: 1;
    display: grid;
    grid-template-columns: minmax(240px, 300px) 1fr;
    min-height: 0;
}

.active-patches-pane {
    border-right: 1px solid var(--border);
    padding: 12px;
    overflow-y: auto;
    min-height: 0;
}

.hotpatch-main {
    display: flex;
    flex-direction: column;
    gap: 16px;
    padding: 16px;
    min-height: 0;
    overflow-y: auto;
}

.setup-row {
    flex: 0 0 auto;
    display: flex;
    flex-wrap: wrap;
    gap: 16px;
}

.setup-card {
    flex: 1 1 360px;
    min-width: 0;
    background: var(--bg-alt);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 12px 14px;
}

.browse-row {
    flex: 1;
    display: flex;
    flex-wrap: wrap;
    gap: 16px;
    min-height: 420px;
}

.method-pane {
    flex: 1 1 420px;
    min-width: 0;
    display: flex;
    flex-direction: column;
    min-height: 0;
    background: var(--bg-alt);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 12px 14px;
}

.method-pane.patch-pane {
    gap: 14px;
}

.method-pane .patch-method-section {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
}

.method-pane .apply-section {
    flex: 0 0 auto;
}

.placeholder-pane {
    align-items: center;
    justify-content: center;
    background: transparent;
    border-style: dashed;
    text-align: center;
}

section h4 {
    margin: 0 0 8px;
    font-size: 13px;
}

.assembly-section button.primary {
    margin-top: 8px;
}

.scaffold-name-label {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;
    font-size: 13px;
}

.scaffold-name-input {
    flex: 1;
}

.scaffold-buttons {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 8px;
}

.copy-debug-prompt {
    display: inline-flex;
    align-items: center;
    gap: 6px;
}

.patch-type-label {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;
    font-size: 13px;
}

.compatibility-filter-label {
    display: flex;
    align-items: center;
    gap: 6px;
    margin-bottom: 8px;
    font-size: 13px;
    width: fit-content;
}

.status {
    margin: 6px 0 0;
    font-size: 13px;
}

.status.warning {
    color: var(--danger);
}

.status.empty {
    color: var(--text-muted);
}

.reload-confirm {
    margin-top: 8px;
    padding: 8px;
    border: 1px solid var(--border);
    border-radius: 6px;
    font-size: 13px;
}

.reload-confirm-buttons {
    display: flex;
    gap: 8px;
    margin-top: 6px;
}

.loaded-assemblies {
    margin-top: 8px;
}

.loaded-assemblies .browse-panel {
    margin-top: 6px;
}

.loaded-assemblies .entry {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 6px 8px;
    cursor: pointer;
    font-size: 13px;
}

.loaded-assemblies .entry:hover {
    background: var(--bg-hover);
}

.loaded-assemblies .name {
    flex-shrink: 0;
    font-weight: 600;
}

.loaded-assemblies .meta {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--text-muted);
    font-size: 12px;
}

.active-list-toolbar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 8px;
    font-size: 13px;
}

.select-all-label {
    display: flex;
    align-items: center;
    gap: 6px;
}

.active-list {
    list-style: none;
    margin: 0;
    padding: 0;
}

.active-list li {
    padding: 6px 0;
    border-top: 1px solid var(--border);
}

.active-list li:first-child {
    border-top: none;
}

/* Grid rather than flex so the checkbox/Remove button occupy a narrow first
column (stacked on their own rows) while the badge/target/method/source
lines share the full remaining width on theirs. */
.active-list-row {
    display: grid;
    grid-template-columns: auto 1fr;
    column-gap: 8px;
    row-gap: 2px;
    align-items: start;
}

.patch-checkbox {
    grid-column: 1;
    grid-row: 1;
    justify-self: center;
    margin-top: 3px;
}

.remove-patch {
    grid-column: 1;
    grid-row: 2;
    justify-self: center;
}

.patch-type-badge {
    grid-column: 2;
    grid-row: 1;
    justify-self: start;
    padding: 1px 6px;
    border-radius: 4px;
    font-size: 11px;
    font-weight: 600;
    line-height: 1.4;
    color: var(--accent-text);
    background: var(--ctp-overlay1);
}

.patch-type-badge.patch-type-prefix {
    background: var(--ctp-blue);
}

.patch-type-badge.patch-type-postfix {
    background: var(--ctp-green);
}

.patch-type-badge.patch-type-transpiler {
    background: var(--ctp-mauve);
}

.patch-type-badge.patch-type-finalizer {
    background: var(--ctp-peach);
}

.patch-type-badge.patch-type-replace {
    background: var(--ctp-red);
}

.target,
.patch-summary-method,
.patch-summary-source {
    grid-column: 2;
    overflow-wrap: anywhere;
}

.target {
    font-size: 13px;
    font-weight: 600;
}

.patch-summary-method,
.patch-summary-source {
    font-size: 12px;
    color: var(--text-muted);
}

.remove-patch {
    background: transparent;
    border: none;
    padding: 4px 6px;
    font-size: 12px;
}

.remove-patch:hover:not(:disabled) {
    background: var(--bg-danger-hover);
}

.confirm {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
    color: var(--text-muted);
    font-size: 13px;
    margin-top: 6px;
    width: 100%;
}

.confirm.bulk {
    margin-top: 0;
    margin-bottom: 8px;
}

.add-probe {
    margin-top: 8px;
    display: inline-flex;
    align-items: center;
    gap: 6px;
}

.active-probes-section {
    margin-top: 16px;
    padding-top: 12px;
    border-top: 1px solid var(--border);
}

.active-probe-list {
    list-style: none;
    margin: 0;
    padding: 0;
}

.active-probe-list li {
    padding: 6px 0;
    border-top: 1px solid var(--border);
}

.active-probe-list li:first-child {
    border-top: none;
}

.active-probe-row {
    display: flex;
    align-items: center;
    gap: 8px;
}

.active-probe-row .target {
    flex: 1;
    min-width: 0;
    font-size: 13px;
    font-weight: 600;
    overflow-wrap: anywhere;
}

.probe-summary {
    font-size: 12px;
    color: var(--text-muted);
    white-space: nowrap;
}

.probe-capped {
    margin: 2px 0 0;
    width: 100%;
}
</style>
