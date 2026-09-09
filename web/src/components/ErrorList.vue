<script setup lang="ts">
import { computed, ref } from 'vue';

import { formatErrorTitle } from '../errorTitle';
import { filterAndSort } from '../filter';
import { type InspectedCounts, isUnread } from '../inspected';
import { settings, toggleErrorCaptureEnabled } from '../settings';
import { t } from '../translations';
import type { ErrorListEntry } from '../types';

const props = withDefaults(
    defineProps<{
        entries: ErrorListEntry[];
        filterText: string;
        selectedKey: string | null;
        inspectedCounts?: InspectedCounts;
    }>(),
    { inspectedCounts: () => ({}) },
);

const emit = defineEmits<{
    select: [dedupeKey: string];
    dismiss: [dedupeKey: string];
    'clear-all': [];
    'mark-all-seen': [];
}>();

const pendingClearAll = ref(false);

const filtered = computed(() => filterAndSort(props.entries, props.filterText));

const hasUnread = computed(() =>
    props.entries.some((entry) => isUnread(entry, props.inspectedCounts)),
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
                class="capture-toggle"
                :class="{ paused: !settings.errorCaptureEnabled }"
                :title="
                    settings.errorCaptureEnabled
                        ? t('ErrorList.PauseCaptureTooltip')
                        : t('ErrorList.ResumeCaptureTooltip')
                "
                @click="toggleErrorCaptureEnabled"
            >
                {{
                    settings.errorCaptureEnabled
                        ? t('ErrorList.PauseCapture')
                        : t('ErrorList.ResumeCapture')
                }}
            </button>
            <button
                class="mark-all-seen"
                :disabled="!hasUnread"
                @click="emit('mark-all-seen')"
            >
                {{ t('ErrorList.MarkAllSeen') }}
            </button>
            <button
                class="clear-all"
                :disabled="entries.length === 0"
                @click="pendingClearAll = true"
            >
                {{ t('ErrorList.ClearAll') }}
            </button>
        </div>
        <div v-if="pendingClearAll" class="confirm">
            {{ t('ErrorList.ClearAllConfirm', entries.length) }}
            <button @click="confirmClearAll">
                {{ t('ErrorList.Confirm') }}
            </button>
            <button @click="pendingClearAll = false">
                {{ t('ErrorList.Cancel') }}
            </button>
        </div>
        <p v-if="filtered.length === 0" class="empty">
            {{
                entries.length === 0
                    ? t('ErrorList.Empty')
                    : t('ErrorList.NoMatches')
            }}
        </p>
        <div
            v-for="entry in filtered"
            :key="entry.dedupeKey"
            class="entry"
            :class="{
                selected: entry.dedupeKey === selectedKey,
                unread: isUnread(entry, inspectedCounts),
            }"
        >
            <button class="entry-main" @click="emit('select', entry.dedupeKey)">
                <div class="entry-header">
                    {{ formatErrorTitle(entry.errorTypeName, entry.message) }}
                </div>
                <div class="entry-sub">
                    {{
                        t(
                            'ErrorList.EntrySummary',
                            entry.topFrameModName ?? t('Common.Unresolved'),
                            entry.occurrenceCount,
                            formatDate(entry.lastSeen),
                        )
                    }}
                </div>
            </button>
            <button
                class="dismiss"
                :title="t('ErrorList.DismissTooltip')"
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

.capture-toggle,
.mark-all-seen,
.clear-all {
    font-size: 12px;
    padding: 4px 8px;
}

.capture-toggle {
    margin-right: auto;
}

.capture-toggle.paused {
    color: var(--danger);
    border-color: var(--danger);
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

.entry.unread:not(.selected) {
    background: var(--bg-unread);
}

.entry.selected {
    background: var(--bg-selected);
}

/* entry-main/dismiss are <button>s and get their own hover background here (rather than the row
   as a whole) so the row's 4px flex gap stays visible as a seam between them, letting the two
   pills light up independently on hover instead of merging into one flat block. This also
   overrides the global button:hover:not(:disabled) rule in style.css, which would otherwise paint
   them plain gray regardless of the row's unread/selected state. */
.entry:not(.unread):not(.selected) .entry-main:hover {
    background: var(--bg-hover);
}

.entry.unread:not(.selected) .entry-main:hover {
    background: var(--bg-unread-hover);
}

.entry.selected .entry-main:hover {
    background: transparent;
}

/* Dismiss always hovers to a danger color, independent of the row's unread/selected state -- it's
   a destructive action and should read as one regardless of what else is going on with the row. */
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
