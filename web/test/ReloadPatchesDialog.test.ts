import { mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';

import ReloadPatchesDialog from '../src/components/ReloadPatchesDialog.vue';
import type { ActivePatch } from '../src/types';

// The real t() falls back to the raw, argument-free key when no translation was fetched (not
// mocked here) -- this stand-in substitutes args the same way the real translated strings would,
// so the message assertions below can check for the actual interpolated text.
vi.mock('../src/translations', () => ({
    t: (key: string, ...args: (string | number)[]) =>
        args.length > 0 ? `${key}(${args.join(', ')})` : key,
}));

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
    return mount(ReloadPatchesDialog, {
        props: { patches },
    });
}

describe('ReloadPatchesDialog', () => {
    it('lists every patch from the previous generation, all selected by default', () => {
        const wrapper = mountDialog([patch('patch-1'), patch('patch-2')]);

        const rows = wrapper.findAll('.patch-row');
        expect(rows).toHaveLength(2);
        for (const row of rows) {
            expect(
                (row.find('input[type=checkbox]').element as HTMLInputElement)
                    .checked,
            ).toBe(true);
        }
        expect(wrapper.find('.remove-confirm').text()).toContain(
            'HotPatch.RemoveSelectedAndReload(2)',
        );
    });

    it('toggles one patch out of the selection', async () => {
        const wrapper = mountDialog([patch('patch-1'), patch('patch-2')]);

        await wrapper.findAll('.patch-row input')[0].setValue(false);

        expect(wrapper.find('.remove-confirm').text()).toContain(
            'HotPatch.RemoveSelectedAndReload(1)',
        );
    });

    it('shows the keep-old-patches label once nothing is selected', async () => {
        const wrapper = mountDialog([patch('patch-1')]);

        await wrapper.find('.select-all-label input').setValue(false);

        expect(wrapper.find('.remove-confirm').text()).toBe(
            'HotPatch.KeepOldPatches',
        );
    });

    it('emits confirm with only the selected ids, without ever calling an API itself', async () => {
        const wrapper = mountDialog([patch('patch-1'), patch('patch-2')]);

        await wrapper.findAll('.patch-row input')[0].setValue(false);
        await wrapper.find('.remove-confirm').trigger('click');

        expect(wrapper.emitted('confirm')).toEqual([[['patch-2']]]);
    });

    it('emits confirm with an empty array when the player keeps every old patch', async () => {
        const wrapper = mountDialog([patch('patch-1')]);

        await wrapper.find('.select-all-label input').setValue(false);
        await wrapper.find('.remove-confirm').trigger('click');

        expect(wrapper.emitted('confirm')).toEqual([[[]]]);
    });

    it('emits cancel on Escape, the close button, and an overlay click', async () => {
        const wrapper = mountDialog([patch('patch-1')]);

        await wrapper.find('.close').trigger('click');
        expect(wrapper.emitted('cancel')).toHaveLength(1);

        await wrapper.find('.overlay').trigger('click');
        expect(wrapper.emitted('cancel')).toHaveLength(2);

        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
        expect(wrapper.emitted('cancel')).toHaveLength(3);

        expect(wrapper.emitted('confirm')).toBeUndefined();
    });
});
