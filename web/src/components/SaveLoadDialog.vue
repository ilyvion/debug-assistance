<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';

import {
    ApiError,
    deleteSave,
    fetchSaves,
    loadCapture,
    saveCapture,
} from '../api';
import { defaultSaveName } from '../saveName';
import { t } from '../translations';
import type { SaveFileEntry } from '../types';

const emit = defineEmits<{
    close: [];
    loaded: [];
}>();

const saves = ref<SaveFileEntry[]>([]);
const error = ref<string | null>(null);
const info = ref<string | null>(null);
const saveName = ref(defaultSaveName());
const busy = ref(false);
const confirmOverwrite = ref(false);
const pendingDelete = ref<string | null>(null);
const pendingReplace = ref<string | null>(null);

function describeError(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}

async function refresh() {
    try {
        saves.value = await fetchSaves();
    } catch (err) {
        error.value = describeError(err);
    }
}

onMounted(() => void refresh());

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

async function doSave(overwrite: boolean) {
    error.value = null;
    info.value = null;
    busy.value = true;
    try {
        const result = await saveCapture(saveName.value, overwrite);
        info.value = t('SaveLoad.Saved', result.occurrenceCount);
        confirmOverwrite.value = false;
        await refresh();
    } catch (err) {
        if (err instanceof ApiError && err.status === 409) {
            confirmOverwrite.value = true;
        } else {
            error.value = describeError(err);
        }
    } finally {
        busy.value = false;
    }
}

async function doLoad(fileName: string, mode: 'merge' | 'replace') {
    error.value = null;
    info.value = null;
    busy.value = true;
    try {
        const result = await loadCapture(fileName, mode);
        info.value = t('SaveLoad.Loaded', result.loadedCount);
        pendingReplace.value = null;
        emit('loaded');
    } catch (err) {
        error.value = describeError(err);
    } finally {
        busy.value = false;
    }
}

async function doDelete(fileName: string) {
    error.value = null;
    busy.value = true;
    try {
        await deleteSave(fileName);
        pendingDelete.value = null;
        await refresh();
    } catch (err) {
        error.value = describeError(err);
    } finally {
        busy.value = false;
    }
}
</script>

<template>
    <div class="overlay modal-overlay" @click.self="emit('close')">
        <div class="dialog modal-dialog">
            <header>
                <h3>{{ t('SaveLoad.Title') }}</h3>
                <button class="close" @click="emit('close')">✕</button>
            </header>

            <section class="save-section">
                <label class="save-label">
                    {{ t('SaveLoad.SaveAsLabel') }}
                    <input v-model="saveName" type="text" />
                </label>
                <button
                    class="primary"
                    :disabled="busy || saveName.trim() === ''"
                    @click="doSave(false)"
                >
                    {{ t('SaveLoad.SaveButton') }}
                </button>
                <div v-if="confirmOverwrite" class="confirm">
                    {{ t('SaveLoad.AlreadyExists', saveName) }}
                    <button :disabled="busy" @click="doSave(true)">
                        {{ t('SaveLoad.Overwrite') }}
                    </button>
                    <button @click="confirmOverwrite = false">
                        {{ t('SaveLoad.Cancel') }}
                    </button>
                </div>
            </section>

            <p v-if="error" class="status error">{{ error }}</p>
            <p v-if="info" class="status">{{ info }}</p>

            <section class="saves-list">
                <p v-if="saves.length === 0" class="empty">
                    {{ t('SaveLoad.NoSaves') }}
                </p>
                <div
                    v-for="save in saves"
                    :key="save.fileName"
                    class="save-row"
                >
                    <div class="save-row-main">
                        <span class="name">{{ save.fileName }}</span>
                        <span class="date">{{
                            new Date(save.lastWriteTime).toLocaleString()
                        }}</span>
                        <button
                            :disabled="busy"
                            @click="doLoad(save.fileName, 'merge')"
                        >
                            {{ t('SaveLoad.Merge') }}
                        </button>
                        <button
                            :disabled="busy"
                            @click="pendingReplace = save.fileName"
                        >
                            {{ t('SaveLoad.Replace') }}
                        </button>
                        <button
                            :disabled="busy"
                            :title="t('SaveLoad.DeleteTooltip')"
                            @click="pendingDelete = save.fileName"
                        >
                            🗑
                        </button>
                    </div>
                    <div
                        v-if="pendingReplace === save.fileName"
                        class="confirm"
                    >
                        {{ t('SaveLoad.ReplaceConfirm') }}
                        <button
                            :disabled="busy"
                            @click="doLoad(save.fileName, 'replace')"
                        >
                            {{ t('SaveLoad.Confirm') }}
                        </button>
                        <button @click="pendingReplace = null">
                            {{ t('SaveLoad.Cancel') }}
                        </button>
                    </div>
                    <div v-if="pendingDelete === save.fileName" class="confirm">
                        {{ t('SaveLoad.DeleteConfirm', save.fileName) }}
                        <button
                            :disabled="busy"
                            @click="doDelete(save.fileName)"
                        >
                            {{ t('SaveLoad.Confirm') }}
                        </button>
                        <button @click="pendingDelete = null">
                            {{ t('SaveLoad.Cancel') }}
                        </button>
                    </div>
                </div>
            </section>
        </div>
    </div>
</template>

<style scoped>
.dialog {
    width: min(560px, 90vw);
    max-height: 80vh;
}

.save-section {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-end;
    gap: 8px;
    margin: 14px 0;
}

.save-label {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 4px;
    min-width: 200px;
}

.save-label input {
    width: 100%;
}

.confirm {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
    color: var(--text-muted);
    font-size: 13px;
    margin-top: 6px;
    width: 100%;
}

.status {
    margin: 6px 0;
}

.empty {
    color: var(--text-muted);
}

.save-row {
    padding: 8px 0;
    border-top: 1px solid var(--border);
}

.save-row-main {
    display: flex;
    align-items: center;
    gap: 8px;
}

.name {
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.date {
    color: var(--text-muted);
    font-size: 12px;
}

.save-row button {
    font-size: 12px;
    padding: 4px 8px;
}
</style>
