import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
    ApiError,
    deleteSave,
    fetchSaves,
    loadCapture,
    saveCapture,
} from '../src/api';
import SaveLoadDialog from '../src/components/SaveLoadDialog.vue';
import type { SaveFileEntry } from '../src/types';

vi.mock('../src/api', () => ({
    ApiError: class ApiError extends Error {
        constructor(
            message: string,
            public readonly status: number,
        ) {
            super(message);
        }
    },
    deleteSave: vi.fn(),
    fetchSaves: vi.fn(),
    loadCapture: vi.fn(),
    saveCapture: vi.fn(),
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

function save(fileName = 'Capture_1'): SaveFileEntry {
    return { fileName, lastWriteTime: '2026-01-02T03:04:05Z' };
}

async function mountDialog(saves: SaveFileEntry[] = []) {
    vi.mocked(fetchSaves).mockResolvedValueOnce(saves);
    const wrapper = mount(SaveLoadDialog);
    await flushMicrotasks();
    return wrapper;
}

describe('SaveLoadDialog', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('fetches and lists saves on mount', async () => {
        const wrapper = await mountDialog([save('First'), save('Second')]);

        expect(fetchSaves).toHaveBeenCalledOnce();
        const rows = wrapper.findAll('.save-row');
        expect(rows).toHaveLength(2);
        expect(rows[0].find('.name').text()).toBe('First');
        expect(rows[1].find('.name').text()).toBe('Second');
    });

    it('shows an empty state when there are no saves', async () => {
        const wrapper = await mountDialog([]);

        expect(wrapper.find('.saves-list .empty').exists()).toBe(true);
    });

    it('shows an error when the initial fetch fails', async () => {
        vi.mocked(fetchSaves).mockRejectedValueOnce(new Error('network down'));
        const wrapper = mount(SaveLoadDialog);
        await flushMicrotasks();

        expect(wrapper.find('.status.error').text()).toBe('network down');
    });

    it('disables the save button while the save name is blank', async () => {
        const wrapper = await mountDialog();

        await wrapper.find('.save-label input').setValue('   ');

        expect(
            wrapper.find('.save-section button.primary').attributes('disabled'),
        ).toBeDefined();
    });

    it('saves without overwrite and shows the result', async () => {
        const wrapper = await mountDialog();
        await wrapper.find('.save-label input').setValue('MyCapture');

        vi.mocked(saveCapture).mockResolvedValueOnce({ occurrenceCount: 7 });
        vi.mocked(fetchSaves).mockResolvedValueOnce([save('MyCapture')]);

        await wrapper.find('.save-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(saveCapture).toHaveBeenCalledWith('MyCapture', false);
        expect(wrapper.find('.status').text()).toBe('SaveLoad.Saved(7)');
        expect(wrapper.find('.confirm').exists()).toBe(false);
    });

    it('offers an overwrite confirmation on a 409 and saves with overwrite once confirmed', async () => {
        const wrapper = await mountDialog();
        await wrapper.find('.save-label input').setValue('MyCapture');

        vi.mocked(saveCapture).mockRejectedValueOnce(
            new ApiError('A save with that name already exists', 409),
        );

        await wrapper.find('.save-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.save-section .confirm').text()).toContain(
            'SaveLoad.AlreadyExists(MyCapture)',
        );
        expect(wrapper.find('.status.error').exists()).toBe(false);

        vi.mocked(saveCapture).mockResolvedValueOnce({ occurrenceCount: 3 });
        vi.mocked(fetchSaves).mockResolvedValueOnce([save('MyCapture')]);

        await wrapper.find('.save-section .confirm button').trigger('click');
        await flushMicrotasks();

        expect(saveCapture).toHaveBeenLastCalledWith('MyCapture', true);
        expect(wrapper.find('.save-section .confirm').exists()).toBe(false);
        expect(wrapper.find('.status').text()).toBe('SaveLoad.Saved(3)');
    });

    it('cancels the overwrite confirmation without saving again', async () => {
        const wrapper = await mountDialog();
        await wrapper.find('.save-label input').setValue('MyCapture');

        vi.mocked(saveCapture).mockRejectedValueOnce(
            new ApiError('A save with that name already exists', 409),
        );
        await wrapper.find('.save-section button.primary').trigger('click');
        await flushMicrotasks();

        const confirmButtons = wrapper.findAll('.save-section .confirm button');
        await confirmButtons[1].trigger('click');

        expect(wrapper.find('.save-section .confirm').exists()).toBe(false);
        expect(saveCapture).toHaveBeenCalledOnce();
    });

    it('shows a plain error message for a non-409 save failure', async () => {
        const wrapper = await mountDialog();
        await wrapper.find('.save-label input').setValue('MyCapture');

        vi.mocked(saveCapture).mockRejectedValueOnce(new Error('disk full'));

        await wrapper.find('.save-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.status.error').text()).toBe('disk full');
        expect(wrapper.find('.save-section .confirm').exists()).toBe(false);
    });

    it('merges a save directly without a confirmation step', async () => {
        const wrapper = await mountDialog([save()]);

        vi.mocked(loadCapture).mockResolvedValueOnce({ loadedCount: 5 });

        await wrapper.find('.save-row button').trigger('click');
        await flushMicrotasks();

        expect(loadCapture).toHaveBeenCalledWith('Capture_1', 'merge');
        expect(wrapper.find('.status').text()).toBe('SaveLoad.Loaded(5)');
        expect(wrapper.emitted('loaded')).toHaveLength(1);
    });

    it('requires confirmation before replacing, then loads with replace once confirmed', async () => {
        const wrapper = await mountDialog([save()]);

        const rowButtons = wrapper.findAll('.save-row-main button');
        await rowButtons[1].trigger('click'); // Replace
        await flushMicrotasks();

        expect(loadCapture).not.toHaveBeenCalled();
        expect(wrapper.find('.save-row .confirm').text()).toContain(
            'SaveLoad.ReplaceConfirm',
        );

        vi.mocked(loadCapture).mockResolvedValueOnce({ loadedCount: 9 });
        await wrapper.find('.save-row .confirm button').trigger('click');
        await flushMicrotasks();

        expect(loadCapture).toHaveBeenCalledWith('Capture_1', 'replace');
        expect(wrapper.find('.save-row .confirm').exists()).toBe(false);
        expect(wrapper.emitted('loaded')).toHaveLength(1);
    });

    it('cancels a pending replace without calling loadCapture', async () => {
        const wrapper = await mountDialog([save()]);

        const rowButtons = wrapper.findAll('.save-row-main button');
        await rowButtons[1].trigger('click'); // Replace
        await flushMicrotasks();

        const confirmButtons = wrapper.findAll('.save-row .confirm button');
        await confirmButtons[1].trigger('click');

        expect(loadCapture).not.toHaveBeenCalled();
        expect(wrapper.find('.save-row .confirm').exists()).toBe(false);
    });

    it('requires confirmation before deleting, then deletes and refreshes once confirmed', async () => {
        const wrapper = await mountDialog([save()]);

        const rowButtons = wrapper.findAll('.save-row-main button');
        await rowButtons[2].trigger('click'); // Delete
        await flushMicrotasks();

        expect(deleteSave).not.toHaveBeenCalled();
        expect(wrapper.find('.save-row .confirm').text()).toContain(
            'SaveLoad.DeleteConfirm(Capture_1)',
        );

        vi.mocked(deleteSave).mockResolvedValueOnce(undefined);
        vi.mocked(fetchSaves).mockResolvedValueOnce([]);

        await wrapper.find('.save-row .confirm button').trigger('click');
        await flushMicrotasks();

        expect(deleteSave).toHaveBeenCalledWith('Capture_1');
        expect(wrapper.findAll('.save-row')).toHaveLength(0);
    });

    it('cancels a pending delete without calling deleteSave', async () => {
        const wrapper = await mountDialog([save()]);

        const rowButtons = wrapper.findAll('.save-row-main button');
        await rowButtons[2].trigger('click'); // Delete
        await flushMicrotasks();

        const confirmButtons = wrapper.findAll('.save-row .confirm button');
        await confirmButtons[1].trigger('click');

        expect(deleteSave).not.toHaveBeenCalled();
        expect(wrapper.find('.save-row .confirm').exists()).toBe(false);
        expect(wrapper.findAll('.save-row')).toHaveLength(1);
    });

    it('closes on Escape, on the close button, and on an overlay click', async () => {
        const wrapper = await mountDialog();

        await wrapper.find('.close').trigger('click');
        expect(wrapper.emitted('close')).toHaveLength(1);

        await wrapper.find('.overlay').trigger('click');
        expect(wrapper.emitted('close')).toHaveLength(2);

        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
        expect(wrapper.emitted('close')).toHaveLength(3);
    });

    it('stops listening for Escape once unmounted', async () => {
        const wrapper = await mountDialog();
        wrapper.unmount();

        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));

        expect(wrapper.emitted('close')).toBeUndefined();
    });
});
