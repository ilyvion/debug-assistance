<script setup lang="ts">
import { ref, watch } from 'vue';

import { fetchProbeAiPrompt, fetchProbeDetail } from '../api';
import { settings } from '../settings';
import { t } from '../translations';
import type { BrowsedMethod, ProbeDetail } from '../types';
import FrameRow from './FrameRow.vue';

const props = defineProps<{
    dedupeKey: string;
}>();

const emit = defineEmits<{
    'patch-this-method': [BrowsedMethod, string];
}>();

const detail = ref<ProbeDetail | null>(null);
const loadError = ref<string | null>(null);

interface FrameRowHandle {
    decompileAllForFrame(onItemDone?: () => void): Promise<void>;
    itemCount(): number;
}

const frameRows = ref<(FrameRowHandle | null)[]>([]);
const decompilingAll = ref(false);
const decompileProgress = ref(0);
const decompileTotal = ref(0);

const copyingPrompt = ref(false);
const copyPromptError = ref<string | null>(null);
const promptCopied = ref(false);

const showRawTrace = ref(false);
const rawTraceCopyError = ref<string | null>(null);
const rawTraceCopied = ref(false);

async function load() {
    detail.value = null;
    loadError.value = null;
    frameRows.value = [];
    copyPromptError.value = null;
    promptCopied.value = false;
    showRawTrace.value = false;
    rawTraceCopyError.value = null;
    rawTraceCopied.value = false;
    try {
        detail.value = await fetchProbeDetail(props.dedupeKey);
    } catch (err) {
        loadError.value = err instanceof Error ? err.message : String(err);
    }
}

watch(() => props.dedupeKey, load, { immediate: true });

async function decompileAll() {
    const rows = frameRows.value.filter((row) => row !== null);
    decompileTotal.value = rows.reduce((sum, row) => sum + row.itemCount(), 0);
    decompileProgress.value = 0;
    decompilingAll.value = true;
    try {
        await Promise.all(
            rows.map((row) =>
                row.decompileAllForFrame(() => decompileProgress.value++),
            ),
        );
    } finally {
        decompilingAll.value = false;
    }
}

async function copyAiPrompt() {
    copyPromptError.value = null;
    promptCopied.value = false;
    copyingPrompt.value = true;
    try {
        const prompt = await fetchProbeAiPrompt(props.dedupeKey);
        await navigator.clipboard.writeText(prompt);
        promptCopied.value = true;
    } catch (err) {
        copyPromptError.value =
            err instanceof Error ? err.message : String(err);
    } finally {
        copyingPrompt.value = false;
    }
}

async function copyRawStackTrace() {
    if (!detail.value) {
        return;
    }
    rawTraceCopyError.value = null;
    rawTraceCopied.value = false;
    try {
        await navigator.clipboard.writeText(detail.value.rawStackTrace);
        rawTraceCopied.value = true;
    } catch (err) {
        rawTraceCopyError.value =
            err instanceof Error ? err.message : String(err);
    }
}

function formatDate(iso: string): string {
    return new Date(iso).toLocaleString();
}
</script>

<template>
    <div class="detail">
        <p v-if="loadError" class="status error">{{ loadError }}</p>
        <template v-else-if="detail">
            <header class="header">
                <h2>{{ detail.targetDisplayName }}</h2>
                <p class="meta">
                    {{
                        t(
                            'ProbeDetail.SeenSummary',
                            detail.occurrenceCount,
                            formatDate(detail.firstSeen),
                            formatDate(detail.lastSeen),
                        )
                    }}
                </p>
            </header>

            <button
                v-if="detail.frames.length > 0"
                class="decompile-all"
                :disabled="decompilingAll"
                @click="decompileAll"
            >
                {{ t('ProbeDetail.DecompileAll') }}
            </button>

            <div class="frames">
                <FrameRow
                    v-for="(frame, i) in detail.frames"
                    :key="frame.index"
                    :ref="
                        (el) =>
                            (frameRows[i] =
                                el as unknown as FrameRowHandle | null)
                    "
                    :dedupe-key="detail.dedupeKey"
                    :frame="frame"
                    kind="probe"
                    @patch-this-method="
                        emit('patch-this-method', $event, dedupeKey)
                    "
                />
            </div>

            <div class="trace-actions raw-trace-actions">
                <button
                    class="raw-trace-toggle"
                    @click="showRawTrace = !showRawTrace"
                >
                    {{
                        showRawTrace
                            ? t('ProbeDetail.HideStackTrace')
                            : t('ProbeDetail.ShowStackTrace')
                    }}
                </button>
                <button class="copy-raw-trace" @click="copyRawStackTrace">
                    {{ t('ProbeDetail.CopyStackTrace') }}
                </button>
            </div>
            <p v-if="rawTraceCopied" class="status">
                {{ t('ProbeDetail.CopyStackTraceCopied') }}
            </p>
            <p v-if="rawTraceCopyError" class="status error">
                {{ t('ProbeDetail.CopyStackTraceFailed', rawTraceCopyError) }}
            </p>
            <div v-if="showRawTrace" class="raw-trace">
                <pre class="raw-trace-text">{{ detail.rawStackTrace }}</pre>
            </div>

            <template v-if="settings.aiPromptGeneratorEnabled">
                <button
                    class="ai-prompt"
                    :disabled="copyingPrompt"
                    :title="t('ProbeDetail.CopyAiPromptTooltip')"
                    @click="copyAiPrompt"
                >
                    <span v-if="copyingPrompt" class="spinner" />
                    {{ t('ProbeDetail.CopyAiPrompt') }}
                </button>
                <p v-if="promptCopied" class="status">
                    {{ t('ProbeDetail.CopyAiPromptCopied') }}
                </p>
                <p v-if="copyPromptError" class="status error">
                    {{ t('ProbeDetail.CopyAiPromptFailed', copyPromptError) }}
                </p>
            </template>
        </template>

        <div v-if="decompilingAll" class="progress-overlay modal-overlay">
            <div class="progress-box">
                <span class="progress-spinner" />
                <p>
                    {{
                        t(
                            'ProbeDetail.DecompilingAllProgress',
                            decompileProgress,
                            decompileTotal,
                        )
                    }}
                </p>
                <progress :value="decompileProgress" :max="decompileTotal" />
            </div>
        </div>
    </div>
</template>

<style scoped>
.detail {
    height: 100%;
    overflow-y: auto;
    padding: 4px 4px 16px;
}

.header h2 {
    margin: 0 0 4px;
    font-size: 16px;
    overflow-wrap: anywhere;
}

.meta {
    margin: 2px 0;
    color: var(--text-muted);
    font-size: 12px;
}

.decompile-all {
    margin-top: 12px;
}

.frames {
    margin-top: 12px;
}

.trace-actions {
    display: flex;
    gap: 6px;
}

.trace-actions button {
    font-size: 12px;
    padding: 4px 8px;
}

.raw-trace-actions {
    margin-top: 12px;
}

.raw-trace {
    margin-top: 8px;
}

.raw-trace-text {
    background: var(--bg-alt);
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 8px;
    font-size: 12px;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
}

.ai-prompt {
    margin-top: 12px;
    width: 100%;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
}

.status {
    margin: 6px 0;
    font-size: 12px;
}

.progress-overlay {
    z-index: 20;
}

.progress-box {
    background: var(--bg-alt);
    border: 1px solid var(--border);
    border-radius: 10px;
    box-shadow: 0 8px 30px var(--shadow);
    padding: 24px 32px;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 10px;
    min-width: 260px;
}

.progress-box progress {
    width: 100%;
}

.progress-spinner {
    display: inline-block;
    width: 28px;
    height: 28px;
    border: 3px solid var(--accent);
    border-right-color: transparent;
    border-radius: 50%;
    animation: spin 0.7s linear infinite;
}
</style>
