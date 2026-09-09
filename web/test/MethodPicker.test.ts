import { mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';

import {
    fetchHotPatchAssemblies,
    fetchHotPatchMethods,
    fetchHotPatchMethodsOfType,
    fetchHotPatchNamespaces,
    fetchHotPatchTypes,
} from '../src/api';
import MethodPicker from '../src/components/MethodPicker.vue';
import type {
    AssemblyEntry,
    BrowsedMethod,
    NamespaceEntry,
    TypeEntry,
} from '../src/types';

vi.mock('../src/api', () => ({
    fetchHotPatchAssemblies: vi.fn(),
    fetchHotPatchMethods: vi.fn(),
    fetchHotPatchMethodsOfType: vi.fn(),
    fetchHotPatchNamespaces: vi.fn(),
    fetchHotPatchTypes: vi.fn(),
}));

function flushMicrotasks(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}

// MethodPicker debounces its search by 250ms before calling fetchHotPatchMethods.
function flushDebounce(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 300));
}

function assemblyEntry(overrides: Partial<AssemblyEntry> = {}): AssemblyEntry {
    return {
        name: 'MyPatch',
        fullName: 'MyPatch, Version=0.0.0.0',
        typeCount: 1,
        ...overrides,
    };
}

function namespaceEntry(
    overrides: Partial<NamespaceEntry> = {},
): NamespaceEntry {
    return { name: 'MyPatch.Fixes', typeCount: 1, ...overrides };
}

function typeEntry(overrides: Partial<TypeEntry> = {}): TypeEntry {
    return {
        name: 'SomeFix',
        fullName: 'MyPatch.Fixes.SomeFix',
        methodCount: 1,
        ...overrides,
    };
}

function methodEntry(overrides: Partial<BrowsedMethod> = {}): BrowsedMethod {
    return {
        assemblyName: 'MyPatch',
        assemblyFullName: 'MyPatch, Version=0.0.0.0',
        metadataToken: 111,
        declaringTypeName: 'MyPatch.Fixes.SomeFix',
        namespace: 'MyPatch.Fixes',
        methodName: 'Prefix',
        signature: 'static bool Prefix()',
        isStatic: true,
        ...overrides,
    };
}

describe('MethodPicker', () => {
    it('lists every loaded assembly at the root level when path is null', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([
            assemblyEntry({ name: 'Assembly-CSharp' }),
            assemblyEntry({ name: 'MyPatch' }),
        ]);

        const wrapper = mount(MethodPicker, {
            props: { path: null, modelValue: null },
        });
        await flushMicrotasks();

        expect(fetchHotPatchAssemblies).toHaveBeenCalledWith(null, null);
        const rows = wrapper.findAll('.results li');
        expect(rows).toHaveLength(2);
        expect(rows[0].text()).toContain('Assembly-CSharp');
    });

    it('auto-selects the sole assembly when path is set, skipping straight to its namespaces', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([
            assemblyEntry(),
        ]);
        vi.mocked(fetchHotPatchNamespaces).mockResolvedValueOnce([
            namespaceEntry(),
        ]);

        const wrapper = mount(MethodPicker, {
            props: { path: '/dev/patch.dll', modelValue: null },
        });
        await flushMicrotasks();

        expect(fetchHotPatchNamespaces).toHaveBeenCalledWith(
            'MyPatch, Version=0.0.0.0',
            null,
        );
        const rows = wrapper.findAll('.results li');
        expect(rows).toHaveLength(1);
        expect(rows[0].text()).toContain('MyPatch.Fixes');
    });

    it('drills down from assembly to namespace to type to method and selects it', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([
            assemblyEntry(),
        ]);
        const wrapper = mount(MethodPicker, {
            props: { path: null, modelValue: null },
        });
        await flushMicrotasks();

        vi.mocked(fetchHotPatchNamespaces).mockResolvedValueOnce([
            namespaceEntry(),
        ]);
        await wrapper.find('.results button').trigger('click');
        await flushMicrotasks();

        vi.mocked(fetchHotPatchTypes).mockResolvedValueOnce([typeEntry()]);
        await wrapper.find('.results button').trigger('click');
        await flushMicrotasks();
        expect(fetchHotPatchTypes).toHaveBeenCalledWith(
            'MyPatch, Version=0.0.0.0',
            'MyPatch.Fixes',
            null,
        );

        const method = methodEntry();
        vi.mocked(fetchHotPatchMethodsOfType).mockResolvedValueOnce([method]);
        await wrapper.find('.results button').trigger('click');
        await flushMicrotasks();
        expect(fetchHotPatchMethodsOfType).toHaveBeenCalledWith(
            'MyPatch, Version=0.0.0.0',
            'MyPatch.Fixes.SomeFix',
            null,
        );

        await wrapper.find('.results button').trigger('click');

        expect(wrapper.emitted('update:modelValue')).toEqual([[method]]);
    });

    it('clicking an earlier breadcrumb goes back to that level without refetching it', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([
            assemblyEntry(),
        ]);
        const wrapper = mount(MethodPicker, {
            props: { path: null, modelValue: null },
        });
        await flushMicrotasks();

        vi.mocked(fetchHotPatchNamespaces).mockResolvedValueOnce([
            namespaceEntry(),
        ]);
        await wrapper.find('.results button').trigger('click');
        await flushMicrotasks();

        const callsBefore = vi.mocked(fetchHotPatchAssemblies).mock.calls
            .length;
        await wrapper.find('.crumb').trigger('click');

        expect(vi.mocked(fetchHotPatchAssemblies).mock.calls).toHaveLength(
            callsBefore,
        );
        const rows = wrapper.findAll('.results li');
        expect(rows).toHaveLength(1);
        expect(rows[0].text()).toContain('MyPatch');
    });

    it('disables Up at the top of the tree', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([]);
        const wrapper = mount(MethodPicker, {
            props: { path: null, modelValue: null },
        });
        await flushMicrotasks();

        expect(
            wrapper.find('.toolbar button').attributes('disabled'),
        ).toBeDefined();
    });

    it('enables Up once drilled into an assembly, and going up returns to the assembly list', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([
            assemblyEntry(),
        ]);
        const wrapper = mount(MethodPicker, {
            props: { path: null, modelValue: null },
        });
        await flushMicrotasks();

        vi.mocked(fetchHotPatchNamespaces).mockResolvedValueOnce([
            namespaceEntry(),
        ]);
        await wrapper.find('.results button').trigger('click');
        await flushMicrotasks();

        const upButton = wrapper.find('.toolbar button');
        expect(upButton.attributes('disabled')).toBeUndefined();
        await upButton.trigger('click');

        const rows = wrapper.findAll('.results li');
        expect(rows).toHaveLength(1);
        expect(rows[0].text()).toContain('MyPatch');
    });

    it('typing into the filter box shows flat search results instead of the tree', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([]);
        const wrapper = mount(MethodPicker, {
            props: { path: '/dev/patch.dll', modelValue: null },
        });
        await flushMicrotasks();

        const method = methodEntry();
        vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce([method]);
        await wrapper.find('.filter-input').setValue('Prefix');
        await flushDebounce();

        expect(fetchHotPatchMethods).toHaveBeenCalledWith(
            '/dev/patch.dll',
            'Prefix',
            null,
        );
        expect(wrapper.find('.toolbar').exists()).toBe(false);
        expect(wrapper.find('.results button').text()).toContain(
            method.signature,
        );
    });

    it('ignores a slower, earlier search response that arrives after a newer one', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([]);
        const wrapper = mount(MethodPicker, {
            props: { path: '/dev/patch.dll', modelValue: null },
        });
        await flushMicrotasks();

        let resolveFirst!: (methods: BrowsedMethod[]) => void;
        const firstResponse = new Promise<BrowsedMethod[]>((resolve) => {
            resolveFirst = resolve;
        });
        vi.mocked(fetchHotPatchMethods).mockReturnValueOnce(firstResponse);
        await wrapper.find('.filter-input').setValue('post');
        await flushDebounce();

        const secondResult = [
            methodEntry({
                methodName: 'PostExposeData',
                signature: 'void PostExposeData()',
            }),
        ];
        vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce(secondResult);
        await wrapper.find('.filter-input').setValue('postexposedata');
        await flushDebounce();

        // The broader, earlier "post" search finally resolves after the narrower one already has.
        resolveFirst([
            methodEntry({
                methodName: 'PostExposeData',
                signature: 'void PostExposeData()',
            }),
            methodEntry({
                methodName: 'PostSpawnSetup',
                signature: 'void PostSpawnSetup()',
            }),
        ]);
        await flushMicrotasks();

        const rows = wrapper.findAll('.results button');
        expect(rows).toHaveLength(1);
        expect(rows[0].text()).toContain('PostExposeData');
    });

    it("shows each search result's declaring type, so identical-looking overloads stay distinguishable", async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([]);
        const wrapper = mount(MethodPicker, {
            props: { path: '/dev/patch.dll', modelValue: null },
        });
        await flushMicrotasks();

        vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce([
            methodEntry({ declaringTypeName: 'MyPatch.Fixes.SomeFix' }),
            methodEntry({ declaringTypeName: 'MyPatch.Fixes.OtherFix' }),
        ]);
        await wrapper.find('.filter-input').setValue('Prefix');
        await flushDebounce();

        const rows = wrapper.findAll('.results button');
        expect(rows).toHaveLength(2);
        expect(rows[0].text()).toContain('MyPatch.Fixes.SomeFix');
        expect(rows[1].text()).toContain('MyPatch.Fixes.OtherFix');
    });

    it('includes the target method and patch type as a compatibility filter when both are set', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([]);
        const targetMethod = methodEntry({
            assemblyFullName: 'Assembly-CSharp',
            metadataToken: 42,
        });
        const wrapper = mount(MethodPicker, {
            props: {
                path: '/dev/patch.dll',
                modelValue: null,
                targetMethod,
                patchType: 'Postfix',
            },
        });
        await flushMicrotasks();

        vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce([]);
        await wrapper.find('.filter-input').setValue('Prefix');
        await flushDebounce();

        expect(fetchHotPatchMethods).toHaveBeenCalledWith(
            '/dev/patch.dll',
            'Prefix',
            {
                target: {
                    assemblyFullName: 'Assembly-CSharp',
                    metadataToken: 42,
                },
                patchType: 'Postfix',
            },
        );
    });

    it('re-runs the current search when the patch type changes while browsing', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([]);
        const wrapper = mount(MethodPicker, {
            props: {
                path: '/dev/patch.dll',
                modelValue: null,
                targetMethod: methodEntry(),
                patchType: 'Prefix' as const,
            },
        });
        await flushMicrotasks();

        vi.mocked(fetchHotPatchMethods).mockResolvedValue([]);
        await wrapper.find('.filter-input').setValue('Fix');
        await flushDebounce();
        const callsBefore = vi.mocked(fetchHotPatchMethods).mock.calls.length;

        await wrapper.setProps({ patchType: 'Postfix' });
        await flushMicrotasks();

        expect(
            vi.mocked(fetchHotPatchMethods).mock.calls.length,
        ).toBeGreaterThan(callsBefore);
        expect(fetchHotPatchMethods).toHaveBeenLastCalledWith(
            '/dev/patch.dll',
            'Fix',
            {
                target: {
                    assemblyFullName: methodEntry().assemblyFullName,
                    metadataToken: methodEntry().metadataToken,
                },
                patchType: 'Postfix',
            },
        );
    });

    it('re-fetches the assembly list when the patch type changes while browsing at the top level', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValue([
            assemblyEntry({ name: 'One' }),
            assemblyEntry({ name: 'Two', fullName: 'Two, Version=0.0.0.0' }),
        ]);
        const targetMethod = methodEntry({
            assemblyFullName: 'Assembly-CSharp',
            metadataToken: 42,
        });
        const wrapper = mount(MethodPicker, {
            props: {
                path: null,
                modelValue: null,
                targetMethod,
                patchType: 'Prefix' as const,
            },
        });
        await flushMicrotasks();
        const callsBefore = vi.mocked(fetchHotPatchAssemblies).mock.calls
            .length;

        await wrapper.setProps({ patchType: 'Postfix' });
        await flushMicrotasks();

        expect(
            vi.mocked(fetchHotPatchAssemblies).mock.calls.length,
        ).toBeGreaterThan(callsBefore);
        expect(fetchHotPatchAssemblies).toHaveBeenLastCalledWith(null, {
            target: {
                assemblyFullName: targetMethod.assemblyFullName,
                metadataToken: targetMethod.metadataToken,
            },
            patchType: 'Postfix',
        });
    });

    it('re-fetches the namespace list when the patch type changes while browsing namespaces', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([
            assemblyEntry({ name: 'One' }),
            assemblyEntry({ name: 'Two', fullName: 'Two, Version=0.0.0.0' }),
        ]);
        const targetMethod = methodEntry();
        const wrapper = mount(MethodPicker, {
            props: {
                path: null,
                modelValue: null,
                targetMethod,
                patchType: 'Prefix' as const,
            },
        });
        await flushMicrotasks();

        vi.mocked(fetchHotPatchNamespaces).mockResolvedValue([
            namespaceEntry(),
        ]);
        await wrapper.find('.results button').trigger('click');
        await flushMicrotasks();
        const callsBefore = vi.mocked(fetchHotPatchNamespaces).mock.calls
            .length;

        await wrapper.setProps({ patchType: 'Postfix' });
        await flushMicrotasks();

        expect(
            vi.mocked(fetchHotPatchNamespaces).mock.calls.length,
        ).toBeGreaterThan(callsBefore);
        expect(fetchHotPatchNamespaces).toHaveBeenLastCalledWith(
            'MyPatch, Version=0.0.0.0',
            {
                target: {
                    assemblyFullName: targetMethod.assemblyFullName,
                    metadataToken: targetMethod.metadataToken,
                },
                patchType: 'Postfix',
            },
        );
    });

    it('re-fetches the type list when the patch type changes while browsing types', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValueOnce([
            assemblyEntry(),
        ]);
        const targetMethod = methodEntry();
        const wrapper = mount(MethodPicker, {
            props: {
                path: null,
                modelValue: null,
                targetMethod,
                patchType: 'Prefix' as const,
            },
        });
        await flushMicrotasks();

        vi.mocked(fetchHotPatchNamespaces).mockResolvedValueOnce([
            namespaceEntry(),
        ]);
        await wrapper.find('.results button').trigger('click');
        await flushMicrotasks();

        vi.mocked(fetchHotPatchTypes).mockResolvedValue([typeEntry()]);
        await wrapper.find('.results button').trigger('click');
        await flushMicrotasks();
        const callsBefore = vi.mocked(fetchHotPatchTypes).mock.calls.length;

        await wrapper.setProps({ patchType: 'Postfix' });
        await flushMicrotasks();

        expect(vi.mocked(fetchHotPatchTypes).mock.calls.length).toBeGreaterThan(
            callsBefore,
        );
        expect(fetchHotPatchTypes).toHaveBeenLastCalledWith(
            'MyPatch, Version=0.0.0.0',
            'MyPatch.Fixes',
            {
                target: {
                    assemblyFullName: targetMethod.assemblyFullName,
                    metadataToken: targetMethod.metadataToken,
                },
                patchType: 'Postfix',
            },
        );
    });

    it('clicking Change after selecting a method drills back into the type it was chosen from', async () => {
        const method = methodEntry();
        vi.mocked(fetchHotPatchNamespaces).mockResolvedValueOnce([
            namespaceEntry(),
        ]);
        vi.mocked(fetchHotPatchTypes).mockResolvedValueOnce([typeEntry()]);
        vi.mocked(fetchHotPatchMethodsOfType).mockResolvedValueOnce([
            methodEntry({
                methodName: 'OtherPrefix',
                signature: 'static bool OtherPrefix()',
            }),
        ]);
        const wrapper = mount(MethodPicker, {
            props: { path: null, modelValue: method },
        });

        const selected = wrapper.find('.selected');
        expect(selected.exists()).toBe(true);
        expect(selected.text()).toContain(method.declaringTypeName);
        await wrapper.find('.selected button').trigger('click');
        await flushMicrotasks();

        expect(wrapper.emitted('update:modelValue')?.[0]).toEqual([null]);
        expect(fetchHotPatchNamespaces).toHaveBeenCalledWith(
            method.assemblyFullName,
            null,
        );
        expect(fetchHotPatchTypes).toHaveBeenCalledWith(
            method.assemblyFullName,
            method.namespace,
            null,
        );
        expect(fetchHotPatchMethodsOfType).toHaveBeenCalledWith(
            method.assemblyFullName,
            method.declaringTypeName,
            null,
        );
        // Landed on the member list of the same type, not the assembly root.
        const rows = wrapper.findAll('.results li');
        expect(rows).toHaveLength(1);
        expect(rows[0].text()).toContain('OtherPrefix()');
    });

    it('clicking Change with nothing selected yet resets to the assembly root', async () => {
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValue([assemblyEntry()]);
        const wrapper = mount(MethodPicker, {
            props: { path: null, modelValue: null },
        });
        await flushMicrotasks();

        const rows = wrapper.findAll('.results li');
        expect(rows).toHaveLength(1);
        expect(rows[0].text()).toContain('MyPatch');
    });
});
