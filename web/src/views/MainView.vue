<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { useRouter } from 'vue-router';

import { checkAlive, clearErrors, deleteError, fetchErrors } from '../api';
import ErrorDetail from '../components/ErrorDetail.vue';
import ErrorList from '../components/ErrorList.vue';
import SaveLoadDialog from '../components/SaveLoadDialog.vue';
import ThemeToggle from '../components/ThemeToggle.vue';
import { setPendingHotPatchTarget } from '../hotPatchNav';
import {
    type InspectedCounts,
    withAllInspected,
    withInspected,
    withoutInspected,
} from '../inspected';
import { t } from '../translations';
import type { BrowsedMethod, ErrorListEntry } from '../types';

const POLL_INTERVAL_MS = 3000;

const router = useRouter();

const entries = ref<ErrorListEntry[]>([]);
const filterText = ref('');
const selectedKey = ref<string | null>(null);
// Occurrence count each error had the last time it was looked at; in-memory only, see
// inspected.ts. Drives ErrorList's unread highlight.
const inspectedCounts = ref<InspectedCounts>({});
const alive = ref(true);
const showSaveLoad = ref(false);

// The topbar button (the "arbitrary method" case) opens with nothing pre-filled; a frame's
// own "Patch this method" action (the "easy mode") pre-fills the target from that frame's
// resolved method instead.
function openHotPatchFromScratch() {
    setPendingHotPatchTarget(null, null);
    void router.push('/hotpatch');
}

function openHotPatchForFrame(method: BrowsedMethod, dedupeKey: string) {
    setPendingHotPatchTarget(method, dedupeKey);
    void router.push('/hotpatch');
}

let pollHandle: number | undefined;

async function poll() {
    try {
        entries.value = await fetchErrors();
        alive.value = true;
        // Keep the selected entry from re-flagging itself as unread while it's the one actually
        // being looked at, e.g. if it receives a new duplicate while its detail pane is open.
        const selected = entries.value.find(
            (entry) => entry.dedupeKey === selectedKey.value,
        );
        if (selected) {
            inspectedCounts.value = withInspected(
                inspectedCounts.value,
                selected,
            );
        }
    } catch {
        alive.value = await checkAlive();
    }
}

function onSelect(dedupeKey: string) {
    selectedKey.value = dedupeKey;
    const entry = entries.value.find((e) => e.dedupeKey === dedupeKey);
    if (entry) {
        inspectedCounts.value = withInspected(inspectedCounts.value, entry);
    }
}

function onMarkAllSeen() {
    inspectedCounts.value = withAllInspected(
        inspectedCounts.value,
        entries.value,
    );
}

onMounted(() => {
    void poll();
    pollHandle = window.setInterval(() => void poll(), POLL_INTERVAL_MS);
});

onUnmounted(() => {
    if (pollHandle !== undefined) {
        window.clearInterval(pollHandle);
    }
});

function onLoaded() {
    selectedKey.value = null;
    showSaveLoad.value = false;
    void poll();
}

async function onDismiss(dedupeKey: string) {
    try {
        await deleteError(dedupeKey);
        if (selectedKey.value === dedupeKey) {
            selectedKey.value = null;
        }
    } catch {
        // entry may already be gone; the poll below resyncs the list either way
    }
    // Drop any recorded inspection count so a dedupeKey that later reappears (a fresh
    // occurrence after dismissal) isn't wrongly suppressed by a stale, now-unrelated count.
    inspectedCounts.value = withoutInspected(inspectedCounts.value, dedupeKey);
    await poll();
}

async function onClearAll() {
    try {
        await clearErrors();
    } catch {
        // resynced by the poll below regardless
    }
    selectedKey.value = null;
    inspectedCounts.value = {};
    await poll();
}
</script>

<template>
    <div class="app">
        <header class="topbar">
            <h1>DebugAssistance</h1>
            <input
                v-model="filterText"
                type="search"
                :placeholder="t('App.FilterPlaceholder')"
            />
            <span v-if="!alive" class="offline">{{
                t('App.ServerUnreachable')
            }}</span>
            <button @click="showSaveLoad = true">
                {{ t('SaveLoad.Title') }}
            </button>
            <button @click="openHotPatchFromScratch">
                {{ t('HotPatch.Title') }}
            </button>
            <ThemeToggle />
        </header>
        <main class="content">
            <div class="list-pane">
                <ErrorList
                    :entries="entries"
                    :filter-text="filterText"
                    :selected-key="selectedKey"
                    :inspected-counts="inspectedCounts"
                    @select="onSelect"
                    @dismiss="onDismiss"
                    @clear-all="onClearAll"
                    @mark-all-seen="onMarkAllSeen"
                />
            </div>
            <div class="detail-pane">
                <ErrorDetail
                    v-if="selectedKey"
                    :dedupe-key="selectedKey"
                    @patch-this-method="openHotPatchForFrame"
                />
                <p v-else class="placeholder">
                    {{ t('App.SelectErrorPlaceholder') }}
                </p>
            </div>
        </main>
        <SaveLoadDialog
            v-if="showSaveLoad"
            @close="showSaveLoad = false"
            @loaded="onLoaded"
        />
    </div>
</template>

<style scoped>
.app {
    display: flex;
    flex-direction: column;
    height: 100vh;
}

.topbar {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 10px 16px;
    border-bottom: 1px solid var(--border);
    background: var(--bg-alt);
}

.topbar h1 {
    font-size: 15px;
    margin: 0;
    white-space: nowrap;
}

.topbar input {
    flex: 1;
    max-width: 360px;
}

.offline {
    color: var(--danger);
    font-size: 12px;
}

.content {
    flex: 1;
    display: grid;
    grid-template-columns: minmax(260px, 340px) 1fr;
    min-height: 0;
}

.list-pane {
    border-right: 1px solid var(--border);
    padding: 8px;
    overflow: hidden;
    min-height: 0;
}

.detail-pane {
    padding: 16px;
    overflow: hidden;
    min-height: 0;
}

.placeholder {
    color: var(--text-muted);
    text-align: center;
    margin-top: 40px;
}
</style>
