import { mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';

import { decompileFrame, decompilePatch } from '../src/api';
import CodePanel from '../src/components/CodePanel.vue';
import FrameRow from '../src/components/FrameRow.vue';
import type { BrowsedMethod, FrameInfo } from '../src/types';

vi.mock('../src/api', () => ({
    decompileFrame: vi.fn(),
    decompilePatch: vi.fn(),
}));

function frameWithPatches(): FrameInfo {
    return {
        index: 0,
        rawText: 'Some.Type.Method',
        declaringTypeName: 'Some.Type',
        methodName: 'Method',
        fileName: null,
        lineNumber: null,
        columnNumber: null,
        ilOffset: null,
        resolvedModName: 'SomeMod',
        resolvedAssemblyShortName: 'SomeMod.dll',
        patchTarget: null,
        patches: [
            {
                index: 0,
                ownerModId: 'PatchModA',
                patchKind: 'prefix',
                declaringTypeName: 'Patch.A',
                methodName: 'Prefix',
            },
            {
                index: 1,
                ownerModId: 'PatchModB',
                patchKind: 'postfix',
                declaringTypeName: 'Patch.B',
                methodName: 'Postfix',
            },
        ],
    };
}

describe('FrameRow', () => {
    it('keeps two patch panels open at the same time', async () => {
        vi.mocked(decompilePatch).mockImplementation(
            (_key, _frame, patchIndex) =>
                Promise.resolve({
                    code: `patch ${String(patchIndex)}`,
                    highlightLine: null,
                }),
        );

        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        const patchButtons = wrapper.findAll('.patch .row-actions button');
        await patchButtons[0].trigger('click');
        await patchButtons[1].trigger('click');
        await flushMicrotasks();

        const panels = wrapper.findAll('.patch .panel');
        expect(panels).toHaveLength(2);
        expect(panels[0].text()).toContain('patch 0');
        expect(panels[1].text()).toContain('patch 1');
    });

    it('closing one open panel leaves the other one open', async () => {
        vi.mocked(decompilePatch).mockResolvedValue({
            code: 'code',
            highlightLine: null,
        });

        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        const patchButtons = wrapper.findAll('.patch .row-actions button');
        await patchButtons[0].trigger('click');
        await patchButtons[1].trigger('click');
        await flushMicrotasks();
        await patchButtons[0].trigger('click');

        expect(wrapper.findAll('.patch .panel')).toHaveLength(1);
    });

    it('decompileAllForFrame opens and fetches every panel, reporting progress per item', async () => {
        vi.mocked(decompileFrame).mockResolvedValue({
            code: 'original',
            highlightLine: null,
        });
        vi.mocked(decompilePatch).mockResolvedValue({
            code: 'patch',
            highlightLine: null,
        });

        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        const onItemDone = vi.fn();
        // frame.patches.length > 0, so items are: original, patched, patch-0, patch-1.
        expect(wrapper.vm.itemCount()).toBe(4);
        await wrapper.vm.decompileAllForFrame(onItemDone);

        expect(decompileFrame).toHaveBeenCalledWith('key', 0, false);
        expect(decompileFrame).toHaveBeenCalledWith('key', 0, true);
        expect(decompilePatch).toHaveBeenCalledWith('key', 0, 0);
        expect(decompilePatch).toHaveBeenCalledWith('key', 0, 1);
        expect(onItemDone).toHaveBeenCalledTimes(4);
        expect(wrapper.findAll('.panel')).toHaveLength(4);
    });

    it('does not auto-scroll panels opened by decompileAllForFrame', async () => {
        vi.mocked(decompileFrame).mockResolvedValue({
            code: 'original',
            highlightLine: 3,
        });
        vi.mocked(decompilePatch).mockResolvedValue({
            code: 'patch',
            highlightLine: 3,
        });

        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        await wrapper.vm.decompileAllForFrame();

        const panels = wrapper.findAllComponents(CodePanel);
        expect(panels).toHaveLength(4);
        for (const panel of panels) {
            expect(panel.props('autoScroll')).toBe(false);
        }
    });

    it('auto-scrolls a panel opened by an individual Decompile click, including after a prior decompile all', async () => {
        vi.mocked(decompilePatch).mockResolvedValue({
            code: 'patch',
            highlightLine: 3,
        });

        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        await wrapper.vm.decompileAllForFrame();

        const patchButtons = wrapper.findAll('.patch .row-actions button');
        // Closes the panel decompileAllForFrame just opened.
        await patchButtons[0].trigger('click');
        // Reopens it via an explicit, individual click.
        await patchButtons[0].trigger('click');
        await flushMicrotasks();

        // patchButtons[0] is patch-0's own button; its panel is the first `.patch .panel`.
        const panel = wrapper
            .findAll('.patch .panel')[0]
            .findComponent(CodePanel);
        expect(panel.props('autoScroll')).toBe(true);
    });

    function patchTargetFixture(): BrowsedMethod {
        return {
            assemblyName: 'SomeMod',
            assemblyFullName: 'SomeMod, Version=0.0.0.0',
            metadataToken: 42,
            declaringTypeName: 'Some.Type',
            namespace: 'Some',
            methodName: 'Method',
            signature: 'void Method()',
            isStatic: false,
        };
    }

    it('disables "Patch this method" when the frame has no resolvable patch target', () => {
        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        const button = wrapper.find('.patch-this-method');
        expect(button.attributes('disabled')).toBeDefined();
    });

    it('emits patch-this-method with the resolved patch target when clicked', async () => {
        const target = patchTargetFixture();
        const wrapper = mount(FrameRow, {
            props: {
                dedupeKey: 'key',
                frame: { ...frameWithPatches(), patchTarget: target },
            },
        });

        const button = wrapper.find('.patch-this-method');
        expect(button.attributes('disabled')).toBeUndefined();
        await button.trigger('click');

        expect(wrapper.emitted('patch-this-method')).toEqual([[target]]);
    });

    it('shows the fully qualified type and method name in the row label', () => {
        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        const label = wrapper.find('.row-label');
        expect(label.text()).toContain('Some.Type.Method');
        expect(label.attributes('title')).not.toContain('Some.Type.Method');
    });

    it('falls back to the raw stack trace text when the frame has no resolved name', () => {
        const frame = {
            ...frameWithPatches(),
            declaringTypeName: null,
            methodName: null,
            rawText: '  at Unresolved.Frame(...)',
        };
        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame },
        });

        expect(wrapper.find('.row-label').text()).toContain(
            'at Unresolved.Frame(...)',
        );
    });
});

function flushMicrotasks(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}
