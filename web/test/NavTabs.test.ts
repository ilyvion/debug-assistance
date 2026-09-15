import { mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';

import NavTabs from '../src/components/NavTabs.vue';
import { consumePendingHotPatchTarget } from '../src/hotPatchNav';

async function mountWithRouter(current: 'errors' | 'hotpatch' | 'probes') {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', component: { template: '<div />' } },
            { path: '/hotpatch', component: { template: '<div />' } },
            { path: '/probes', component: { template: '<div />' } },
        ],
    });
    await router.push('/');
    const pushSpy = vi.spyOn(router, 'push');
    const wrapper = mount(NavTabs, {
        props: { current },
        global: { plugins: [router] },
    });
    return { wrapper, pushSpy };
}

describe('NavTabs', () => {
    it('marks the tab matching the current page as active', async () => {
        const { wrapper } = await mountWithRouter('hotpatch');

        const buttons = wrapper.findAll('button');
        expect(buttons.map((b) => b.classes().includes('active'))).toEqual([
            false,
            true,
            false,
        ]);
    });

    it('navigates to / when the Errors tab is clicked', async () => {
        const { wrapper, pushSpy } = await mountWithRouter('hotpatch');

        await wrapper.findAll('button')[0].trigger('click');

        expect(pushSpy).toHaveBeenCalledWith('/');
    });

    it('navigates to /probes when the Probes tab is clicked', async () => {
        const { wrapper, pushSpy } = await mountWithRouter('errors');

        await wrapper.findAll('button')[2].trigger('click');

        expect(pushSpy).toHaveBeenCalledWith('/probes');
    });

    it('clears any pending hot-patch target before navigating to Hot Patch', async () => {
        const { wrapper, pushSpy } = await mountWithRouter('errors');

        await wrapper.findAll('button')[1].trigger('click');

        expect(pushSpy).toHaveBeenCalledWith('/hotpatch');
        expect(consumePendingHotPatchTarget()).toEqual({
            target: null,
            dedupeKey: null,
        });
    });
});
