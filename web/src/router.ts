import { createRouter, createWebHistory } from 'vue-router';

import { consumePendingHotPatchTarget } from './hotPatchNav';
import HotPatchView from './views/HotPatchView.vue';
import MainView from './views/MainView.vue';
import ProbesView from './views/ProbesView.vue';

// Browser history: DebugAssistanceServer.ServeFile falls back to index.html for any extensionless
// path that isn't a real file on disk, so a direct navigation or refresh at e.g. /hotpatch is
// served the SPA, which then resolves the route client-side.
export const router = createRouter({
    history: createWebHistory(),
    routes: [
        { path: '/', component: MainView },
        {
            path: '/hotpatch',
            component: HotPatchView,
            // Evaluated once per navigation to this route -- consumes the target set by
            // hotPatchNav.setPendingHotPatchTarget just before the push, so HotPatchView keeps
            // taking initialTarget/dedupeKey as plain props. A direct or refreshed navigation
            // (nothing pending) falls back to the from-scratch entry point.
            props: () => {
                const { target, dedupeKey } = consumePendingHotPatchTarget();
                return { initialTarget: target, dedupeKey };
            },
        },
        { path: '/probes', component: ProbesView },
    ],
});
