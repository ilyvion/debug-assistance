<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';

import { t } from '../translations';
import type { ActivePatch } from '../types';

const props = defineProps<{
    patches: ActivePatch[];
}>();

const emit = defineEmits<{
    cancel: [];
    confirm: [removePatchIds: string[]];
}>();

const items = ref<ActivePatch[]>([...props.patches]);
const selectedIds = ref<Set<string>>(
    new Set(items.value.map((item) => item.id)),
);

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
        emit('cancel');
    }
}
onMounted(() => {
    window.addEventListener('keydown', onKeydown);
});
onUnmounted(() => {
    window.removeEventListener('keydown', onKeydown);
});

function confirm() {
    emit('confirm', [...selectedIds.value]);
}
</script>

<template>
    <div class="overlay modal-overlay" @click.self="emit('cancel')">
        <div class="dialog modal-dialog">
            <header>
                <h3>{{ t('HotPatch.ReloadPatchesTitle') }}</h3>
                <button class="close" @click="emit('cancel')">✕</button>
            </header>

            <p class="intro">
                {{ t('HotPatch.ReloadWillLeaveOrRemovePatches', items.length) }}
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

            <div class="actions">
                <button type="button" class="remove-confirm" @click="confirm">
                    {{
                        selectedIds.size > 0
                            ? t(
                                  'HotPatch.RemoveSelectedAndReload',
                                  selectedIds.size,
                              )
                            : t('HotPatch.KeepOldPatches')
                    }}
                </button>
                <button type="button" class="cancel" @click="emit('cancel')">
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

.actions {
    display: flex;
    gap: 8px;
    margin-top: 12px;
}
</style>
