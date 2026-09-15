import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';

import ProbeList from '../src/components/ProbeList.vue';
import type { ProbeListEntry } from '../src/types';

function entry(dedupeKey: string): ProbeListEntry {
    return {
        dedupeKey,
        targetDeclaringTypeName: 'Some.Type',
        targetMethodName: 'Method',
        targetDisplayName: `Some.Type.Method${dedupeKey}`,
        occurrenceCount: 1,
        firstSeen: '2026-01-01T00:00:00Z',
        lastSeen: '2026-01-01T00:00:00Z',
        topFrameModName: null,
    };
}

describe('ProbeList', () => {
    it('shows the empty message when there are no entries', () => {
        const wrapper = mount(ProbeList, {
            props: { entries: [], filterText: '', selectedKey: null },
        });

        expect(wrapper.find('.empty').exists()).toBe(true);
    });

    it('emits select when an entry is clicked', async () => {
        const wrapper = mount(ProbeList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
            },
        });

        await wrapper.find('.entry-main').trigger('click');

        expect(wrapper.emitted('select')).toEqual([['a']]);
    });

    it('emits dismiss when the dismiss button is clicked', async () => {
        const wrapper = mount(ProbeList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
            },
        });

        await wrapper.find('.dismiss').trigger('click');

        expect(wrapper.emitted('dismiss')).toEqual([['a']]);
    });

    it('filters entries by target display name', () => {
        const wrapper = mount(ProbeList, {
            props: {
                entries: [entry('a'), entry('b')],
                filterText: 'MethodA',
                selectedKey: null,
            },
        });

        const entries = wrapper.findAll('.entry');
        expect(entries).toHaveLength(1);
        expect(entries[0].text()).toContain('Some.Type.Methoda');
    });

    it('requires confirmation before emitting clear-all', async () => {
        const wrapper = mount(ProbeList, {
            props: {
                entries: [entry('a')],
                filterText: '',
                selectedKey: null,
            },
        });

        await wrapper.find('.clear-all').trigger('click');
        expect(wrapper.emitted('clear-all')).toBeUndefined();

        await wrapper.find('.confirm button').trigger('click');
        expect(wrapper.emitted('clear-all')).toEqual([[]]);
    });
});
