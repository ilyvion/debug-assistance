<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';

import {
    applyHotPatch,
    fetchConveniencePatches,
    rescanConveniencePatches,
} from '../api';
import { t } from '../translations';
import type { ConveniencePatch, MethodRef } from '../types';

const props = defineProps<{
    target: MethodRef;
}>();

const emit = defineEmits<{
    close: [];
    applied: [];
}>();

interface Item extends ConveniencePatch {
    id: number;
}

function describeError(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}

const items = ref<Item[]>([]);
const selectedIds = ref<Set<number>>(new Set());
const errors = ref<Map<number, string>>(new Map());
const loading = ref(false);
const rescanning = ref(false);
const loadError = ref<string | null>(null);
const applying = ref(false);

function setItems(patches: ConveniencePatch[]) {
    items.value = patches.map((patch, id) => ({ ...patch, id }));
    selectedIds.value = new Set();
    errors.value = new Map();
}

async function load() {
    loading.value = true;
    loadError.value = null;
    try {
        setItems(await fetchConveniencePatches(props.target));
    } catch (err) {
        loadError.value = describeError(err);
    } finally {
        loading.value = false;
    }
}

async function rescan() {
    rescanning.value = true;
    loadError.value = null;
    try {
        setItems(await rescanConveniencePatches(props.target));
    } catch (err) {
        loadError.value = describeError(err);
    } finally {
        rescanning.value = false;
    }
}

void load();

function toggle(id: number) {
    const next = new Set(selectedIds.value);
    if (next.has(id)) {
        next.delete(id);
    } else {
        next.add(id);
    }
    selectedIds.value = next;
}

function toggleAll() {
    selectedIds.value =
        selectedIds.value.size === items.value.length
            ? new Set()
            : new Set(items.value.map((item) => item.id));
}

function patchTypeClass(patchType: string): string {
    return `patch-type-${patchType.toLowerCase()}`;
}

function onKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
        emit('close');
    }
}
onMounted(() => {
    window.addEventListener('keydown', onKeydown);
});
onUnmounted(() => {
    window.removeEventListener('keydown', onKeydown);
});

// Unlike DiscoveredPatchesDialog, every item here shares the same (already fixed) target, so a
// successfully applied one is only unchecked rather than removed from the list -- the player can
// still see and re-apply it, e.g. against a different target after closing and reopening.
async function applySelected() {
    applying.value = true;
    const toApplyIds = new Set(selectedIds.value);
    const succeededIds = new Set<number>();
    for (const item of items.value) {
        if (!toApplyIds.has(item.id)) {
            continue;
        }
        try {
            const result = await applyHotPatch(
                props.target,
                item.patchMethod,
                item.patchType,
            );
            if (result.success) {
                succeededIds.add(item.id);
                errors.value.delete(item.id);
            } else {
                errors.value.set(
                    item.id,
                    result.error ?? t('HotPatch.ApplyFailed'),
                );
            }
        } catch (err) {
            errors.value.set(item.id, describeError(err));
        }
    }
    selectedIds.value = new Set(
        [...selectedIds.value].filter((id) => !succeededIds.has(id)),
    );
    applying.value = false;
    if (succeededIds.size > 0) {
        emit('applied');
    }
}
</script>

<template>
    <div class="overlay modal-overlay" @click.self="emit('close')">
        <div class="dialog modal-dialog">
            <header>
                <h3>{{ t('HotPatch.ConveniencePatchesTitle') }}</h3>
                <button class="close" @click="emit('close')">✕</button>
            </header>

            <p class="intro">{{ t('HotPatch.ConveniencePatchesIntro') }}</p>

            <p v-if="loadError" class="status error">{{ loadError }}</p>
            <p v-else-if="loading" class="status empty">
                {{ t('HotPatch.Loading') }}
            </p>
            <p v-else-if="items.length === 0" class="status empty">
                {{ t('HotPatch.NoConveniencePatches') }}
            </p>

            <template v-if="!loading && items.length > 0">
                <div class="toolbar">
                    <label class="select-all-label">
                        <input
                            type="checkbox"
                            :checked="
                                selectedIds.size === items.length &&
                                items.length > 0
                            "
                            @change="toggleAll"
                        />
                        {{ t('HotPatch.SelectAll') }}
                    </label>
                </div>

                <ul class="patch-list">
                    <li v-for="item in items" :key="item.id">
                        <div class="patch-row">
                            <input
                                type="checkbox"
                                :checked="selectedIds.has(item.id)"
                                @change="toggle(item.id)"
                            />
                            <span
                                class="patch-type-badge"
                                :class="patchTypeClass(item.patchType)"
                                >{{ item.patchType }}</span
                            >
                            <div class="name">{{ item.name }}</div>
                            <div class="description">
                                {{ item.description }}
                            </div>
                        </div>
                        <p v-if="errors.has(item.id)" class="status error">
                            {{ errors.get(item.id) }}
                        </p>
                    </li>
                </ul>
            </template>

            <div class="actions">
                <button
                    type="button"
                    class="primary"
                    :disabled="applying || selectedIds.size === 0"
                    @click="applySelected"
                >
                    <span v-if="applying" class="spinner" />
                    {{ t('HotPatch.ApplySelected', selectedIds.size) }}
                </button>
                <button type="button" :disabled="rescanning" @click="rescan">
                    <span v-if="rescanning" class="spinner" />
                    {{ t('HotPatch.RescanConveniencePatches') }}
                </button>
                <button type="button" @click="emit('close')">
                    {{ t('HotPatch.Close') }}
                </button>
            </div>
        </div>
    </div>
</template>

<style scoped>
.dialog {
    width: min(560px, 90vw);
    max-height: 80vh;
    display: flex;
    flex-direction: column;
}

.intro {
    margin: 0 0 10px;
    font-size: 13px;
    color: var(--text-muted);
}

.toolbar {
    margin-bottom: 8px;
    font-size: 13px;
}

.select-all-label {
    display: flex;
    align-items: center;
    gap: 6px;
    width: fit-content;
}

.patch-list {
    list-style: none;
    margin: 0;
    padding: 0;
    overflow-y: auto;
}

.patch-list li {
    padding: 6px 0;
    border-top: 1px solid var(--border);
}

.patch-list li:first-child {
    border-top: none;
}

.patch-row {
    display: grid;
    grid-template-columns: auto auto 1fr;
    column-gap: 8px;
    row-gap: 2px;
    align-items: start;
}

.patch-type-badge {
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

.patch-type-badge.patch-type-finalizer {
    background: var(--ctp-peach);
}

.name,
.description {
    grid-column: 3;
    overflow-wrap: anywhere;
}

.name {
    font-size: 13px;
    font-weight: 600;
}

.description {
    font-size: 12px;
    color: var(--text-muted);
}

.status {
    margin: 4px 0 0;
    font-size: 13px;
}

.status.empty {
    color: var(--text-muted);
}

.actions {
    display: flex;
    gap: 8px;
    margin-top: 12px;
}
</style>
