<script setup lang="ts">
import { ref, watch } from 'vue';

import {
    fetchHotPatchAssemblies,
    fetchHotPatchMethods,
    fetchHotPatchMethodsOfType,
    fetchHotPatchNamespaces,
    fetchHotPatchTypes,
} from '../api';
import { t } from '../translations';
import type {
    AssemblyEntry,
    BrowsedMethod,
    CompatibleWith,
    HarmonyPatchTypeName,
    NamespaceEntry,
    TypeEntry,
} from '../types';

const props = defineProps<{
    // null browses every currently loaded assembly (the target-method picker); a path browses
    // just the one assembly LiveAssemblyLoader has loaded from it (the patch-method picker).
    path: string | null;
    modelValue: BrowsedMethod | null;
    // Only meaningful for the patch-method picker: when both are set, narrows every fetched
    // method list to what PatchCompatibility.IsCompatible accepts for that target + patch type.
    // Left unset by the target-method picker, which has no target of its own to filter against.
    targetMethod?: BrowsedMethod | null;
    patchType?: HarmonyPatchTypeName | null;
}>();

const emit = defineEmits<{
    'update:modelValue': [BrowsedMethod | null];
}>();

const open = ref(props.modelValue === null);
const loading = ref(false);
const error = ref<string | null>(null);

// Assembly -> Namespace -> Type -> Member, drilled into one level at a time (mirrors
// FileBrowser's own breadcrumb-driven navigation) rather than an all-levels-expanded tree, since
// that keeps each list to a manageable size instead of dumping thousands of methods at once.
const browseLevel = ref<0 | 1 | 2 | 3>(0);
const assemblies = ref<AssemblyEntry[]>([]);
const namespaces = ref<NamespaceEntry[]>([]);
const types = ref<TypeEntry[]>([]);
const browsedMethods = ref<BrowsedMethod[]>([]);
const selectedAssembly = ref<AssemblyEntry | null>(null);
const selectedNamespace = ref<NamespaceEntry | null>(null);
const selectedType = ref<TypeEntry | null>(null);

const filterText = ref('');
const searchResults = ref<BrowsedMethod[]>([]);
let debounceHandle: number | undefined;
// Guards against a slower, earlier request's response overwriting a newer one that arrived
// first — each search() call claims the next number and only applies its result if it's still
// the most recently issued one by the time the response arrives.
let searchSequence = 0;

function describeError(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}

function isSearching(): boolean {
    return filterText.value.trim() !== '';
}

function compatibilityFilter(): CompatibleWith | null {
    return props.targetMethod && props.patchType
        ? {
              target: {
                  assemblyFullName: props.targetMethod.assemblyFullName,
                  metadataToken: props.targetMethod.metadataToken,
              },
              patchType: props.patchType,
          }
        : null;
}

// Every loaded assembly's every method is a huge list -- require a short filter first so the
// target-method picker doesn't fetch that by accident. The patch-method picker (a single,
// freshly loaded assembly) has no such floor.
function requiresLongerFilter(): boolean {
    return props.path === null && filterText.value.trim().length < 2;
}

function scheduleSearch() {
    if (debounceHandle !== undefined) {
        window.clearTimeout(debounceHandle);
    }
    debounceHandle = window.setTimeout(() => void search(), 250);
}

async function search() {
    if (requiresLongerFilter()) {
        searchResults.value = [];
        return;
    }
    const sequence = ++searchSequence;
    loading.value = true;
    error.value = null;
    try {
        const results = await fetchHotPatchMethods(
            props.path,
            filterText.value.trim() || undefined,
            compatibilityFilter(),
        );
        if (sequence === searchSequence) {
            searchResults.value = results;
        }
    } catch (err) {
        if (sequence === searchSequence) {
            error.value = describeError(err);
        }
    } finally {
        if (sequence === searchSequence) {
            loading.value = false;
        }
    }
}

// The patch-method picker (path set) only ever has the one assembly LiveAssemblyLoader just
// loaded -- skip straight past the assembly level instead of making the player click through a
// list with exactly one entry in it.
async function loadAssemblies() {
    loading.value = true;
    error.value = null;
    try {
        assemblies.value = await fetchHotPatchAssemblies(
            props.path,
            compatibilityFilter(),
        );
        if (props.path !== null && assemblies.value.length === 1) {
            await selectAssembly(assemblies.value[0]);
        } else {
            browseLevel.value = 0;
        }
    } catch (err) {
        error.value = describeError(err);
    } finally {
        loading.value = false;
    }
}

async function selectAssembly(assembly: AssemblyEntry) {
    selectedAssembly.value = assembly;
    selectedNamespace.value = null;
    selectedType.value = null;
    loading.value = true;
    error.value = null;
    try {
        namespaces.value = await fetchHotPatchNamespaces(
            assembly.fullName,
            compatibilityFilter(),
        );
        browseLevel.value = 1;
    } catch (err) {
        error.value = describeError(err);
    } finally {
        loading.value = false;
    }
}

async function selectNamespace(namespaceEntry: NamespaceEntry) {
    if (!selectedAssembly.value) {
        return;
    }
    selectedNamespace.value = namespaceEntry;
    selectedType.value = null;
    loading.value = true;
    error.value = null;
    try {
        types.value = await fetchHotPatchTypes(
            selectedAssembly.value.fullName,
            namespaceEntry.name,
            compatibilityFilter(),
        );
        browseLevel.value = 2;
    } catch (err) {
        error.value = describeError(err);
    } finally {
        loading.value = false;
    }
}

async function selectType(typeEntry: TypeEntry) {
    if (!selectedAssembly.value) {
        return;
    }
    selectedType.value = typeEntry;
    loading.value = true;
    error.value = null;
    try {
        browsedMethods.value = await fetchHotPatchMethodsOfType(
            selectedAssembly.value.fullName,
            typeEntry.fullName,
            compatibilityFilter(),
        );
        browseLevel.value = 3;
    } catch (err) {
        error.value = describeError(err);
    } finally {
        loading.value = false;
    }
}

function canGoUp(): boolean {
    return (
        browseLevel.value === 3 ||
        browseLevel.value === 2 ||
        (browseLevel.value === 1 && props.path === null)
    );
}

// Re-fetches the target level's list rather than just flipping browseLevel, since the
// compatibility filter (targetMethod/patchType) may have changed while browsing a deeper level --
// the watch below only re-fetches the leaf list currently on screen, so an ancestor level's cached
// namespaces/types would otherwise still reflect the old filter once the player navigates back up
// to it.
async function goToLevel(level: 0 | 1 | 2 | 3) {
    if (level === browseLevel.value) {
        return;
    }
    if (level === 3 && selectedType.value) {
        await selectType(selectedType.value);
    } else if (level === 2 && selectedNamespace.value) {
        await selectNamespace(selectedNamespace.value);
    } else if (level === 1 && selectedAssembly.value) {
        await selectAssembly(selectedAssembly.value);
    } else if (level === 0) {
        await loadAssemblies();
    }
}

function goUp() {
    if (browseLevel.value > 0) {
        void goToLevel((browseLevel.value - 1) as 0 | 1 | 2);
    }
}

function namespaceLabel(name: string): string {
    return name === '' ? t('HotPatch.GlobalNamespace') : name;
}

function breadcrumbs(): { name: string; level: 0 | 1 | 2 | 3 }[] {
    const crumbs: { name: string; level: 0 | 1 | 2 | 3 }[] = [];
    if (props.path === null) {
        crumbs.push({ name: t('HotPatch.AllAssemblies'), level: 0 });
    }
    if (selectedAssembly.value) {
        crumbs.push({ name: selectedAssembly.value.name, level: 1 });
    }
    if (selectedNamespace.value) {
        crumbs.push({
            name: namespaceLabel(selectedNamespace.value.name),
            level: 2,
        });
    }
    if (selectedType.value) {
        crumbs.push({ name: selectedType.value.name, level: 3 });
    }
    return crumbs;
}

function selectMethod(method: BrowsedMethod) {
    emit('update:modelValue', method);
    open.value = false;
}

function resetAndOpen() {
    browseLevel.value = 0;
    assemblies.value = [];
    namespaces.value = [];
    types.value = [];
    browsedMethods.value = [];
    selectedAssembly.value = null;
    selectedNamespace.value = null;
    selectedType.value = null;
    filterText.value = '';
    searchResults.value = [];
    open.value = true;
    void loadAssemblies();
}

watch(
    () => props.path,
    () => {
        emit('update:modelValue', null);
        resetAndOpen();
    },
);

// Unlike the path watch above, a target/patchType change doesn't reset browse position -- it only
// re-runs whichever fetch is currently backing the visible list, so re-narrowing which
// assemblies/namespaces/types/methods count as compatible doesn't kick the player back out to the
// assembly list. Re-running a level above the leaf also means a type or namespace that only
// existed because it had a now-incompatible method drops out of view immediately.
watch([() => props.targetMethod, () => props.patchType], () => {
    if (!open.value) {
        return;
    }
    if (isSearching()) {
        void search();
    } else if (browseLevel.value === 3 && selectedType.value) {
        void selectType(selectedType.value);
    } else if (browseLevel.value === 2 && selectedNamespace.value) {
        void selectNamespace(selectedNamespace.value);
    } else if (browseLevel.value === 1 && selectedAssembly.value) {
        void selectAssembly(selectedAssembly.value);
    } else if (browseLevel.value === 0) {
        void loadAssemblies();
    }
});

if (open.value) {
    void loadAssemblies();
}

// A nested type's DeclaringTypeName ("Outer+Inner") uses the same '+' nesting separator as
// DisplayTypeName's backend counterpart (HotPatchEndpoints.DisplayTypeName) -- mirrored here so
// the TypeEntry synthesized for navigateBackTo shows the same short name the type list itself
// would have shown.
function shortTypeName(method: BrowsedMethod): string {
    const prefix = `${method.namespace}.`;
    const withoutNamespace = method.declaringTypeName.startsWith(prefix)
        ? method.declaringTypeName.slice(prefix.length)
        : method.declaringTypeName;
    return withoutNamespace.replace(/\+/g, '.');
}

// Unlike resetAndOpen, this drills straight back down to the type `method` was chosen from
// instead of landing on the assembly root, so picking several patch methods off the same type in
// a row doesn't require re-navigating through it every time.
async function navigateBackTo(method: BrowsedMethod) {
    browseLevel.value = 0;
    assemblies.value = [];
    namespaces.value = [];
    types.value = [];
    browsedMethods.value = [];
    selectedAssembly.value = null;
    selectedNamespace.value = null;
    selectedType.value = null;
    filterText.value = '';
    searchResults.value = [];
    open.value = true;
    await selectAssembly({
        name: method.assemblyName,
        fullName: method.assemblyFullName,
        typeCount: 0,
    });
    await selectNamespace({ name: method.namespace, typeCount: 0 });
    await selectType({
        name: shortTypeName(method),
        fullName: method.declaringTypeName,
        methodCount: 0,
    });
}

function change() {
    const previousMethod = props.modelValue;
    emit('update:modelValue', null);
    if (previousMethod) {
        void navigateBackTo(previousMethod);
    } else {
        resetAndOpen();
    }
}

// Lets HotPatchView drive "Change" itself right after a successful Apply, so the player lands
// straight back in the type they just patched from instead of having to click Change manually.
defineExpose({ change });
</script>

<template>
    <div class="method-picker">
        <div v-if="modelValue && !open" class="selected">
            <code>{{ modelValue.signature }}</code>
            <span class="type">{{ modelValue.declaringTypeName }}</span>
            <span class="assembly">{{ modelValue.assemblyName }}</span>
            <button type="button" @click="change">
                {{ t('HotPatch.ChangeMethod') }}
            </button>
        </div>
        <div v-else class="browser browse-panel">
            <input
                v-model="filterText"
                type="search"
                class="filter-input"
                :placeholder="t('HotPatch.MethodFilterPlaceholder')"
                @input="scheduleSearch"
            />

            <template v-if="isSearching()">
                <p v-if="error" class="status error">{{ error }}</p>
                <p v-else-if="loading" class="status">
                    {{ t('HotPatch.SearchingMethods') }}
                </p>
                <p v-else-if="requiresLongerFilter()" class="status">
                    {{ t('HotPatch.TypeToSearchMethods') }}
                </p>
                <p v-else-if="searchResults.length === 0" class="status empty">
                    {{ t('HotPatch.NoMethodsFound') }}
                </p>
                <ul v-else class="results browse-list">
                    <li
                        v-for="method in searchResults"
                        :key="`${method.assemblyFullName}:${String(method.metadataToken)}`"
                    >
                        <button
                            type="button"
                            class="search-result"
                            @click="selectMethod(method)"
                        >
                            <code class="signature">{{
                                method.signature
                            }}</code>
                            <span class="method-meta">
                                <span class="type">{{
                                    method.declaringTypeName
                                }}</span>
                                <span class="assembly">{{
                                    method.assemblyName
                                }}</span>
                            </span>
                        </button>
                    </li>
                </ul>
            </template>

            <template v-else>
                <div class="toolbar browse-toolbar">
                    <button type="button" :disabled="!canGoUp()" @click="goUp">
                        {{ t('HotPatch.Up') }}
                    </button>
                    <div class="breadcrumbs browse-breadcrumbs">
                        <template
                            v-for="(crumb, index) in breadcrumbs()"
                            :key="crumb.level"
                        >
                            <span
                                v-if="index > 0"
                                class="crumb-sep browse-crumb-sep"
                                >/</span
                            >
                            <button
                                type="button"
                                class="crumb browse-crumb"
                                :disabled="crumb.level === browseLevel"
                                @click="goToLevel(crumb.level)"
                            >
                                {{ crumb.name }}
                            </button>
                        </template>
                    </div>
                </div>

                <p v-if="error" class="status error">{{ error }}</p>
                <p v-else-if="loading" class="status">
                    {{ t('HotPatch.Loading') }}
                </p>
                <template v-else-if="browseLevel === 0">
                    <p v-if="assemblies.length === 0" class="status empty">
                        {{ t('HotPatch.Empty') }}
                    </p>
                    <ul v-else class="results browse-list">
                        <li
                            v-for="assembly in assemblies"
                            :key="assembly.fullName"
                        >
                            <button
                                type="button"
                                @click="selectAssembly(assembly)"
                            >
                                <span class="name">{{ assembly.name }}</span>
                                <span class="meta">{{
                                    t('HotPatch.TypeCount', assembly.typeCount)
                                }}</span>
                            </button>
                        </li>
                    </ul>
                </template>
                <template v-else-if="browseLevel === 1">
                    <p v-if="namespaces.length === 0" class="status empty">
                        {{ t('HotPatch.Empty') }}
                    </p>
                    <ul v-else class="results browse-list">
                        <li v-for="ns in namespaces" :key="ns.name">
                            <button type="button" @click="selectNamespace(ns)">
                                <span class="name">{{
                                    namespaceLabel(ns.name)
                                }}</span>
                                <span class="meta">{{
                                    t('HotPatch.TypeCount', ns.typeCount)
                                }}</span>
                            </button>
                        </li>
                    </ul>
                </template>
                <template v-else-if="browseLevel === 2">
                    <p v-if="types.length === 0" class="status empty">
                        {{ t('HotPatch.Empty') }}
                    </p>
                    <ul v-else class="results browse-list">
                        <li
                            v-for="typeEntry in types"
                            :key="typeEntry.fullName"
                        >
                            <button
                                type="button"
                                @click="selectType(typeEntry)"
                            >
                                <span class="name">{{ typeEntry.name }}</span>
                                <span class="meta">{{
                                    t(
                                        'HotPatch.MethodCount',
                                        typeEntry.methodCount,
                                    )
                                }}</span>
                            </button>
                        </li>
                    </ul>
                </template>
                <template v-else>
                    <p v-if="browsedMethods.length === 0" class="status empty">
                        {{ t('HotPatch.NoMethodsFound') }}
                    </p>
                    <ul v-else class="results browse-list">
                        <li
                            v-for="method in browsedMethods"
                            :key="`${method.assemblyFullName}:${String(method.metadataToken)}`"
                        >
                            <button type="button" @click="selectMethod(method)">
                                <code>{{ method.signature }}</code>
                            </button>
                        </li>
                    </ul>
                </template>
            </template>
        </div>
    </div>
</template>

<style scoped>
.method-picker {
    display: flex;
    flex-direction: column;
    gap: 6px;
    flex: 1;
    min-height: 0;
}

.browser.browse-panel {
    flex: 1;
    min-height: 0;
}

.results {
    flex: 1;
    overflow-y: auto;
    min-height: 0;
}

.selected {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
}

.selected code {
    overflow-wrap: anywhere;
}

.assembly {
    color: var(--text-muted);
    font-size: 12px;
}

.filter-input {
    width: 100%;
}

.crumb:disabled {
    color: var(--text);
    font-weight: 600;
}

.status {
    margin: 0;
    color: var(--text-muted);
    font-size: 12px;
}

.results button {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    width: 100%;
    text-align: left;
    background: transparent;
    border: none;
    padding: 6px 8px;
}

.results button:hover {
    background: var(--bg-hover);
}

.results code {
    flex: 1 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.search-result {
    flex-direction: column;
    align-items: stretch;
    justify-content: flex-start;
    gap: 2px;
}

.search-result .signature {
    flex: initial;
    white-space: normal;
    overflow-wrap: anywhere;
}

.search-result .method-meta {
    flex-shrink: initial;
    gap: 6px;
    overflow: hidden;
}

.search-result .type,
.search-result .assembly {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    opacity: 0.75;
}

.results .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.results .meta {
    color: var(--text-muted);
    font-size: 12px;
    flex-shrink: 0;
}

.method-meta {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-shrink: 0;
}

.type {
    color: var(--text-muted);
    font-size: 12px;
}
</style>
