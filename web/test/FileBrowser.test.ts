import { mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';

import { ApiError, fetchFiles } from '../src/api';
import FileBrowser from '../src/components/FileBrowser.vue';
import type { FileBrowserList } from '../src/types';

vi.mock('../src/api', async () => {
    const actual =
        await vi.importActual<typeof import('../src/api')>('../src/api');
    return {
        ...actual,
        fetchFiles: vi.fn(),
    };
});

function rootListing(): FileBrowserList {
    return {
        currentPath: null,
        parentPath: null,
        entries: [
            {
                name: '/',
                path: '/',
                isDirectory: true,
                size: null,
                lastWriteTime: null,
            },
        ],
        shortcuts: [{ kind: 'Mods', path: '/game/Mods' }],
    };
}

function dirListing(path: string, parentPath: string | null): FileBrowserList {
    return {
        currentPath: path,
        parentPath,
        entries: [
            {
                name: 'subdir',
                path: `${path}/subdir`,
                isDirectory: true,
                size: null,
                lastWriteTime: null,
            },
            {
                name: 'patch.dll',
                path: `${path}/patch.dll`,
                isDirectory: false,
                size: 2048,
                lastWriteTime: '2026-01-01T00:00:00Z',
            },
        ],
        shortcuts: [{ kind: 'Mods', path: '/game/Mods' }],
    };
}

async function openBrowser(mode: 'file' | 'dir' = 'file', modelValue = '') {
    const wrapper = mount(FileBrowser, {
        props: { modelValue, mode },
    });
    await wrapper.find('.path-row button:last-child').trigger('click');
    await flushMicrotasks();
    return wrapper;
}

describe('FileBrowser', () => {
    it('clicking a directory entry navigates into it and updates the listing', async () => {
        vi.mocked(fetchFiles).mockResolvedValueOnce(rootListing());
        const wrapper = await openBrowser();

        vi.mocked(fetchFiles).mockResolvedValueOnce(dirListing('/', null));
        await wrapper.find('.entry').trigger('click');
        await flushMicrotasks();

        expect(fetchFiles).toHaveBeenLastCalledWith('/', 'file', undefined);
        expect(wrapper.findAll('.entry')).toHaveLength(2);
    });

    it('clicking a file entry commits it immediately and closes the browser', async () => {
        vi.mocked(fetchFiles).mockResolvedValueOnce(
            dirListing('/some/dir', '/some'),
        );
        const wrapper = await openBrowser('file');

        const entries = wrapper.findAll('.entry');
        await entries[1].trigger('click'); // patch.dll

        expect(wrapper.emitted('update:modelValue')).toEqual([
            ['/some/dir/patch.dll'],
        ]);
        expect(wrapper.find('.panel').exists()).toBe(false);
    });

    it('directory mode commits the current directory as the player navigates, with no separate select step', async () => {
        vi.mocked(fetchFiles).mockResolvedValueOnce(rootListing());
        const wrapper = await openBrowser('dir');

        // The true root listing (currentPath === null) has no directory to commit yet.
        expect(wrapper.emitted('update:modelValue')).toBeUndefined();

        vi.mocked(fetchFiles).mockResolvedValueOnce(dirListing('/', null));
        await wrapper.find('.entry').trigger('click');
        await flushMicrotasks();

        expect(wrapper.emitted('update:modelValue')).toEqual([['/']]);
    });

    it('keeps the manual text field and a browser selection in sync via modelValue', async () => {
        vi.mocked(fetchFiles).mockResolvedValueOnce(
            dirListing('/some/dir', '/some'),
        );
        const wrapper = await openBrowser('file');

        const entries = wrapper.findAll('.entry');
        await entries[1].trigger('click'); // patch.dll

        expect(wrapper.emitted('update:modelValue')?.[0]).toEqual([
            '/some/dir/patch.dll',
        ]);

        await wrapper.setProps({ modelValue: '/some/dir/patch.dll' });
        expect(
            wrapper.find<HTMLInputElement>('.path-input').element.value,
        ).toBe('/some/dir/patch.dll');

        const input = wrapper.find('.path-input');
        await input.setValue('/typed/by/hand.dll');
        expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual([
            '/typed/by/hand.dll',
        ]);
    });

    it('clicking Browse with a path already in the field browses straight there', async () => {
        vi.mocked(fetchFiles).mockResolvedValueOnce(
            dirListing('/some/dir', '/some'),
        );
        const wrapper = await openBrowser('file', '/some/dir');

        expect(fetchFiles).toHaveBeenCalledWith('/some/dir', 'file', undefined);
        expect(wrapper.findAll('.entry')).toHaveLength(2);
    });

    it('clicking a shortcut navigates straight to it', async () => {
        vi.mocked(fetchFiles).mockResolvedValueOnce(rootListing());
        const wrapper = await openBrowser();

        vi.mocked(fetchFiles).mockResolvedValueOnce(
            dirListing('/game/Mods', '/game'),
        );
        await wrapper.find('.shortcut').trigger('click');
        await flushMicrotasks();

        expect(fetchFiles).toHaveBeenLastCalledWith(
            '/game/Mods',
            'file',
            undefined,
        );
    });

    it('typing a path and pressing Go jumps straight to it', async () => {
        const wrapper = mount(FileBrowser, {
            props: { modelValue: '/some/dir', mode: 'file' },
        });

        vi.mocked(fetchFiles).mockResolvedValueOnce(
            dirListing('/some/dir', '/some'),
        );
        await wrapper.find('.path-row button:first-of-type').trigger('click');
        await flushMicrotasks();

        expect(fetchFiles).toHaveBeenLastCalledWith(
            '/some/dir',
            'file',
            undefined,
        );
        expect(wrapper.find('.panel').exists()).toBe(true);
    });

    it('Go refuses to navigate to a path that does not exist and shows an error', async () => {
        const wrapper = mount(FileBrowser, {
            props: { modelValue: '/does/not/exist', mode: 'file' },
        });

        vi.mocked(fetchFiles).mockRejectedValueOnce(
            new ApiError('Path not found: /does/not/exist', 400),
        );
        await wrapper.find('.path-row button:first-of-type').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.panel').exists()).toBe(false);
        expect(wrapper.find('.path-error').text()).toBe(
            'Path not found: /does/not/exist',
        );
        expect(wrapper.find('.path-input').classes()).toContain('invalid');
    });

    it('shows a breadcrumb for the root itself, not just its descendants', async () => {
        vi.mocked(fetchFiles).mockResolvedValueOnce(rootListing());
        const wrapper = await openBrowser();

        vi.mocked(fetchFiles).mockResolvedValueOnce(dirListing('/some', null));
        await wrapper.find('.entry').trigger('click');
        await flushMicrotasks();

        const crumbs = wrapper.findAll('.crumb');
        expect(crumbs[0].text()).toBe('/');
        expect(crumbs[1].text()).toBe('some');
        expect(wrapper.findAll('.crumb-sep')).toHaveLength(1);
    });
});

function flushMicrotasks(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}
