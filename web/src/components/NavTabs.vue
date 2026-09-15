<script setup lang="ts">
import { useRouter } from 'vue-router';

import { setPendingHotPatchTarget } from '../hotPatchNav';
import { t } from '../translations';

// Errors, Hot Patch and Probes are three sibling top-level pages, not a "main" page with two
// sub-pages you navigate back out of -- every page renders this same set of tabs so moving
// between any two of them looks and behaves identically regardless of which one you're on.
defineProps<{ current: 'errors' | 'hotpatch' | 'probes' }>();

const router = useRouter();

function goToErrors() {
    void router.push('/');
}

function goToHotPatch() {
    // Matches the from-scratch entry point: a tab click, unlike a frame's own "Patch this
    // method" action, has no specific target method to pre-fill.
    setPendingHotPatchTarget(null, null);
    void router.push('/hotpatch');
}

function goToProbes() {
    void router.push('/probes');
}
</script>

<template>
    <nav class="nav-tabs">
        <button
            type="button"
            :class="{ active: current === 'errors' }"
            @click="goToErrors"
        >
            {{ t('App.ErrorsTitle') }}
        </button>
        <button
            type="button"
            :class="{ active: current === 'hotpatch' }"
            @click="goToHotPatch"
        >
            {{ t('HotPatch.Title') }}
        </button>
        <button
            type="button"
            :class="{ active: current === 'probes' }"
            @click="goToProbes"
        >
            {{ t('Probes.Title') }}
        </button>
    </nav>
</template>

<style scoped>
.nav-tabs {
    display: flex;
    gap: 4px;
}

.nav-tabs button {
    border: none;
    background: transparent;
    padding: 6px 10px;
    border-radius: 4px;
    color: var(--text-muted);
    white-space: nowrap;
}

.nav-tabs button:hover {
    background: var(--bg-hover);
}

.nav-tabs button.active {
    background: var(--bg-hover);
    color: var(--text);
    font-weight: 600;
}
</style>
