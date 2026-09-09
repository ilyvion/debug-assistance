<script setup lang="ts">
import { ref } from 'vue';

import { fetchFiles } from '../api';
import { t } from '../translations';
import type { FileBrowserEntry, FileBrowserShortcut } from '../types';

const props = defineProps<{
    modelValue: string;
    mode: 'file' | 'dir';
    extension?: string;
}>();

const emit = defineEmits<{
    'update:modelValue': [string];
}>();

const open = ref(false);
const currentPath = ref<string | null>(null);
const parentPath = ref<string | null>(null);
const entries = ref<FileBrowserEntry[]>([]);
const shortcuts = ref<FileBrowserShortcut[]>([]);
const loading = ref(false);
const pathError = ref<string | null>(null);

async function browse(path: string | null): Promise<boolean> {
    loading.value = true;
    pathError.value = null;
    try {
        const result = await fetchFiles(path, props.mode, props.extension);
        currentPath.value = result.currentPath;
        parentPath.value = result.parentPath;
        entries.value = result.entries;
        shortcuts.value = result.shortcuts;
        // Directory mode has no separate "select" step: wherever the player is currently
        // browsing is the chosen directory, so the bound path stays live in sync with it.
        if (props.mode === 'dir' && result.currentPath !== null) {
            emit('update:modelValue', result.currentPath);
        }
        return true;
    } catch (err) {
        pathError.value = err instanceof Error ? err.message : String(err);
        return false;
    } finally {
        loading.value = false;
    }
}

function toggleOpen() {
    open.value = !open.value;
    if (open.value) {
        const target = props.modelValue.trim();
        void browse(target !== '' ? target : null);
    }
}

async function goToPath() {
    const path = props.modelValue.trim();
    if (path === '') {
        return;
    }
    if (await browse(path)) {
        open.value = true;
    }
}

function shortcutLabel(shortcut: FileBrowserShortcut): string {
    switch (shortcut.kind) {
        case 'Mods':
            return t('FileBrowser.ShortcutMods');
        case 'Workshop':
            return t('FileBrowser.ShortcutWorkshop');
        default:
            return shortcut.kind;
    }
}

function enterEntry(entry: FileBrowserEntry) {
    if (entry.isDirectory) {
        void browse(entry.path);
    } else {
        // File mode has no separate "select" step either: picking a file is itself the
        // commit-worthy action, so it closes the browser immediately.
        emit('update:modelValue', entry.path);
        open.value = false;
    }
}

function goUp() {
    void browse(parentPath.value);
}

// Splits the current path into clickable ancestor segments, including the root itself (e.g. "/"
// on Unix, "C:\" on Windows) as its own crumb, since that's a valid navigation target too. When
// currentPath is null (the player is looking at the platform-root listing itself), there's
// nothing to build a trail out of — the root entries in the listing pane serve that role instead.
function breadcrumbs(): { name: string; path: string }[] {
    if (currentPath.value === null) {
        return [];
    }
    const sep = currentPath.value.includes('\\') ? '\\' : '/';
    const isAbsoluteUnix = sep === '/' && currentPath.value.startsWith('/');
    const driveMatch = /^([A-Za-z]:)\\/.exec(currentPath.value);

    const crumbs: { name: string; path: string }[] = [];
    let acc = '';
    if (isAbsoluteUnix) {
        crumbs.push({ name: '/', path: '/' });
        acc = '/';
    } else if (driveMatch) {
        const root = `${driveMatch[1]}\\`;
        crumbs.push({ name: root, path: root });
        acc = root;
    }

    const parts = currentPath.value.split(sep).filter((part) => part !== '');
    for (const part of parts) {
        if (driveMatch?.[1] === part) {
            continue;
        }
        acc = acc === '' || acc.endsWith(sep) ? acc + part : acc + sep + part;
        crumbs.push({ name: part, path: acc });
    }
    return crumbs;
}

function onTextInput(event: Event) {
    emit('update:modelValue', (event.target as HTMLInputElement).value);
}

function formatSize(size: number | null): string {
    if (size === null) {
        return '';
    }
    if (size < 1024) {
        return `${String(size)} B`;
    }
    const units = ['KB', 'MB', 'GB', 'TB'];
    let value = size / 1024;
    let unitIndex = 0;
    while (value >= 1024 && unitIndex < units.length - 1) {
        value /= 1024;
        unitIndex++;
    }
    return `${value.toFixed(1)} ${units[unitIndex]}`;
}
</script>

<template>
    <div class="file-browser">
        <div class="path-row">
            <input
                type="text"
                class="path-input"
                :class="{ invalid: pathError !== null }"
                :value="modelValue"
                :placeholder="t('FileBrowser.PathPlaceholder')"
                @input="onTextInput"
                @keydown.enter.prevent="goToPath"
            />
            <button
                type="button"
                :disabled="modelValue.trim() === ''"
                @click="goToPath"
            >
                {{ t('FileBrowser.Go') }}
            </button>
            <button type="button" @click="toggleOpen">
                {{ t('FileBrowser.Browse') }}
            </button>
        </div>
        <p v-if="pathError" class="path-error">{{ pathError }}</p>

        <div v-if="open" class="panel browse-panel">
            <div v-if="shortcuts.length > 0" class="shortcuts">
                <button
                    v-for="shortcut in shortcuts"
                    :key="shortcut.kind"
                    type="button"
                    class="shortcut"
                    @click="browse(shortcut.path)"
                >
                    {{ shortcutLabel(shortcut) }}
                </button>
            </div>

            <div class="toolbar browse-toolbar">
                <button
                    type="button"
                    :disabled="currentPath === null"
                    @click="goUp"
                >
                    {{ t('FileBrowser.Up') }}
                </button>
                <div class="breadcrumbs browse-breadcrumbs">
                    <template
                        v-for="(crumb, index) in breadcrumbs()"
                        :key="crumb.path"
                    >
                        <span
                            v-if="index > 0"
                            class="crumb-sep browse-crumb-sep"
                            >/</span
                        >
                        <button
                            type="button"
                            class="crumb browse-crumb"
                            @click="browse(crumb.path)"
                        >
                            {{ crumb.name }}
                        </button>
                    </template>
                </div>
            </div>

            <p v-if="loading" class="status">
                {{ t('FileBrowser.Loading') }}
            </p>
            <p v-else-if="entries.length === 0" class="status empty">
                {{ t('FileBrowser.Empty') }}
            </p>
            <ul v-else class="entries browse-list">
                <li
                    v-for="entry in entries"
                    :key="entry.path"
                    class="entry"
                    @click="enterEntry(entry)"
                >
                    <span class="icon">{{
                        entry.isDirectory ? '📁' : '📄'
                    }}</span>
                    <span class="name">{{ entry.name }}</span>
                    <span v-if="!entry.isDirectory" class="meta">
                        {{ formatSize(entry.size) }}
                        <template v-if="entry.lastWriteTime">
                            ·
                            {{ new Date(entry.lastWriteTime).toLocaleString() }}
                        </template>
                    </span>
                </li>
            </ul>

            <div class="actions">
                <button type="button" @click="open = false">
                    {{ t('FileBrowser.Close') }}
                </button>
            </div>
        </div>
    </div>
</template>

<style scoped>
.file-browser {
    display: flex;
    flex-direction: column;
    gap: 6px;
}

.path-row {
    display: flex;
    gap: 8px;
}

.path-input {
    flex: 1;
    min-width: 0;
}

.path-input.invalid {
    border-color: var(--danger);
}

.path-error {
    margin: 0;
    color: var(--danger);
    font-size: 12px;
}

.shortcuts {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
}

.shortcut {
    font-size: 12px;
    padding: 4px 8px;
}

.status {
    margin: 0;
    color: var(--text-muted);
}

.entry {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 8px;
    cursor: pointer;
}

.entry:hover {
    background: var(--bg-hover);
}

.entry .name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.entry .meta {
    color: var(--text-muted);
    font-size: 12px;
    flex-shrink: 0;
}

.actions {
    display: flex;
    gap: 8px;
    justify-content: flex-end;
}
</style>
