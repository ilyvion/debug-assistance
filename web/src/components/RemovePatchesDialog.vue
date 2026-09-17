<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';

import { removeManyHotPatches } from '../api';
import { t } from '../translations';
import type { ActivePatch } from '../types';

const props = defineProps<{
    patches: ActivePatch[];
}>();

const emit = defineEmits<{
    close: [];
    removed: [];
}>();

const items = ref<ActivePatch[]>([...props.patches]);
const selectedIds = ref<Set<string>>(
    new Set(items.value.map((item) => item.id)),
);
const error = ref<string | null>(null);
const removing = ref(false);

function describeError(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}

function toggle(id: string) {
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

// Removes each successfully removed patch from the list (so retrying only re-attempts what's
// still pending) -- removeManyHotPatches doesn't report which of the requested ids failed and
// why, just which ones it actually removed, so a leftover id after the call just falls back to a
// single dialog-wide error instead of a per-row one.
async function removeSelected() {
    removing.value = true;
    error.value = null;
    const toRemoveIds = [...selectedIds.value];
    try {
        const removedIds = new Set(await removeManyHotPatches(toRemoveIds));
        items.value = items.value.filter((item) => !removedIds.has(item.id));
        selectedIds.value = new Set(
            [...selectedIds.value].filter((id) => !removedIds.has(id)),
        );
        if (removedIds.size > 0) {
            emit('removed');
        }
        if (items.value.length === 0) {
            emit('close');
        } else if (removedIds.size < toRemoveIds.length) {
            error.value = t('HotPatch.RemoveSelectedFailed');
        }
    } catch (err) {
        error.value = describeError(err);
    } finally {
        removing.value = false;
    }
}
</script>

<template>
    <div class="overlay modal-overlay" @click.self="emit('close')">
        <div class="dialog modal-dialog">
            <header>
                <h3>{{ t('HotPatch.RemovePatchesTitle') }}</h3>
                <button class="close" @click="emit('close')">✕</button>
            </header>

            <p class="intro">
                {{ t('HotPatch.RemoveSelectedConfirm', selectedIds.size) }}
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
                </li>
            </ul>

            <p v-if="error" class="status error">{{ error }}</p>

            <div class="actions">
                <button
                    type="button"
                    class="remove-confirm"
                    :disabled="removing || selectedIds.size === 0"
                    @click="removeSelected"
                >
                    <span v-if="removing" class="spinner" />
                    {{ t('HotPatch.RemoveSelected', selectedIds.size) }}
                </button>
                <button type="button" class="cancel" @click="emit('close')">
                    {{ t('HotPatch.Cancel') }}
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

.patch-type-badge.patch-type-replace {
    background: var(--ctp-red);
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
