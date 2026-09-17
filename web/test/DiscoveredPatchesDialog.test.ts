import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { applyHotPatch } from '../src/api';
import DiscoveredPatchesDialog from '../src/components/DiscoveredPatchesDialog.vue';
import type { DiscoveredPatch } from '../src/types';

vi.mock('../src/api', () => ({
    applyHotPatch: vi.fn(),
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

function patch(
    patchType: DiscoveredPatch['patchType'],
    targetDescription = 'Some.Type.Method',
): DiscoveredPatch {
    return {
        target: { assemblyFullName: 'Assembly-CSharp', metadataToken: 1 },
        targetDescription,
        patchMethod: { assemblyFullName: 'MyPatch', metadataToken: 2 },
        patchMethodDescription: `GeneratedPatch.Patches.${patchType}`,
        patchType,
    };
}

function mountDialog(patches: DiscoveredPatch[]) {
    return mount(DiscoveredPatchesDialog, {
        props: { patches, sourceAssemblyPath: '/some/path/MyPatch.dll' },
    });
}

describe('DiscoveredPatchesDialog', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('lists every discovered patch, all selected by default', () => {
        const wrapper = mountDialog([patch('Prefix'), patch('Postfix')]);

        const rows = wrapper.findAll('.patch-row');
        expect(rows).toHaveLength(2);
        for (const row of rows) {
            expect(
                (row.find('input[type=checkbox]').element as HTMLInputElement)
                    .checked,
            ).toBe(true);
        }
    });

    it('toggles one patch out of the selection', async () => {
        const wrapper = mountDialog([patch('Prefix'), patch('Postfix')]);

        await wrapper.findAll('.patch-row input')[0].setValue(false);

        expect(wrapper.find('button.primary').text()).toContain(
            'HotPatch.ApplySelected(1)',
        );
    });

    it('toggles every patch via select all', async () => {
        const wrapper = mountDialog([patch('Prefix'), patch('Postfix')]);

        await wrapper.find('.select-all-label input').setValue(false);
        expect(wrapper.find('button.primary').text()).toContain(
            'HotPatch.ApplySelected(0)',
        );

        await wrapper.find('.select-all-label input').setValue(true);
        expect(wrapper.find('button.primary').text()).toContain(
            'HotPatch.ApplySelected(2)',
        );
    });

    it('applies every selected patch and removes them from the list on success', async () => {
        const wrapper = mountDialog([patch('Prefix'), patch('Postfix')]);
        vi.mocked(applyHotPatch).mockResolvedValue({ success: true });

        await wrapper.find('button.primary').trigger('click');
        await flushMicrotasks();

        expect(applyHotPatch).toHaveBeenCalledTimes(2);
        expect(applyHotPatch).toHaveBeenCalledWith(
            { assemblyFullName: 'Assembly-CSharp', metadataToken: 1 },
            { assemblyFullName: 'MyPatch', metadataToken: 2 },
            'Prefix',
            '/some/path/MyPatch.dll',
        );
        expect(wrapper.findAll('.patch-row')).toHaveLength(0);
        expect(wrapper.emitted('applied')).toHaveLength(1);
        expect(wrapper.emitted('close')).toHaveLength(1);
    });

    it('keeps a failed patch in the list with its error, and only emits applied for the successful one', async () => {
        const wrapper = mountDialog([patch('Prefix'), patch('Postfix')]);
        vi.mocked(applyHotPatch)
            .mockResolvedValueOnce({ success: true })
            .mockResolvedValueOnce({
                success: false,
                error: 'already patched',
            });

        await wrapper.find('button.primary').trigger('click');
        await flushMicrotasks();

        const rows = wrapper.findAll('.patch-row');
        expect(rows).toHaveLength(1);
        expect(rows[0].text()).toContain('Postfix');
        expect(wrapper.find('.status.error').text()).toBe('already patched');
        expect(wrapper.emitted('applied')).toHaveLength(1);
        expect(wrapper.emitted('close')).toBeUndefined();
    });

    it('shows the thrown error message when applyHotPatch rejects', async () => {
        const wrapper = mountDialog([patch('Prefix')]);
        vi.mocked(applyHotPatch).mockRejectedValueOnce(
            new Error('network down'),
        );

        await wrapper.find('button.primary').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.status.error').text()).toBe('network down');
        expect(wrapper.emitted('close')).toBeUndefined();
    });

    it('disables the apply button once nothing is selected', async () => {
        const wrapper = mountDialog([patch('Prefix')]);

        await wrapper.findAll('.patch-row input')[0].setValue(false);

        expect(
            wrapper.find('button.primary').attributes('disabled'),
        ).toBeDefined();
    });

    it('closes on Escape, the close button, and an overlay click', async () => {
        const wrapper = mountDialog([patch('Prefix')]);

        await wrapper.find('.close').trigger('click');
        expect(wrapper.emitted('close')).toHaveLength(1);

        await wrapper.find('.overlay').trigger('click');
        expect(wrapper.emitted('close')).toHaveLength(2);

        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
        expect(wrapper.emitted('close')).toHaveLength(3);
    });
});
