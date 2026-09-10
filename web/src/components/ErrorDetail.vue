<script setup lang="ts">
import { computed, ref, watch } from 'vue';

import { fetchAiPrompt, fetchErrorDetail } from '../api';
import { formatErrorTitle } from '../errorTitle';
import { settings } from '../settings';
import { t } from '../translations';
import type { BrowsedMethod, ErrorDetail } from '../types';
import FrameRow from './FrameRow.vue';

const props = defineProps<{
    dedupeKey: string;
}>();

const emit = defineEmits<{
    'patch-this-method': [BrowsedMethod, string];
}>();

const detail = ref<ErrorDetail | null>(null);
const loadError = ref<string | null>(null);

// FrameRow's `.vue` component instance type isn't resolvable from a plain (non vue-tsc) type
// checker such as the one ESLint's type-aware rules run under, so a hand-written interface
// mirroring its defineExpose() call stands in for InstanceType<typeof FrameRow> here.
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

// detail.innerCauses is outermost-inner-first (index 0 is the wrapping exception's own
// InnerException); reversed here so the root cause — the actionable one — is what's shown first.
// originalIndex is preserved because the decompile/patch endpoints address a cause by its
// position in detail.innerCauses, not its display position.
const orderedCauses = computed(() =>
    detail.value
        ? detail.value.innerCauses
              .map((cause, originalIndex) => ({ cause, originalIndex }))
              .reverse()
        : [],
);
const openCauseTraces = ref<Set<number>>(new Set());
const causeTraceCopyError = ref<Map<number, string>>(new Map());
const causeTraceCopied = ref<Set<number>>(new Set());

function toggleCauseTrace(index: number) {
    const next = new Set(openCauseTraces.value);
    if (next.has(index)) {
        next.delete(index);
    } else {
        next.add(index);
    }
    openCauseTraces.value = next;
}

async function load() {
    detail.value = null;
    loadError.value = null;
    frameRows.value = [];
    copyPromptError.value = null;
    promptCopied.value = false;
    showRawTrace.value = false;
    rawTraceCopyError.value = null;
    rawTraceCopied.value = false;
    openCauseTraces.value = new Set();
    causeTraceCopyError.value = new Map();
    causeTraceCopied.value = new Set();
    try {
        detail.value = await fetchErrorDetail(props.dedupeKey);
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
        const prompt = await fetchAiPrompt(props.dedupeKey);
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

async function copyCauseStackTrace(index: number, rawStackTrace: string) {
    const nextErrors = new Map(causeTraceCopyError.value);
    nextErrors.delete(index);
    causeTraceCopyError.value = nextErrors;
    const nextCopied = new Set(causeTraceCopied.value);
    nextCopied.delete(index);
    causeTraceCopied.value = nextCopied;
    try {
        await navigator.clipboard.writeText(rawStackTrace);
        causeTraceCopied.value = new Set(causeTraceCopied.value).add(index);
    } catch (err) {
        causeTraceCopyError.value = new Map(causeTraceCopyError.value).set(
            index,
            err instanceof Error ? err.message : String(err),
        );
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
                <h2>
                    {{ formatErrorTitle(detail.errorTypeName, detail.message) }}
                </h2>
                <p class="meta">
                    {{
                        t(
                            'ErrorDetail.SeenSummary',
                            detail.occurrenceCount,
                            formatDate(detail.firstSeen),
                            formatDate(detail.lastSeen),
                        )
                    }}
                </p>
                <p v-if="detail.harmonyRefHash != null" class="meta">
                    {{
                        t(
                            'ErrorDetail.HarmonyRef',
                            detail.harmonyRefHash.toString(16).toUpperCase(),
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
                {{ t('ErrorDetail.DecompileAll') }}
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
                            ? t('ErrorDetail.HideStackTrace')
                            : t('ErrorDetail.ShowStackTrace')
                    }}
                </button>
                <button class="copy-raw-trace" @click="copyRawStackTrace">
                    {{ t('ErrorDetail.CopyStackTrace') }}
                </button>
            </div>
            <p v-if="rawTraceCopied" class="status">
                {{ t('ErrorDetail.CopyStackTraceCopied') }}
            </p>
            <p v-if="rawTraceCopyError" class="status error">
                {{ t('ErrorDetail.CopyStackTraceFailed', rawTraceCopyError) }}
            </p>
            <div v-if="showRawTrace" class="raw-trace">
                <pre class="raw-trace-text">{{ detail.rawStackTrace }}</pre>
            </div>

            <div v-if="orderedCauses.length > 0" class="causes">
                <div
                    v-for="({ cause, originalIndex }, i) in orderedCauses"
                    :key="originalIndex"
                    class="cause"
                >
                    <h3 class="cause-title">
                        {{
                            i === 0
                                ? t(
                                      'ErrorDetail.RootCause',
                                      formatErrorTitle(
                                          cause.errorTypeName,
                                          cause.message,
                                      ),
                                  )
                                : t(
                                      'ErrorDetail.CausedBy',
                                      formatErrorTitle(
                                          cause.errorTypeName,
                                          cause.message,
                                      ),
                                  )
                        }}
                    </h3>

                    <div v-if="cause.frames.length > 0" class="frames">
                        <FrameRow
                            v-for="frame in cause.frames"
                            :key="frame.index"
                            :dedupe-key="detail.dedupeKey"
                            :frame="frame"
                            :cause-index="originalIndex"
                            @patch-this-method="
                                emit('patch-this-method', $event, dedupeKey)
                            "
                        />
                    </div>

                    <div class="trace-actions cause-trace-actions">
                        <button
                            class="cause-trace-toggle"
                            @click="toggleCauseTrace(i)"
                        >
                            {{
                                openCauseTraces.has(i)
                                    ? t('ErrorDetail.HideStackTrace')
                                    : t('ErrorDetail.ShowStackTrace')
                            }}
                        </button>
                        <button
                            class="copy-cause-trace"
                            @click="copyCauseStackTrace(i, cause.rawStackTrace)"
                        >
                            {{ t('ErrorDetail.CopyStackTrace') }}
                        </button>
                    </div>
                    <p v-if="causeTraceCopied.has(i)" class="status">
                        {{ t('ErrorDetail.CopyStackTraceCopied') }}
                    </p>
                    <p v-if="causeTraceCopyError.has(i)" class="status error">
                        {{
                            t(
                                'ErrorDetail.CopyStackTraceFailed',
                                causeTraceCopyError.get(i) ?? '',
                            )
                        }}
                    </p>
                    <div v-if="openCauseTraces.has(i)" class="cause-trace">
                        <pre class="cause-trace-text">{{
                            cause.rawStackTrace
                        }}</pre>
                    </div>
                </div>
            </div>

            <template v-if="settings.aiPromptGeneratorEnabled">
                <button
                    class="ai-prompt"
                    :disabled="copyingPrompt"
                    :title="t('ErrorDetail.CopyAiPromptTooltip')"
                    @click="copyAiPrompt"
                >
                    <span v-if="copyingPrompt" class="spinner" />
                    {{ t('ErrorDetail.CopyAiPrompt') }}
                </button>
                <p v-if="promptCopied" class="status">
                    {{ t('ErrorDetail.CopyAiPromptCopied') }}
                </p>
                <p v-if="copyPromptError" class="status error">
                    {{ t('ErrorDetail.CopyAiPromptFailed', copyPromptError) }}
                </p>
            </template>
        </template>

        <div v-if="decompilingAll" class="progress-overlay modal-overlay">
            <div class="progress-box">
                <span class="progress-spinner" />
                <p>
                    {{
                        t(
                            'ErrorDetail.DecompilingAllProgress',
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

.causes {
    margin-top: 12px;
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.cause {
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 8px;
}

.cause-title {
    margin: 0 0 4px;
    font-size: 13px;
    overflow-wrap: anywhere;
}

.trace-actions {
    display: flex;
    gap: 6px;
}

.trace-actions button {
    font-size: 12px;
    padding: 4px 8px;
}

.cause-trace-actions {
    margin-top: 8px;
}

.cause-trace {
    margin-top: 6px;
}

.cause-trace-text {
    background: var(--bg-alt);
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 8px;
    font-size: 12px;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
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
