<script setup lang="ts">
import NavTabs from './NavTabs.vue';
import ThemeToggle from './ThemeToggle.vue';

// Common chrome for all three sibling pages (Errors/Hot Patch/Probes): the mod name, the shared
// NavTabs, this page's own subheader, and the theme picker, all styled once instead of being
// re-declared per view. The default slot holds whatever page-specific controls (filter box,
// warning text, buttons, ...) sit between the subheader and the theme picker.
defineProps<{
    current: 'errors' | 'hotpatch' | 'probes';
    title: string;
}>();
</script>

<template>
    <header class="page-header">
        <h1>DebugAssistance</h1>
        <NavTabs :current="current" />
        <h2>{{ title }}</h2>
        <slot />
        <ThemeToggle class="theme-toggle-anchor" />
    </header>
</template>

<style scoped>
.page-header {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 10px 16px;
    border-bottom: 1px solid var(--border);
    background: var(--bg-alt);
}

.page-header h1 {
    font-size: 15px;
    margin: 0;
    white-space: nowrap;
}

.page-header h2 {
    margin: 0;
    font-size: 15px;
    white-space: nowrap;
}

.page-header :deep(.theme-toggle-anchor) {
    margin-left: auto;
}
</style>
