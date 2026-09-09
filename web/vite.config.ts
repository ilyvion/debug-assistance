import vue from '@vitejs/plugin-vue';
import { defineConfig } from 'vite';

// Builds straight into ../Site, which is where the mod's in-process HTTP
// server (Source/DebugAssistance/Web/DebugAssistanceServer.cs) serves static
// files from at runtime.
export default defineConfig({
    plugins: [vue()],
    build: {
        outDir: '../Site',
        emptyOutDir: true,
    },
});
