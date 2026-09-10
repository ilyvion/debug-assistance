import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { fetchAiPrompt, fetchErrorDetail } from '../src/api';
import ErrorDetail from '../src/components/ErrorDetail.vue';
import { settings } from '../src/settings';
import type {
    ErrorCause,
    ErrorDetail as ErrorDetailModel,
    FrameInfo,
} from '../src/types';

vi.mock('../src/api', () => ({
    fetchErrorDetail: vi.fn(),
    fetchAiPrompt: vi.fn(),
    decompileFrame: vi.fn(),
    decompilePatch: vi.fn(),
    decompileCauseFrame: vi.fn(),
    decompileCausePatch: vi.fn(),
}));

// The real t() falls back to the raw, argument-free key when no translation was fetched (not
// mocked here) — this stand-in substitutes args the same way the real translated strings would,
// so the assertions below can check for the actual interpolated error text.
vi.mock('../src/translations', () => ({
    t: (key: string, ...args: (string | number)[]) =>
        args.length > 0 ? `${key}(${args.join(', ')})` : key,
}));

// Defaults to enabled so the existing AI-prompt tests below don't each have to opt in; the
// dedicated test for the setting itself flips this back off.
vi.mock('../src/settings', () => ({
    settings: { aiPromptGeneratorEnabled: true },
}));

function flushMicrotasks(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}

function detail(overrides: Partial<ErrorDetailModel> = {}): ErrorDetailModel {
    return {
        dedupeKey: 'key',
        errorTypeName: 'System.Exception',
        message: 'boom',
        rawStackTrace: 'at Foo.Bar()',
        occurrenceCount: 1,
        firstSeen: '2026-01-01T00:00:00.000Z',
        lastSeen: '2026-01-01T00:00:00.000Z',
        harmonyRefHash: null,
        frames: [],
        innerCauses: [],
        ...overrides,
    };
}

function causeFrame(overrides: Partial<FrameInfo> = {}): FrameInfo {
    return {
        index: 0,
        rawText: 'at Cause.Frame()',
        declaringTypeName: null,
        methodName: null,
        displayName: null,
        fileName: null,
        lineNumber: null,
        columnNumber: null,
        ilOffset: null,
        resolvedModName: null,
        resolvedAssemblyShortName: null,
        patches: [],
        patchTarget: null,
        ...overrides,
    };
}

function cause(overrides: Partial<ErrorCause> = {}): ErrorCause {
    return {
        errorTypeName: 'System.InvalidOperationException',
        message: 'root cause',
        rawStackTrace: 'at Cause.Frame()',
        frames: [causeFrame()],
        ...overrides,
    };
}

afterEach(() => {
    vi.restoreAllMocks();
    settings.aiPromptGeneratorEnabled = true;
});

describe('ErrorDetail AI prompt button', () => {
    it('is hidden when the AI prompt generator setting is disabled', async () => {
        settings.aiPromptGeneratorEnabled = false;
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(detail());

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        expect(wrapper.find('button.ai-prompt').exists()).toBe(false);
    });

    it('copies the fetched prompt to the clipboard and shows a confirmation', async () => {
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(detail());
        vi.mocked(fetchAiPrompt).mockResolvedValueOnce('# the prompt');
        const writeText = vi.fn().mockResolvedValueOnce(undefined);
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText },
            configurable: true,
        });

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        await wrapper.find('button.ai-prompt').trigger('click');
        await flushMicrotasks();

        expect(fetchAiPrompt).toHaveBeenCalledWith('key');
        expect(writeText).toHaveBeenCalledWith('# the prompt');
        expect(wrapper.text()).toContain('ErrorDetail.CopyAiPromptCopied');
    });

    it('shows an error message when copying fails', async () => {
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(detail());
        vi.mocked(fetchAiPrompt).mockRejectedValueOnce(
            new Error('server unreachable'),
        );

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        await wrapper.find('button.ai-prompt').trigger('click');
        await flushMicrotasks();

        expect(wrapper.text()).toContain('server unreachable');
    });
});

describe('ErrorDetail raw stack trace', () => {
    it('is hidden until the toggle button is clicked', async () => {
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(detail());

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        expect(wrapper.find('.raw-trace').exists()).toBe(false);

        await wrapper.find('button.raw-trace-toggle').trigger('click');

        expect(wrapper.find('.raw-trace').exists()).toBe(true);
        expect(wrapper.text()).toContain('at Foo.Bar()');
    });

    it('copies the raw stack trace to the clipboard without needing it shown first', async () => {
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(detail());
        const writeText = vi.fn().mockResolvedValueOnce(undefined);
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText },
            configurable: true,
        });

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        expect(wrapper.find('.raw-trace').exists()).toBe(false);
        await wrapper.find('button.copy-raw-trace').trigger('click');
        await flushMicrotasks();

        expect(writeText).toHaveBeenCalledWith('at Foo.Bar()');
        expect(wrapper.text()).toContain('ErrorDetail.CopyStackTraceCopied');
        expect(wrapper.find('.raw-trace').exists()).toBe(false);
    });

    it('shows an error message when copying fails', async () => {
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(detail());
        const writeText = vi
            .fn()
            .mockRejectedValueOnce(new Error('clipboard denied'));
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText },
            configurable: true,
        });

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        await wrapper.find('button.copy-raw-trace').trigger('click');
        await flushMicrotasks();

        expect(wrapper.text()).toContain('clipboard denied');
    });
});

describe('ErrorDetail inner-exception causes', () => {
    it('renders decompilable frame rows for each cause, root cause first', async () => {
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(
            detail({
                innerCauses: [
                    cause({ message: 'wrapper cause' }),
                    cause({ message: 'root cause' }),
                ],
            }),
        );

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        const titles = wrapper.findAll('.cause-title');
        expect(titles).toHaveLength(2);
        expect(titles[0].text()).toContain('ErrorDetail.RootCause(');
        expect(titles[0].text()).toContain('root cause');
        expect(titles[1].text()).toContain('ErrorDetail.CausedBy(');
        expect(titles[1].text()).toContain('wrapper cause');
        expect(wrapper.findAll('.cause .frame')).toHaveLength(2);
    });

    it('keeps every cause stack trace collapsed by default, including the root cause', async () => {
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(
            detail({ innerCauses: [cause()] }),
        );

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        expect(wrapper.find('.cause-trace-text').exists()).toBe(false);

        await wrapper.find('button.cause-trace-toggle').trigger('click');

        expect(wrapper.find('.cause-trace-text').exists()).toBe(true);
    });

    it('copies a cause stack trace to the clipboard without needing it shown first', async () => {
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(
            detail({
                innerCauses: [cause({ rawStackTrace: 'at Cause.Foo()' })],
            }),
        );
        const writeText = vi.fn().mockResolvedValueOnce(undefined);
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText },
            configurable: true,
        });

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        expect(wrapper.find('.cause-trace-text').exists()).toBe(false);
        await wrapper.find('button.copy-cause-trace').trigger('click');
        await flushMicrotasks();

        expect(writeText).toHaveBeenCalledWith('at Cause.Foo()');
        expect(wrapper.text()).toContain('ErrorDetail.CopyStackTraceCopied');
        expect(wrapper.find('.cause-trace-text').exists()).toBe(false);
    });

    it('shows an error message when copying a cause stack trace fails', async () => {
        vi.mocked(fetchErrorDetail).mockResolvedValueOnce(
            detail({ innerCauses: [cause()] }),
        );
        const writeText = vi
            .fn()
            .mockRejectedValueOnce(new Error('clipboard denied'));
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText },
            configurable: true,
        });

        const wrapper = mount(ErrorDetail, {
            props: { dedupeKey: 'key' },
        });
        await flushMicrotasks();

        await wrapper.find('button.copy-cause-trace').trigger('click');
        await flushMicrotasks();

        expect(wrapper.text()).toContain('clipboard denied');
    });
});
