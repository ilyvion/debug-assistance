<script setup lang="ts">
import { computed, ref } from 'vue';

import { t } from '../translations';
import type { ProbeListEntry } from '../types';

const props = defineProps<{
    entries: ProbeListEntry[];
    filterText: string;
    selectedKey: string | null;
}>();

const emit = defineEmits<{
    select: [dedupeKey: string];
    dismiss: [dedupeKey: string];
    'clear-all': [];
}>();

const pendingClearAll = ref(false);

const filtered = computed(() =>
    props.entries.filter((entry) =>
        `${entry.targetDisplayName} ${entry.targetDeclaringTypeName}`
            .toLowerCase()
            .includes(props.filterText.toLowerCase()),
    ),
);

function formatDate(iso: string): string {
    return new Date(iso).toLocaleString();
}

function confirmClearAll() {
    pendingClearAll.value = false;
    emit('clear-all');
}
</script>

<template>
    <div class="list">
        <div class="list-header">
            <button
                class="clear-all"
                :disabled="entries.length === 0"
                @click="pendingClearAll = true"
            >
                {{ t('ProbeList.ClearAll') }}
            </button>
        </div>
        <div v-if="pendingClearAll" class="confirm">
            {{ t('ProbeList.ClearAllConfirm', entries.length) }}
            <button @click="confirmClearAll">
                {{ t('ProbeList.Confirm') }}
            </button>
            <button @click="pendingClearAll = false">
                {{ t('ProbeList.Cancel') }}
            </button>
        </div>
        <p v-if="filtered.length === 0" class="empty">
            {{
                entries.length === 0
                    ? t('ProbeList.Empty')
                    : t('ProbeList.NoMatches')
            }}
        </p>
        <div
            v-for="entry in filtered"
            :key="entry.dedupeKey"
            class="entry"
            :class="{ selected: entry.dedupeKey === selectedKey }"
        >
            <button class="entry-main" @click="emit('select', entry.dedupeKey)">
                <div class="entry-header">
                    {{ entry.targetDisplayName }}
                </div>
                <div class="entry-sub">
                    {{
                        t(
                            'ProbeList.EntrySummary',
                            entry.topFrameModName ?? t('Common.Unresolved'),
                            entry.occurrenceCount,
                            formatDate(entry.lastSeen),
                        )
                    }}
                </div>
            </button>
            <button
                class="dismiss"
                :title="t('ProbeList.DismissTooltip')"
                @click="emit('dismiss', entry.dedupeKey)"
            >
                🗑
            </button>
        </div>
    </div>
</template>

<style scoped>
.list {
    display: flex;
    flex-direction: column;
    gap: 2px;
    overflow-y: auto;
    height: 100%;
}

.empty {
    color: var(--text-muted);
    padding: 12px;
    text-align: center;
}

.list-header {
    display: flex;
    justify-content: flex-end;
    gap: 6px;
    padding: 0 2px 4px;
}

.clear-all {
    font-size: 12px;
    padding: 4px 8px;
}

.confirm {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
    color: var(--text-muted);
    font-size: 13px;
    padding: 6px 8px;
}

.entry {
    display: flex;
    align-items: stretch;
    gap: 4px;
    border-radius: 6px;
}

.entry.selected {
    background: var(--bg-selected);
}

.entry:not(.selected) .entry-main:hover {
    background: var(--bg-hover);
}

.entry.selected .entry-main:hover {
    background: transparent;
}

.entry .dismiss:hover {
    background: var(--bg-danger-hover);
}

.entry-main {
    flex: 1;
    min-width: 0;
    display: block;
    text-align: left;
    background: transparent;
    border: none;
    border-radius: 6px;
    padding: 8px 10px;
    border-top-right-radius: 0;
    border-bottom-right-radius: 0;
}

.dismiss {
    flex: 0 0 auto;
    align-self: center;
    background: transparent;
    border: none;
    font-size: 12px;
    padding: 4px 8px;
    height: 100%;

    border-top-left-radius: 0;
    border-bottom-left-radius: 0;
}

.entry-header {
    font-weight: 600;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.entry-sub {
    color: var(--text-muted);
    font-size: 12px;
    margin-top: 2px;
}
</style>
