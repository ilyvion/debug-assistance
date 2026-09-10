<script setup lang="ts">
import { reactive, ref } from 'vue';

import {
    decompileCauseFrame,
    decompileCausePatch,
    decompileFrame,
    decompilePatch,
} from '../api';
import { t } from '../translations';
import type {
    BrowsedMethod,
    DecompileResult,
    FrameInfo,
    PatchInfo,
} from '../types';
import CodePanel from './CodePanel.vue';

const props = defineProps<{
    dedupeKey: string;
    frame: FrameInfo;
    // When set, this frame belongs to one of the error's inner-exception causes (index into
    // ErrorDetail.innerCauses) rather than the error's own top-level frames, and decompile/patch
    // requests are routed to that cause's own frame list instead.
    causeIndex?: number;
}>();

const emit = defineEmits<{ 'patch-this-method': [BrowsedMethod] }>();

function fetchOriginal(): Promise<DecompileResult> {
    return props.causeIndex != null
        ? decompileCauseFrame(
              props.dedupeKey,
              props.causeIndex,
              props.frame.index,
              false,
          )
        : decompileFrame(props.dedupeKey, props.frame.index, false);
}

function fetchPatched(): Promise<DecompileResult> {
    return props.causeIndex != null
        ? decompileCauseFrame(
              props.dedupeKey,
              props.causeIndex,
              props.frame.index,
              true,
          )
        : decompileFrame(props.dedupeKey, props.frame.index, true);
}

function fetchPatch(patchIndex: number): Promise<DecompileResult> {
    return props.causeIndex != null
        ? decompileCausePatch(
              props.dedupeKey,
              props.causeIndex,
              props.frame.index,
              patchIndex,
          )
        : decompilePatch(props.dedupeKey, props.frame.index, patchIndex);
}

function patchThisMethod() {
    if (props.frame.patchTarget) {
        emit('patch-this-method', props.frame.patchTarget);
    }
}

type PanelKey = 'original' | 'patched' | `patch-${number}`;

// A Set (not a single ref) so several panels within the same frame — e.g. the original method and
// two of its patches — can be open and decompiling at once instead of opening one forcing the
// others closed.
const openPanels = reactive(new Set<PanelKey>());
const loadingPanels = reactive(new Set<PanelKey>());
const results = ref<Partial<Record<PanelKey, DecompileResult>>>({});

// Panels opened by "decompile all" skip the scroll-to-highlighted-line behavior — with every
// frame's panels landing in a burst, scrolling to each one in turn just makes the page jump around
// instead of settling anywhere useful. An individual manual decompile still scrolls, since there
// the highlighted line is exactly what the user asked to see.
const noAutoScrollPanels = reactive(new Set<PanelKey>());

async function ensureFetched(
    key: PanelKey,
    fetcher: () => Promise<DecompileResult>,
) {
    if (key in results.value) {
        return;
    }
    loadingPanels.add(key);
    try {
        results.value[key] = await fetcher();
    } finally {
        loadingPanels.delete(key);
    }
}

async function toggle(key: PanelKey, fetcher: () => Promise<DecompileResult>) {
    if (openPanels.has(key)) {
        openPanels.delete(key);
        return;
    }

    noAutoScrollPanels.delete(key);
    openPanels.add(key);
    await ensureFetched(key, fetcher);
}

// A panel not yet decompiled reads "Decompile" (the action about to happen); once its result has
// been fetched at least once, the button becomes an expand/collapse toggle for the panel that's
// already there.
function toggleLabel(key: PanelKey, patched: boolean): string {
    if (!(key in results.value)) {
        return t(patched ? 'FrameRow.DecompilePatched' : 'FrameRow.Decompile');
    }
    if (openPanels.has(key)) {
        return t(patched ? 'FrameRow.CollapsePatched' : 'FrameRow.Collapse');
    }
    return t(patched ? 'FrameRow.ExpandPatched' : 'FrameRow.Expand');
}

function panelItems(): {
    key: PanelKey;
    fetcher: () => Promise<DecompileResult>;
}[] {
    const items: { key: PanelKey; fetcher: () => Promise<DecompileResult> }[] =
        [{ key: 'original', fetcher: fetchOriginal }];
    if (props.frame.patches.length > 0) {
        items.push({ key: 'patched', fetcher: fetchPatched });
    }
    for (const patch of props.frame.patches) {
        items.push({
            key: `patch-${patch.index}`,
            fetcher: () => fetchPatch(patch.index),
        });
    }
    return items;
}

// Opens and decompiles every panel belonging to this frame (its original method, its merged-patch
// view when patched, and each individual patch) — used by ErrorDetail's "decompile all".
// onItemDone is called once per panel as it settles, letting the caller track overall progress.
async function decompileAllForFrame(onItemDone?: () => void): Promise<void> {
    await Promise.all(
        panelItems().map(async ({ key, fetcher }) => {
            noAutoScrollPanels.add(key);
            openPanels.add(key);
            try {
                await ensureFetched(key, fetcher);
            } finally {
                onItemDone?.();
            }
        }),
    );
}

defineExpose({ decompileAllForFrame, itemCount: () => panelItems().length });

function fullyQualifiedName(
    declaringTypeName: string | null,
    methodName: string | null,
    displayName: string | null,
): string {
    if (displayName != null) {
        return displayName;
    }
    return declaringTypeName
        ? `${declaringTypeName}.${methodName ?? '?'}`
        : (methodName ?? '?');
}

function frameLabel(frame: FrameInfo): string {
    return frame.declaringTypeName != null || frame.methodName != null
        ? fullyQualifiedName(
              frame.declaringTypeName,
              frame.methodName,
              frame.displayName,
          )
        : frame.rawText;
}

function frameTooltip(frame: FrameInfo): string {
    return [
        t(
            'FrameRow.TooltipMod',
            frame.resolvedModName ?? t('Common.Unresolved'),
        ),
        t('FrameRow.TooltipAssembly', frame.resolvedAssemblyShortName ?? '?'),
    ].join('\n');
}

function patchTooltip(patch: PatchInfo): string {
    return [
        fullyQualifiedName(
            patch.declaringTypeName,
            patch.methodName,
            patch.displayName,
        ),
        t('FrameRow.TooltipPatchOwner', patch.ownerModId),
        t('FrameRow.TooltipPatchKind', patch.patchKind),
    ].join('\n');
}

function subText(frame: FrameInfo): string | null {
    if (frame.fileName) {
        const line = frame.lineNumber ?? '?';
        return frame.columnNumber != null
            ? `${frame.fileName}:${String(line)}:${String(frame.columnNumber)}`
            : `${frame.fileName}:${String(line)}`;
    }
    if (frame.ilOffset != null) {
        return `IL offset 0x${frame.ilOffset.toString(16).toUpperCase()}`;
    }
    return null;
}
</script>

<template>
    <div class="frame">
        <div class="row">
            <div class="row-text">
                <div class="row-label" :title="frameTooltip(frame)">
                    [{{ frame.resolvedModName ?? t('Common.Unresolved') }}]
                    {{ frameLabel(frame) }}
                </div>
                <div v-if="subText(frame)" class="row-subtext">
                    {{ subText(frame) }}
                </div>
            </div>
            <div class="row-actions">
                <button
                    :disabled="loadingPanels.has('original')"
                    @click="toggle('original', fetchOriginal)"
                >
                    <span
                        v-if="loadingPanels.has('original')"
                        class="spinner"
                    />
                    {{ toggleLabel('original', false) }}
                </button>
                <button
                    v-if="frame.patches.length > 0"
                    :disabled="loadingPanels.has('patched')"
                    @click="toggle('patched', fetchPatched)"
                >
                    <span v-if="loadingPanels.has('patched')" class="spinner" />
                    {{ toggleLabel('patched', true) }}
                </button>
                <button
                    class="patch-this-method"
                    :disabled="!frame.patchTarget"
                    :title="
                        frame.patchTarget
                            ? undefined
                            : t('FrameRow.PatchThisMethodUnavailable')
                    "
                    @click="patchThisMethod"
                >
                    {{ t('FrameRow.PatchThisMethod') }}
                </button>
            </div>
        </div>

        <div v-if="openPanels.has('original')" class="panel">
            <p v-if="loadingPanels.has('original')" class="status">
                <span class="spinner" />{{ t('FrameRow.Decompiling') }}
            </p>
            <p v-else-if="results.original?.error" class="status error">
                {{ results.original.error }}
            </p>
            <CodePanel
                v-else-if="results.original"
                :code="results.original.code ?? ''"
                :highlight-line="results.original.highlightLine"
                :auto-scroll="!noAutoScrollPanels.has('original')"
            />
        </div>
        <div v-if="openPanels.has('patched')" class="panel">
            <p v-if="loadingPanels.has('patched')" class="status">
                <span class="spinner" />{{ t('FrameRow.Decompiling') }}
            </p>
            <p v-else-if="results.patched?.error" class="status error">
                {{ results.patched.error }}
            </p>
            <CodePanel
                v-else-if="results.patched"
                :code="results.patched.code ?? ''"
                :highlight-line="results.patched.highlightLine"
                :auto-scroll="!noAutoScrollPanels.has('patched')"
            />
        </div>

        <div v-for="patch in frame.patches" :key="patch.index" class="patch">
            <div class="row">
                <div class="row-text">
                    <div class="row-label" :title="patchTooltip(patch)">
                        - {{ patch.patchKind.toUpperCase() }} [{{
                            patch.ownerModId
                        }}]
                        {{ patch.methodName ?? '?' }}
                    </div>
                </div>
                <div class="row-actions">
                    <button
                        :disabled="loadingPanels.has(`patch-${patch.index}`)"
                        @click="
                            toggle(`patch-${patch.index}`, () =>
                                fetchPatch(patch.index),
                            )
                        "
                    >
                        <span
                            v-if="loadingPanels.has(`patch-${patch.index}`)"
                            class="spinner"
                        />
                        {{ toggleLabel(`patch-${patch.index}`, false) }}
                    </button>
                </div>
            </div>
            <div v-if="openPanels.has(`patch-${patch.index}`)" class="panel">
                <p
                    v-if="loadingPanels.has(`patch-${patch.index}`)"
                    class="status"
                >
                    <span class="spinner" />{{ t('FrameRow.Decompiling') }}
                </p>
                <p
                    v-else-if="results[`patch-${patch.index}`]?.error"
                    class="status error"
                >
                    {{ results[`patch-${patch.index}`]?.error }}
                </p>
                <CodePanel
                    v-else-if="results[`patch-${patch.index}`]"
                    :code="results[`patch-${patch.index}`]?.code ?? ''"
                    :highlight-line="
                        results[`patch-${patch.index}`]?.highlightLine
                    "
                    :auto-scroll="
                        !noAutoScrollPanels.has(`patch-${patch.index}`)
                    "
                />
            </div>
        </div>
    </div>
</template>

<style scoped>
.frame {
    padding: 8px 0;
    border-bottom: 1px solid var(--border);
}

.patch {
    margin-top: 6px;
    padding-left: 20px;
}

.row {
    display: flex;
    align-items: flex-start;
    gap: 10px;
}

.row-text {
    flex: 1;
    min-width: 0;
}

.row-label {
    overflow-wrap: anywhere;
}

.row-subtext {
    color: var(--text-muted);
    font-size: 12px;
    margin-top: 2px;
}

.row-actions {
    display: flex;
    gap: 6px;
    flex-shrink: 0;
}

.row-actions button {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    padding: 4px 8px;
}

.panel {
    margin-top: 8px;
}

.status {
    margin: 0;
    display: flex;
    align-items: center;
    gap: 8px;
    color: var(--text-muted);
}
</style>
