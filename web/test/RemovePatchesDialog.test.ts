import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { removeManyHotPatches } from '../src/api';
import RemovePatchesDialog from '../src/components/RemovePatchesDialog.vue';
import type { ActivePatch } from '../src/types';

vi.mock('../src/api', () => ({
    removeManyHotPatches: vi.fn(),
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
    id: string,
    targetDescription = 'Some.Type.Method',
): ActivePatch {
    return {
        id,
        targetDescription,
        patchMethodDescription: `GeneratedPatch.Patches.Prefix`,
        patchType: 'Prefix',
        sourceAssemblyPath: '/some/path/MyPatch.dll',
        sourceAssemblyName: 'MyPatch',
        sourceAssemblyGeneration: 1,
    };
}

function mountDialog(patches: ActivePatch[]) {
    return mount(RemovePatchesDialog, {
        props: { patches },
    });
}

describe('RemovePatchesDialog', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('lists every offered patch, all selected by default', () => {
        const wrapper = mountDialog([patch('patch-1'), patch('patch-2')]);

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
        const wrapper = mountDialog([patch('patch-1'), patch('patch-2')]);

        await wrapper.findAll('.patch-row input')[0].setValue(false);

        expect(wrapper.find('.remove-confirm').text()).toContain(
            'HotPatch.RemoveSelected(1)',
        );
    });

    it('toggles every patch via select all', async () => {
        const wrapper = mountDialog([patch('patch-1'), patch('patch-2')]);

        await wrapper.find('.select-all-label input').setValue(false);
        expect(wrapper.find('.remove-confirm').text()).toContain(
            'HotPatch.RemoveSelected(0)',
        );

        await wrapper.find('.select-all-label input').setValue(true);
        expect(wrapper.find('.remove-confirm').text()).toContain(
            'HotPatch.RemoveSelected(2)',
        );
    });

    it('removes every selected patch and emits removed/close on success', async () => {
        const wrapper = mountDialog([patch('patch-1'), patch('patch-2')]);
        vi.mocked(removeManyHotPatches).mockResolvedValueOnce([
            'patch-1',
            'patch-2',
        ]);

        await wrapper.find('.remove-confirm').trigger('click');
        await flushMicrotasks();

        expect(removeManyHotPatches).toHaveBeenCalledWith([
            'patch-1',
            'patch-2',
        ]);
        expect(wrapper.findAll('.patch-row')).toHaveLength(0);
        expect(wrapper.emitted('removed')).toHaveLength(1);
        expect(wrapper.emitted('close')).toHaveLength(1);
    });

    it('keeps a patch the server did not report as removed, with a dialog-wide error', async () => {
        const wrapper = mountDialog([patch('patch-1'), patch('patch-2')]);
        vi.mocked(removeManyHotPatches).mockResolvedValueOnce(['patch-1']);

        await wrapper.find('.remove-confirm').trigger('click');
        await flushMicrotasks();

        const rows = wrapper.findAll('.patch-row');
        expect(rows).toHaveLength(1);
        expect(wrapper.find('.status.error').text()).toBe(
            'HotPatch.RemoveSelectedFailed',
        );
        expect(wrapper.emitted('removed')).toHaveLength(1);
        expect(wrapper.emitted('close')).toBeUndefined();
    });

    it('shows the thrown error message when removeManyHotPatches rejects', async () => {
        const wrapper = mountDialog([patch('patch-1')]);
        vi.mocked(removeManyHotPatches).mockRejectedValueOnce(
            new Error('network down'),
        );

        await wrapper.find('.remove-confirm').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.status.error').text()).toBe('network down');
        expect(wrapper.emitted('removed')).toBeUndefined();
        expect(wrapper.emitted('close')).toBeUndefined();
    });

    it('disables the remove button once nothing is selected', async () => {
        const wrapper = mountDialog([patch('patch-1')]);

        await wrapper.findAll('.patch-row input')[0].setValue(false);

        expect(
            wrapper.find('.remove-confirm').attributes('disabled'),
        ).toBeDefined();
    });

    it('closes on Escape, the close button, and an overlay click', async () => {
        const wrapper = mountDialog([patch('patch-1')]);

        await wrapper.find('.close').trigger('click');
        expect(wrapper.emitted('close')).toHaveLength(1);

        await wrapper.find('.overlay').trigger('click');
        expect(wrapper.emitted('close')).toHaveLength(2);

        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
        expect(wrapper.emitted('close')).toHaveLength(3);
    });
});
