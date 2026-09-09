<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';

import {
    type ThemePreference,
    applyThemePreference,
    loadThemePreference,
    saveThemePreference,
} from '../theme';
import { t } from '../translations';

const preference = ref<ThemePreference>(loadThemePreference());

watch(
    preference,
    (value) => {
        applyThemePreference(value);
        saveThemePreference(value);
    },
    { immediate: true },
);

onMounted(() => {
    applyThemePreference(preference.value);
});

const options = computed<
    { value: ThemePreference; label: string; title: string }[]
>(() => [
    { value: 'light', label: '☀', title: t('Theme.Light') },
    { value: 'system', label: '◐', title: t('Theme.System') },
    { value: 'dark', label: '☾', title: t('Theme.Dark') },
]);
</script>

<template>
    <div class="theme-toggle" role="radiogroup" :aria-label="t('Theme.Label')">
        <button
            v-for="option in options"
            :key="option.value"
            type="button"
            class="theme-option"
            :class="{ active: preference === option.value }"
            :title="option.title"
            :aria-pressed="preference === option.value"
            @click="preference = option.value"
        >
            {{ option.label }}
        </button>
    </div>
</template>

<style scoped>
.theme-toggle {
    display: flex;
    gap: 2px;
    padding: 2px;
    background-color: var(--ctp-crust);
    border: 1px solid var(--ctp-surface0);
    border-radius: 8px;
}

.theme-option {
    min-width: 2rem;
    padding: 4px 8px;
    font-size: 0.95rem;
    line-height: 1;
    color: var(--ctp-subtext0);
    background: transparent;
    border: none;
    border-radius: 6px;
    cursor: pointer;
}

.theme-option:hover {
    color: var(--ctp-text);
    background-color: var(--ctp-surface0);
}

.theme-option.active {
    color: var(--ctp-base);
    background-color: var(--ctp-blue);
}
</style>
