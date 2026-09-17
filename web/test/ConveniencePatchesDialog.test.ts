import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
    applyHotPatch,
    fetchConveniencePatches,
    rescanConveniencePatches,
} from '../src/api';
import ConveniencePatchesDialog from '../src/components/ConveniencePatchesDialog.vue';
import type { ConveniencePatch } from '../src/types';

vi.mock('../src/api', () => ({
    applyHotPatch: vi.fn(),
    fetchConveniencePatches: vi.fn(),
    rescanConveniencePatches: vi.fn(),
}));

// The real t() falls back to the raw, argument-free key when no translation was fetched (not
// mocked here) -- this stand-in substitutes args the same way the real translated strings would,
// so the message assertions below can check for the actual interpolated text.
vi.mock('../src/translations', () => ({
    t: (key: string, ...args: (string | number)[]) =>
        args.length > 0 ? `${key}(${args.join(', ')})` : key,
}));

function flushMicrotasks(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}

const target = { assemblyFullName: 'Assembly-CSharp', metadataToken: 1 };

function patch(
    patchType: ConveniencePatch['patchType'],
    name = 'Skip method entirely',
): ConveniencePatch {
    return {
        name,
        description: 'Some description',
        patchType,
        patchMethod: { assemblyFullName: 'DebugAssistance', metadataToken: 2 },
        patchMethodDescription:
            'DebugAssistance.HotPatch.ConveniencePatches.SkipMethod',
        sourceAssemblyName: 'DebugAssistance',
    };
}

async function mountDialog(patches: ConveniencePatch[]) {
    vi.mocked(fetchConveniencePatches).mockResolvedValue(patches);
    const wrapper = mount(ConveniencePatchesDialog, { props: { target } });
    await flushMicrotasks();
    await wrapper.vm.$nextTick();
    return wrapper;
}

describe('ConveniencePatchesDialog', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('fetches and lists every convenience patch against the given target', async () => {
        const wrapper = await mountDialog([
            patch('Prefix', 'Skip method entirely'),
            patch('Postfix', 'Print return value'),
        ]);

        expect(fetchConveniencePatches).toHaveBeenCalledWith(target);
        const rows = wrapper.findAll('.patch-row');
        expect(rows).toHaveLength(2);
        expect(rows[0].text()).toContain('Skip method entirely');
        expect(rows[1].text()).toContain('Print return value');
    });

    it('starts with nothing selected, unlike DiscoveredPatchesDialog', async () => {
        const wrapper = await mountDialog([patch('Prefix'), patch('Postfix')]);

        expect(wrapper.find('button.primary').text()).toContain(
            'HotPatch.ApplySelected(0)',
        );
        expect(
            wrapper.find('button.primary').attributes('disabled'),
        ).toBeDefined();
    });

    it('toggles a patch into the selection', async () => {
        const wrapper = await mountDialog([patch('Prefix'), patch('Postfix')]);

        await wrapper.findAll('.patch-row input')[0].setValue(true);

        expect(wrapper.find('button.primary').text()).toContain(
            'HotPatch.ApplySelected(1)',
        );
    });

    it('applies every selected patch against the fixed target, unchecking on success', async () => {
        const wrapper = await mountDialog([patch('Prefix'), patch('Postfix')]);
        vi.mocked(applyHotPatch).mockResolvedValue({ success: true });

        await wrapper.find('.select-all-label input').setValue(true);
        await wrapper.find('button.primary').trigger('click');
        await flushMicrotasks();

        expect(applyHotPatch).toHaveBeenCalledTimes(2);
        expect(applyHotPatch).toHaveBeenCalledWith(
            target,
            { assemblyFullName: 'DebugAssistance', metadataToken: 2 },
            'Prefix',
        );
        expect(wrapper.findAll('.patch-row')).toHaveLength(2);
        expect(wrapper.find('button.primary').text()).toContain(
            'HotPatch.ApplySelected(0)',
        );
        expect(wrapper.emitted('applied')).toHaveLength(1);
    });

    it('keeps a failed patch selected with its error shown', async () => {
        const wrapper = await mountDialog([patch('Prefix'), patch('Postfix')]);
        vi.mocked(applyHotPatch)
            .mockResolvedValueOnce({ success: true })
            .mockResolvedValueOnce({
                success: false,
                error: 'already patched',
            });

        await wrapper.find('.select-all-label input').setValue(true);
        await wrapper.find('button.primary').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.status.error').text()).toBe('already patched');
        expect(wrapper.find('button.primary').text()).toContain(
            'HotPatch.ApplySelected(1)',
        );
    });

    it('rescans and replaces the list', async () => {
        const wrapper = await mountDialog([
            patch('Prefix', 'Skip method entirely'),
        ]);
        vi.mocked(rescanConveniencePatches).mockResolvedValue([
            patch('Postfix', 'Print return value'),
        ]);

        await wrapper.find('button:not(.primary):not(.close)').trigger('click');
        await flushMicrotasks();
        await wrapper.vm.$nextTick();

        expect(rescanConveniencePatches).toHaveBeenCalledWith(target);
        const rows = wrapper.findAll('.patch-row');
        expect(rows).toHaveLength(1);
        expect(rows[0].text()).toContain('Print return value');
    });

    it('shows an empty state when nothing is found', async () => {
        const wrapper = await mountDialog([]);

        expect(wrapper.find('.status.empty').text()).toBe(
            'HotPatch.NoConveniencePatches',
        );
    });

    it('closes on Escape, the close button, and an overlay click', async () => {
        const wrapper = await mountDialog([patch('Prefix')]);

        await wrapper.find('.close').trigger('click');
        expect(wrapper.emitted('close')).toHaveLength(1);

        await wrapper.find('.overlay').trigger('click');
        expect(wrapper.emitted('close')).toHaveLength(2);

        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
        expect(wrapper.emitted('close')).toHaveLength(3);
    });
});
