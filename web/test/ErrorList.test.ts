import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { setErrorCaptureEnabled } from '../src/api';
import ErrorList from '../src/components/ErrorList.vue';
import { settings } from '../src/settings';
import type { ErrorListEntry } from '../src/types';

vi.mock('../src/api', () => ({
    setErrorCaptureEnabled: vi.fn(),
}));

function entry(dedupeKey: string): ErrorListEntry {
    return {
        dedupeKey,
        errorTypeName: 'System.Exception',
        message: `message ${dedupeKey}`,
        occurrenceCount: 1,
        firstSeen: '2026-01-01T00:00:00Z',
        lastSeen: '2026-01-01T00:00:00Z',
        harmonyRefHash: null,
        topFrameModName: null,
    };
}

describe('ErrorList', () => {
    beforeEach(() => {
        settings.errorCaptureEnabled = true;
    });

    it('toggles error capture off and back on', async () => {
        vi.mocked(setErrorCaptureEnabled).mockImplementation((enabled) =>
            Promise.resolve({
                aiPromptGeneratorEnabled: false,
                errorCaptureEnabled: enabled,
            }),
        );
        const wrapper = mount(ErrorList, {
            props: { entries: [], filterText: '', selectedKey: null },
        });

        await wrapper.find('.capture-toggle').trigger('click');
        expect(setErrorCaptureEnabled).toHaveBeenCalledWith(false);
        await wrapper.vm.$nextTick();
        expect(wrapper.find('.capture-toggle').classes()).toContain('paused');

        await wrapper.find('.capture-toggle').trigger('click');
        expect(setErrorCaptureEnabled).toHaveBeenCalledWith(true);
    });

    it('emits select when an entry is clicked', async () => {
        const wrapper = mount(ErrorList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
            },
        });

        await wrapper.find('.entry-main').trigger('click');

        expect(wrapper.emitted('select')).toEqual([['a']]);
    });

    it('emits dismiss immediately with no confirmation step', async () => {
        const wrapper = mount(ErrorList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
            },
        });

        await wrapper.find('.dismiss').trigger('click');

        expect(wrapper.emitted('dismiss')).toEqual([['a']]);
        expect(wrapper.emitted('select')).toBeUndefined();
    });

    it('disables clear-all when there are no entries', () => {
        const wrapper = mount(ErrorList, {
            props: { entries: [], filterText: '', selectedKey: null },
        });

        expect(wrapper.find('.clear-all').attributes('disabled')).toBeDefined();
    });

    it('requires confirmation before emitting clear-all', async () => {
        const wrapper = mount(ErrorList, {
            props: {
                entries: [entry('a'), entry('b')],
                filterText: '',
                selectedKey: null,
            },
        });

        await wrapper.find('.clear-all').trigger('click');
        expect(wrapper.emitted('clear-all')).toBeUndefined();

        await wrapper.find('.confirm button').trigger('click');

        expect(wrapper.emitted('clear-all')).toEqual([[]]);
    });

    it('cancelling the clear-all confirmation emits nothing', async () => {
        const wrapper = mount(ErrorList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
            },
        });

        await wrapper.find('.clear-all').trigger('click');
        const confirmButtons = wrapper.findAll('.confirm button');
        await confirmButtons[1].trigger('click');

        expect(wrapper.find('.confirm').exists()).toBe(false);
        expect(wrapper.emitted('clear-all')).toBeUndefined();
    });

    it('highlights entries with no recorded inspection as unread', () => {
        const wrapper = mount(ErrorList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
                inspectedCounts: {},
            },
        });

        expect(wrapper.find('.entry').classes()).toContain('unread');
    });

    it('does not highlight an entry inspected at its current occurrence count', () => {
        const wrapper = mount(ErrorList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
                inspectedCounts: { a: 1 },
            },
        });

        expect(wrapper.find('.entry').classes()).not.toContain('unread');
    });

    it('re-highlights an inspected entry once it gains a new duplicate', () => {
        const withNewDuplicate = { ...entry('a'), occurrenceCount: 2 };
        const wrapper = mount(ErrorList, {
            props: {
                entries: [withNewDuplicate],
                filterText: '',
                selectedKey: null,
                inspectedCounts: { a: 1 },
            },
        });

        expect(wrapper.find('.entry').classes()).toContain('unread');
    });

    it('disables mark-all-seen when nothing is unread', () => {
        const wrapper = mount(ErrorList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
                inspectedCounts: { a: 1 },
            },
        });

        expect(
            wrapper.find('.mark-all-seen').attributes('disabled'),
        ).toBeDefined();
    });

    it('emits mark-all-seen when clicked', async () => {
        const wrapper = mount(ErrorList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
                inspectedCounts: {},
            },
        });

        await wrapper.find('.mark-all-seen').trigger('click');

        expect(wrapper.emitted('mark-all-seen')).toEqual([[]]);
    });
});
