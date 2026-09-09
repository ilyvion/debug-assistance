<script setup lang="ts">
import Prism from 'prismjs';
import { nextTick, onMounted, ref, watch } from 'vue';

import { dataLineAttr, scrollToHighlightedLine } from '../highlight';

const props = withDefaults(
    defineProps<{
        code: string;
        highlightLine?: number | null;
        autoScroll?: boolean;
    }>(),
    { highlightLine: null, autoScroll: true },
);

const preEl = ref<HTMLPreElement | null>(null);
const codeEl = ref<HTMLElement | null>(null);

async function highlight() {
    await nextTick();
    const code = codeEl.value;
    const pre = preEl.value;
    if (!code || !pre) {
        return;
    }

    code.textContent = props.code;
    Prism.highlightElement(code);

    if (!props.autoScroll) {
        return;
    }
    await nextTick();
    scrollToHighlightedLine(pre);
}

onMounted(() => void highlight());
watch(
    () => [props.code, props.highlightLine],
    () => void highlight(),
);
</script>

<template>
    <pre
        ref="preEl"
        class="line-numbers"
        :data-line="dataLineAttr(highlightLine)"
    ><code
        ref="codeEl"
        class="language-csharp"
    ></code></pre>
</template>
