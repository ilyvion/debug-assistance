import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { fetchProbeAiPrompt, fetchProbeDetail } from '../src/api';
import ProbeDetail from '../src/components/ProbeDetail.vue';
import { settings } from '../src/settings';
import type { ProbeDetail as ProbeDetailModel } from '../src/types';

vi.mock('../src/api', () => ({
    fetchProbeDetail: vi.fn(),
    fetchProbeAiPrompt: vi.fn(),
    decompileProbeFrame: vi.fn(),
    decompileProbePatch: vi.fn(),
}));

vi.mock('../src/translations', () => ({
    t: (key: string, ...args: (string | number)[]) =>
        args.length > 0 ? `${key}(${args.join(', ')})` : key,
}));

vi.mock('../src/settings', () => ({
    settings: { aiPromptGeneratorEnabled: true },
}));

function flushMicrotasks(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}

function detail(overrides: Partial<ProbeDetailModel> = {}): ProbeDetailModel {
    return {
        dedupeKey: 'key',
        targetDeclaringTypeName: 'Some.Type',
        targetMethodName: 'Method',
        targetDisplayName: 'Some.Type.Method()',
        rawStackTrace: 'at Some.Type.Method()',
        occurrenceCount: 3,
        firstSeen: '2026-01-01T00:00:00.000Z',
        lastSeen: '2026-01-02T00:00:00.000Z',
        frames: [],
        ...overrides,
    };
}

afterEach(() => {
    vi.restoreAllMocks();
    settings.aiPromptGeneratorEnabled = true;
});

describe('ProbeDetail', () => {
    it('shows the target display name and seen summary once loaded', async () => {
        vi.mocked(fetchProbeDetail).mockResolvedValueOnce(detail());

        const wrapper = mount(ProbeDetail, { props: { dedupeKey: 'key' } });
        await flushMicrotasks();

        expect(wrapper.find('h2').text()).toBe('Some.Type.Method()');
        expect(wrapper.text()).toContain('ProbeDetail.SeenSummary(3');
    });

    it('shows the load error message when fetching fails', async () => {
        vi.mocked(fetchProbeDetail).mockRejectedValueOnce(
            new Error('server unreachable'),
        );

        const wrapper = mount(ProbeDetail, { props: { dedupeKey: 'key' } });
        await flushMicrotasks();

        expect(wrapper.text()).toContain('server unreachable');
    });

    it('hides the decompile-all button when there are no frames', async () => {
        vi.mocked(fetchProbeDetail).mockResolvedValueOnce(detail());

        const wrapper = mount(ProbeDetail, { props: { dedupeKey: 'key' } });
        await flushMicrotasks();

        expect(wrapper.find('.decompile-all').exists()).toBe(false);
    });
});

describe('ProbeDetail AI prompt button', () => {
    it('is hidden when the AI prompt generator setting is disabled', async () => {
        settings.aiPromptGeneratorEnabled = false;
        vi.mocked(fetchProbeDetail).mockResolvedValueOnce(detail());

        const wrapper = mount(ProbeDetail, { props: { dedupeKey: 'key' } });
        await flushMicrotasks();

        expect(wrapper.find('button.ai-prompt').exists()).toBe(false);
    });

    it('copies the fetched prompt to the clipboard and shows a confirmation', async () => {
        vi.mocked(fetchProbeDetail).mockResolvedValueOnce(detail());
        vi.mocked(fetchProbeAiPrompt).mockResolvedValueOnce('# the prompt');
        const writeText = vi.fn().mockResolvedValueOnce(undefined);
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText },
            configurable: true,
        });

        const wrapper = mount(ProbeDetail, { props: { dedupeKey: 'key' } });
        await flushMicrotasks();

        await wrapper.find('button.ai-prompt').trigger('click');
        await flushMicrotasks();

        expect(fetchProbeAiPrompt).toHaveBeenCalledWith('key');
        expect(writeText).toHaveBeenCalledWith('# the prompt');
        expect(wrapper.text()).toContain('ProbeDetail.CopyAiPromptCopied');
    });

    it('shows an error message when copying fails', async () => {
        vi.mocked(fetchProbeDetail).mockResolvedValueOnce(detail());
        vi.mocked(fetchProbeAiPrompt).mockRejectedValueOnce(
            new Error('server unreachable'),
        );

        const wrapper = mount(ProbeDetail, { props: { dedupeKey: 'key' } });
        await flushMicrotasks();

        await wrapper.find('button.ai-prompt').trigger('click');
        await flushMicrotasks();

        expect(wrapper.text()).toContain('server unreachable');
    });
});

describe('ProbeDetail raw stack trace', () => {
    it('is hidden until the toggle button is clicked', async () => {
        vi.mocked(fetchProbeDetail).mockResolvedValueOnce(detail());

        const wrapper = mount(ProbeDetail, { props: { dedupeKey: 'key' } });
        await flushMicrotasks();

        expect(wrapper.find('.raw-trace').exists()).toBe(false);

        await wrapper.find('.raw-trace-toggle').trigger('click');

        expect(wrapper.find('.raw-trace').text()).toContain(
            'at Some.Type.Method()',
        );
    });
});
