<script setup lang="ts">
import { RouterView, useRouter } from 'vue-router';

const router = useRouter();
</script>

<template>
    <RouterView v-slot="{ Component }">
        <component :is="Component" @close="router.push('/')" />
    </RouterView>
</template>
