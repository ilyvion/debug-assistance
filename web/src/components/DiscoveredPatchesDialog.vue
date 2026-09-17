<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';

import { applyHotPatch } from '../api';
import { t } from '../translations';
import type { DiscoveredPatch } from '../types';

const props = defineProps<{
    patches: DiscoveredPatch[];
    sourceAssemblyPath: string;
}>();

const emit = defineEmits<{
    close: [];
    applied: [];
}>();

interface Item extends DiscoveredPatch {
    id: number;
}

const items = ref<Item[]>(props.patches.map((patch, id) => ({ ...patch, id })));
const selectedIds = ref<Set<number>>(
    new Set(items.value.map((item) => item.id)),
);
const errors = ref<Map<number, string>>(new Map());
const applying = ref(false);

function describeError(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}

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

// Removes each successfully applied patch from the list (so retrying only re-attempts what's
// still pending), leaving a failed one in place with its error shown so the player can retry it.
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
                item.target,
                item.patchMethod,
                item.patchType,
                props.sourceAssemblyPath,
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
    items.value = items.value.filter((item) => !succeededIds.has(item.id));
    selectedIds.value = new Set(
        [...selectedIds.value].filter((id) => !succeededIds.has(id)),
    );
    applying.value = false;
    if (succeededIds.size > 0) {
        emit('applied');
    }
    if (items.value.length === 0) {
        emit('close');
    }
}
</script>

<template>
    <div class="overlay modal-overlay" @click.self="emit('close')">
        <div class="dialog modal-dialog">
            <header>
                <h3>{{ t('HotPatch.DiscoveredPatchesTitle') }}</h3>
                <button class="close" @click="emit('close')">✕</button>
            </header>

            <p class="intro">
                {{ t('HotPatch.DiscoveredPatchesIntro', items.length) }}
            </p>

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
                        <div class="target">{{ item.targetDescription }}</div>
                        <div class="patch-method">
                            {{ item.patchMethodDescription }}
                        </div>
                    </div>
                    <p v-if="errors.has(item.id)" class="status error">
                        {{ errors.get(item.id) }}
                    </p>
                </li>
            </ul>

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

.patch-type-badge.patch-type-transpiler {
    background: var(--ctp-mauve);
}

.patch-type-badge.patch-type-finalizer {
    background: var(--ctp-peach);
}

.target,
.patch-method {
    grid-column: 3;
    overflow-wrap: anywhere;
}

.target {
    font-size: 13px;
    font-weight: 600;
}

.patch-method {
    font-size: 12px;
    color: var(--text-muted);
}

.status {
    margin: 4px 0 0;
    font-size: 13px;
}

.actions {
    display: flex;
    gap: 8px;
    margin-top: 12px;
}
</style>
