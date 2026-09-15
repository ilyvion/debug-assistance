<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { useRouter } from 'vue-router';

import { checkAlive, clearProbes, fetchProbes, removeProbeHit } from '../api';
import PageHeader from '../components/PageHeader.vue';
import ProbeDetail from '../components/ProbeDetail.vue';
import ProbeList from '../components/ProbeList.vue';
import { setPendingHotPatchTarget } from '../hotPatchNav';
import { t } from '../translations';
import type { BrowsedMethod, ProbeListEntry } from '../types';

const POLL_INTERVAL_MS = 3000;

const router = useRouter();

const entries = ref<ProbeListEntry[]>([]);
const filterText = ref('');
const selectedKey = ref<string | null>(null);
const alive = ref(true);

function openHotPatchForFrame(method: BrowsedMethod, dedupeKey: string) {
    setPendingHotPatchTarget(method, dedupeKey);
    void router.push('/hotpatch');
}

let pollHandle: number | undefined;

async function poll() {
    try {
        entries.value = await fetchProbes();
        alive.value = true;
    } catch {
        alive.value = await checkAlive();
    }
}

function onSelect(dedupeKey: string) {
    selectedKey.value = dedupeKey;
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

async function onDismiss(dedupeKey: string) {
    try {
        await removeProbeHit(dedupeKey);
        if (selectedKey.value === dedupeKey) {
            selectedKey.value = null;
        }
    } catch {
        // entry may already be gone; the poll below resyncs the list either way
    }
    await poll();
}

async function onClearAll() {
    try {
        await clearProbes();
    } catch {
        // resynced by the poll below regardless
    }
    selectedKey.value = null;
    await poll();
}
</script>

<template>
    <div class="probes-view">
        <PageHeader current="probes" :title="t('Probes.Title')">
            <input
                v-model="filterText"
                type="search"
                class="filter-input"
                :placeholder="t('Probes.FilterPlaceholder')"
            />
            <span v-if="!alive" class="offline">{{
                t('App.ServerUnreachable')
            }}</span>
        </PageHeader>
        <main class="content">
            <div class="list-pane">
                <ProbeList
                    :entries="entries"
                    :filter-text="filterText"
                    :selected-key="selectedKey"
                    @select="onSelect"
                    @dismiss="onDismiss"
                    @clear-all="onClearAll"
                />
            </div>
            <div class="detail-pane">
                <ProbeDetail
                    v-if="selectedKey"
                    :dedupe-key="selectedKey"
                    @patch-this-method="openHotPatchForFrame"
                />
                <p v-else class="placeholder">
                    {{ t('Probes.SelectPlaceholder') }}
                </p>
            </div>
        </main>
    </div>
</template>

<style scoped>
.probes-view {
    display: flex;
    flex-direction: column;
    height: 100vh;
}

.filter-input {
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
