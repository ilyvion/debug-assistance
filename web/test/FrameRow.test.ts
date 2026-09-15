import { mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';

import {
    addProbe,
    decompileCauseFrame,
    decompileCausePatch,
    decompileFrame,
    decompilePatch,
    decompileProbeFrame,
    decompileProbePatch,
} from '../src/api';
import CodePanel from '../src/components/CodePanel.vue';
import FrameRow from '../src/components/FrameRow.vue';
import type { BrowsedMethod, FrameInfo } from '../src/types';

vi.mock('../src/api', () => ({
    decompileFrame: vi.fn(),
    decompilePatch: vi.fn(),
    decompileCauseFrame: vi.fn(),
    decompileCausePatch: vi.fn(),
    decompileProbeFrame: vi.fn(),
    decompileProbePatch: vi.fn(),
    addProbe: vi.fn(),
}));

function frameWithPatches(): FrameInfo {
    return {
        index: 0,
        rawText: 'Some.Type.Method',
        declaringTypeName: 'Some.Type',
        methodName: 'Method',
        displayName: null,
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
                displayName: null,
            },
            {
                index: 1,
                ownerModId: 'PatchModB',
                patchKind: 'postfix',
                declaringTypeName: 'Patch.B',
                methodName: 'Postfix',
                displayName: null,
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

    it('disables "Probe this method" when the frame has no resolvable patch target', () => {
        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        const button = wrapper.find('.probe-this-method');
        expect(button.attributes('disabled')).toBeDefined();
    });

    it("adds a probe for the frame's resolved target when clicked, and shows the result", async () => {
        const target = patchTargetFixture();
        vi.mocked(addProbe).mockResolvedValue({
            success: true,
            probe: {
                id: 'probe-1',
                targetDeclaringTypeName: target.declaringTypeName,
                targetMethodName: target.methodName,
                targetDisplayName: target.signature,
                appliedAt: '2026-01-01T00:00:00Z',
                totalInvocationCount: 0,
                uniqueHitCount: 0,
                isActive: true,
                capReason: 'None',
            },
        });
        const wrapper = mount(FrameRow, {
            props: {
                dedupeKey: 'key',
                frame: { ...frameWithPatches(), patchTarget: target },
            },
        });

        const button = wrapper.find('.probe-this-method');
        expect(button.attributes('disabled')).toBeUndefined();
        await button.trigger('click');
        await flushMicrotasks();

        expect(addProbe).toHaveBeenCalledWith({
            assemblyFullName: target.assemblyFullName,
            metadataToken: target.metadataToken,
        });
        expect(wrapper.find('.probe-status').text()).toContain(
            'FrameRow.ProbeAdded',
        );
    });

    it('shows an error when adding a probe for the frame fails', async () => {
        const target = patchTargetFixture();
        vi.mocked(addProbe).mockResolvedValue({
            success: false,
            error: 'boom',
        });
        const wrapper = mount(FrameRow, {
            props: {
                dedupeKey: 'key',
                frame: { ...frameWithPatches(), patchTarget: target },
            },
        });

        await wrapper.find('.probe-this-method').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.probe-status.error').text()).toContain(
            'FrameRow.ProbeFailed',
        );
    });

    it('shows the fully qualified type and method name in the row label', () => {
        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        const label = wrapper.find('.row-label');
        expect(label.text()).toContain('Some.Type.Method');
        expect(label.attributes('title')).not.toContain('Some.Type.Method');
    });

    it('shows the fully qualified type and method name in each patch row label', () => {
        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame: frameWithPatches() },
        });

        const patchLabels = wrapper.findAll('.patch .row-label');
        expect(patchLabels[0].text()).toContain('Patch.A.Prefix');
        expect(patchLabels[0].attributes('title')).not.toContain(
            'Patch.A.Prefix',
        );
        expect(patchLabels[1].text()).toContain('Patch.B.Postfix');
    });

    // The backend only sets displayName when it resolved a live MethodBase for the frame, and
    // reformats it as valid C# (e.g. a generic declaring type like
    // "System.Collections.Generic.Dictionary<Thing, Blueprint_Install>" rather than
    // declaringTypeName's own raw CLR-reflection text) -- prefer it over the raw join whenever
    // it's present.
    it('prefers the resolved display name over the raw declaring type and method name', () => {
        const frame = {
            ...frameWithPatches(),
            declaringTypeName:
                'System.Collections.Generic.Dictionary`2[[Verse.Thing]]',
            methodName: 'TryInsert',
            displayName:
                'System.Collections.Generic.Dictionary<Thing, Blueprint_Install>.TryInsert',
        };
        const wrapper = mount(FrameRow, {
            props: { dedupeKey: 'key', frame },
        });

        const label = wrapper.find('.row-label');
        expect(label.text()).toContain(
            'System.Collections.Generic.Dictionary<Thing, Blueprint_Install>.TryInsert',
        );
        expect(label.text()).not.toContain('Dictionary`2');
    });

    it('routes decompile requests to the cause-scoped endpoints when causeIndex is set', async () => {
        vi.mocked(decompileCauseFrame).mockResolvedValue({
            code: 'original',
            highlightLine: null,
        });
        vi.mocked(decompileCausePatch).mockResolvedValue({
            code: 'patch',
            highlightLine: null,
        });

        const wrapper = mount(FrameRow, {
            props: {
                dedupeKey: 'key',
                frame: frameWithPatches(),
                causeIndex: 2,
            },
        });

        await wrapper.vm.decompileAllForFrame();

        expect(decompileCauseFrame).toHaveBeenCalledWith('key', 2, 0, false);
        expect(decompileCauseFrame).toHaveBeenCalledWith('key', 2, 0, true);
        expect(decompileCausePatch).toHaveBeenCalledWith('key', 2, 0, 0);
        expect(decompileCausePatch).toHaveBeenCalledWith('key', 2, 0, 1);
    });

    it('routes decompile requests to the probe endpoints when kind is "probe"', async () => {
        vi.mocked(decompileProbeFrame).mockResolvedValue({
            code: 'original',
            highlightLine: null,
        });
        vi.mocked(decompileProbePatch).mockResolvedValue({
            code: 'patch',
            highlightLine: null,
        });

        const wrapper = mount(FrameRow, {
            props: {
                dedupeKey: 'key',
                frame: frameWithPatches(),
                kind: 'probe',
            },
        });

        await wrapper.vm.decompileAllForFrame();

        expect(decompileProbeFrame).toHaveBeenCalledWith('key', 0, false);
        expect(decompileProbeFrame).toHaveBeenCalledWith('key', 0, true);
        expect(decompileProbePatch).toHaveBeenCalledWith('key', 0, 0);
        expect(decompileProbePatch).toHaveBeenCalledWith('key', 0, 1);
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
