import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
    applyHotPatch,
    fetchActiveHotPatches,
    fetchHotPatchAssemblies,
    fetchHotPatchDebugPrompt,
    fetchHotPatchMethods,
    fetchHotPatchMethodsOfType,
    fetchHotPatchNamespaces,
    fetchHotPatchTypes,
    fetchLoadedHotPatchAssemblies,
    fetchSuggestedScaffoldProjectName,
    loadHotPatchAssembly,
    removeManyHotPatches,
    scaffoldHotPatchProject,
} from '../src/api';
import { settings } from '../src/settings';
import type {
    ActivePatch,
    BrowsedMethod,
    MethodSearchResult,
} from '../src/types';
import HotPatchView from '../src/views/HotPatchView.vue';

vi.mock('../src/api', () => ({
    applyHotPatch: vi.fn(),
    fetchActiveHotPatches: vi.fn(),
    fetchFiles: vi.fn(),
    fetchHotPatchAssemblies: vi.fn(),
    fetchHotPatchDebugPrompt: vi.fn(),
    fetchHotPatchMethods: vi.fn(),
    fetchHotPatchMethodsOfType: vi.fn(),
    fetchHotPatchNamespaces: vi.fn(),
    fetchHotPatchTypes: vi.fn(),
    fetchLoadedHotPatchAssemblies: vi.fn(),
    fetchSuggestedScaffoldProjectName: vi.fn(),
    loadHotPatchAssembly: vi.fn(),
    removeManyHotPatches: vi.fn(),
    scaffoldHotPatchProject: vi.fn(),
}));

// The real t() falls back to the raw, argument-free key when no translation was fetched (not
// mocked here) -- this stand-in substitutes args the same way the real translated strings would,
// so the error-message assertions below can check for the actual interpolated text.
vi.mock('../src/translations', () => ({
    t: (key: string, ...args: (string | number)[]) =>
        args.length > 0 ? `${key}(${args.join(', ')})` : key,
}));

// Defaults to enabled so the existing debug-prompt tests below don't each have to opt in; the
// dedicated test for the setting itself flips this back off.
vi.mock('../src/settings', () => ({
    settings: { aiPromptGeneratorEnabled: true },
}));

function flushMicrotasks(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}

// MethodPicker debounces its search by 250ms before calling fetchHotPatchMethods.
function flushDebounce(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 300));
}

function patchMethodResult(): BrowsedMethod {
    return {
        assemblyName: 'MyPatch',
        assemblyFullName: 'MyPatch, Version=0.0.0.0',
        metadataToken: 111,
        declaringTypeName: 'MyPatch.Fixes',
        namespace: 'MyPatch',
        methodName: 'Prefix',
        signature: 'static bool Prefix()',
        isStatic: true,
    };
}

function methodSearchResult(
    methods: BrowsedMethod[],
    overrides: Partial<Omit<MethodSearchResult, 'methods'>> = {},
): MethodSearchResult {
    return {
        methods,
        totalCount: methods.length,
        hasMore: false,
        ...overrides,
    };
}

function targetMethodResult(): BrowsedMethod {
    return {
        assemblyName: 'Assembly-CSharp',
        assemblyFullName: 'Assembly-CSharp, Version=0.0.0.0',
        metadataToken: 222,
        declaringTypeName: 'RimWorld.SomeClass',
        namespace: 'RimWorld',
        methodName: 'SomeMethod',
        signature: 'void SomeMethod()',
        isStatic: false,
    };
}

async function mountPanel(initialTarget?: BrowsedMethod, dedupeKey?: string) {
    vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce([]);
    const wrapper = mount(HotPatchView, {
        props: { initialTarget, dedupeKey },
    });
    await flushMicrotasks();
    return wrapper;
}

async function scaffoldSuccessfully(
    wrapper: Awaited<ReturnType<typeof mountPanel>>,
    projectDirectory = '/dev/patches/Generated',
    expectedAssemblyPath?: string,
) {
    vi.mocked(scaffoldHotPatchProject).mockResolvedValueOnce({
        success: true,
        projectDirectory,
        expectedAssemblyPath,
    });
    await wrapper
        .find('.scaffold-section .path-input')
        .setValue(projectDirectory);
    await wrapper.find('.scaffold-section button.primary').trigger('click');
    await flushMicrotasks();
}

async function loadAssembly(
    wrapper: Awaited<ReturnType<typeof mountPanel>>,
    generation = 1,
) {
    vi.mocked(loadHotPatchAssembly).mockResolvedValueOnce({
        needsConfirmation: false,
        assemblyName: 'MyPatch',
        assemblyFullName: 'MyPatch, Version=0.0.0.0',
        generation,
        removedPatchDescriptions: [],
    });
    vi.mocked(fetchLoadedHotPatchAssemblies).mockResolvedValueOnce([
        { path: '/dev/patch.dll', assemblyName: 'MyPatch', generation },
    ]);
    await wrapper
        .find('.assembly-section .path-input')
        .setValue('/dev/patch.dll');
    await wrapper.find('.assembly-section button.primary').trigger('click');
    await flushMicrotasks();
}

async function selectMethod(
    wrapper: Awaited<ReturnType<typeof mountPanel>>,
    sectionClass: string,
    filterText: string,
    result: BrowsedMethod,
) {
    vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce(
        methodSearchResult([result]),
    );
    await wrapper.find(`.${sectionClass} .filter-input`).setValue(filterText);
    await flushDebounce();
    await wrapper.find(`.${sectionClass} .results button`).trigger('click');
}

describe('HotPatchView', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        localStorage.clear();
        settings.aiPromptGeneratorEnabled = true;
        // Every MethodPicker fetches the assembly-tree root as soon as it mounts (independent of
        // whatever a given test is exercising via the filter/search box) -- a harmless default
        // keeps that fetch from rejecting and drowning out the test's own assertions.
        vi.mocked(fetchHotPatchAssemblies).mockResolvedValue([]);
        vi.mocked(fetchLoadedHotPatchAssemblies).mockResolvedValue([]);
        vi.mocked(fetchSuggestedScaffoldProjectName).mockResolvedValue(
            'DebugAssistancePatch',
        );
    });

    it('shows no active patches initially and refreshes the list on mount', async () => {
        const wrapper = await mountPanel();

        expect(fetchActiveHotPatches).toHaveBeenCalledOnce();
        expect(
            wrapper.find('.active-patches-section .status.empty').exists(),
        ).toBe(true);
    });

    it('loading an assembly shows its name and enables the method sections', async () => {
        const wrapper = await mountPanel();

        expect(wrapper.find('.patch-method-section').exists()).toBe(false);

        await loadAssembly(wrapper);

        expect(loadHotPatchAssembly).toHaveBeenCalledWith(
            '/dev/patch.dll',
            undefined,
        );
        expect(wrapper.find('.patch-method-section').exists()).toBe(true);
        expect(wrapper.find('.target-method-section').exists()).toBe(true);
        expect(wrapper.find('.assembly-section .status.error').exists()).toBe(
            false,
        );
    });

    it('shows the discovered-patches dialog when the load found pre-configured patches', async () => {
        const wrapper = await mountPanel();
        vi.mocked(loadHotPatchAssembly).mockResolvedValueOnce({
            needsConfirmation: false,
            assemblyName: 'MyPatch',
            assemblyFullName: 'MyPatch, Version=0.0.0.0',
            generation: 1,
            removedPatchDescriptions: [],
            discoveredPatches: [
                {
                    target: {
                        assemblyFullName: 'Assembly-CSharp',
                        metadataToken: 1,
                    },
                    targetDescription: 'Some.Type.Method',
                    patchMethod: {
                        assemblyFullName: 'MyPatch',
                        metadataToken: 2,
                    },
                    patchMethodDescription: 'GeneratedPatch.Patches.Prefix',
                    patchType: 'Prefix',
                },
            ],
        });
        vi.mocked(fetchLoadedHotPatchAssemblies).mockResolvedValueOnce([
            { path: '/dev/patch.dll', assemblyName: 'MyPatch', generation: 1 },
        ]);
        await wrapper
            .find('.assembly-section .path-input')
            .setValue('/dev/patch.dll');
        await wrapper.find('.assembly-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.patch-list').exists()).toBe(true);
        expect(wrapper.find('.patch-list').text()).toContain(
            'Some.Type.Method',
        );
    });

    it('does not show the discovered-patches dialog when the load found none', async () => {
        const wrapper = await mountPanel();

        await loadAssembly(wrapper);

        expect(wrapper.find('.patch-list').exists()).toBe(false);
    });

    const previousGenerationPatch: ActivePatch = {
        id: 'patch-1',
        targetDescription: 'Some.Type.Method',
        patchMethodDescription: 'GeneratedPatch.Patches.Prefix',
        patchType: 'Prefix',
        sourceAssemblyPath: '/dev/patch.dll',
        sourceAssemblyName: 'MyPatch',
        sourceAssemblyGeneration: 1,
    };

    it('opens the reload-patches dialog instead of silently removing patches when reloading a path that still has some active', async () => {
        const wrapper = await mountPanel();

        vi.mocked(loadHotPatchAssembly).mockResolvedValueOnce({
            needsConfirmation: true,
            patchesFromPreviousLoad: [previousGenerationPatch],
        });
        await wrapper
            .find('.assembly-section .path-input')
            .setValue('/dev/patch.dll');
        await wrapper.find('.assembly-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.patch-method-section').exists()).toBe(false);
        const prompt = wrapper.find('.assembly-section .patch-list');
        expect(prompt.exists()).toBe(true);
        expect(prompt.text()).toContain('Some.Type.Method');
    });

    it('removes the previous generation patches once the player confirms removal', async () => {
        const wrapper = await mountPanel();
        vi.mocked(loadHotPatchAssembly).mockResolvedValueOnce({
            needsConfirmation: true,
            patchesFromPreviousLoad: [previousGenerationPatch],
        });
        await wrapper
            .find('.assembly-section .path-input')
            .setValue('/dev/patch.dll');
        await wrapper.find('.assembly-section button.primary').trigger('click');
        await flushMicrotasks();

        vi.mocked(loadHotPatchAssembly).mockResolvedValueOnce({
            needsConfirmation: false,
            assemblyName: 'MyPatch',
            assemblyFullName: 'MyPatch, Version=0.0.0.0',
            generation: 2,
            removedPatchDescriptions: ['Prefix on Some.Type.Method'],
        });
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce([]);
        vi.mocked(fetchLoadedHotPatchAssemblies).mockResolvedValueOnce([
            { path: '/dev/patch.dll', assemblyName: 'MyPatch', generation: 2 },
        ]);

        await wrapper
            .find('.assembly-section .remove-confirm')
            .trigger('click');
        await flushMicrotasks();

        expect(loadHotPatchAssembly).toHaveBeenLastCalledWith(
            '/dev/patch.dll',
            ['patch-1'],
        );
        expect(wrapper.find('.assembly-section .patch-list').exists()).toBe(
            false,
        );
        const notice = wrapper.find('.assembly-section .status.warning');
        expect(notice.exists()).toBe(true);
        expect(notice.text()).toContain('Prefix on Some.Type.Method');
    });

    it('keeps the previous generation patches running when the player declines removal', async () => {
        const wrapper = await mountPanel();
        vi.mocked(loadHotPatchAssembly).mockResolvedValueOnce({
            needsConfirmation: true,
            patchesFromPreviousLoad: [previousGenerationPatch],
        });
        await wrapper
            .find('.assembly-section .path-input')
            .setValue('/dev/patch.dll');
        await wrapper.find('.assembly-section button.primary').trigger('click');
        await flushMicrotasks();

        vi.mocked(loadHotPatchAssembly).mockResolvedValueOnce({
            needsConfirmation: false,
            assemblyName: 'MyPatch',
            assemblyFullName: 'MyPatch, Version=0.0.0.0',
            generation: 2,
            removedPatchDescriptions: [],
        });
        vi.mocked(fetchLoadedHotPatchAssemblies).mockResolvedValueOnce([
            { path: '/dev/patch.dll', assemblyName: 'MyPatch', generation: 2 },
        ]);

        await wrapper
            .find('.assembly-section .patch-row input')
            .setValue(false);
        await wrapper
            .find('.assembly-section .remove-confirm')
            .trigger('click');
        await flushMicrotasks();

        expect(loadHotPatchAssembly).toHaveBeenLastCalledWith(
            '/dev/patch.dll',
            [],
        );
        expect(wrapper.find('.assembly-section .status.warning').exists()).toBe(
            false,
        );
        expect(wrapper.find('.patch-method-section').exists()).toBe(true);
    });

    it('cancels the reload without loading the assembly', async () => {
        const wrapper = await mountPanel();
        vi.mocked(loadHotPatchAssembly).mockResolvedValueOnce({
            needsConfirmation: true,
            patchesFromPreviousLoad: [previousGenerationPatch],
        });
        await wrapper
            .find('.assembly-section .path-input')
            .setValue('/dev/patch.dll');
        await wrapper.find('.assembly-section button.primary').trigger('click');
        await flushMicrotasks();

        await wrapper.find('.assembly-section .cancel').trigger('click');
        await flushMicrotasks();

        expect(loadHotPatchAssembly).toHaveBeenCalledTimes(1);
        expect(wrapper.find('.assembly-section .patch-list').exists()).toBe(
            false,
        );
        expect(wrapper.find('.patch-method-section').exists()).toBe(false);
    });

    it('switches to an already-loaded assembly without calling loadHotPatchAssembly', async () => {
        vi.mocked(fetchLoadedHotPatchAssemblies).mockResolvedValueOnce([
            {
                path: '/dev/other.dll',
                assemblyName: 'OtherPatch',
                generation: 3,
            },
        ]);
        const wrapper = await mountPanel();

        await wrapper.find('.loaded-assemblies > button').trigger('click');
        await wrapper.find('.loaded-assemblies .entry').trigger('click');

        expect(loadHotPatchAssembly).not.toHaveBeenCalled();
        expect(wrapper.find('.patch-method-section').exists()).toBe(true);
        expect(
            (
                wrapper.find('.assembly-section .path-input')
                    .element as HTMLInputElement
            ).value,
        ).toBe('/dev/other.dll');
    });

    it('surfaces a load failure without enabling the method sections', async () => {
        const wrapper = await mountPanel();
        vi.mocked(loadHotPatchAssembly).mockRejectedValueOnce(
            new Error('file not found'),
        );

        await wrapper
            .find('.assembly-section .path-input')
            .setValue('/dev/missing.dll');
        await wrapper.find('.assembly-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.assembly-section .status.error').text()).toBe(
            'file not found',
        );
        expect(wrapper.find('.patch-method-section').exists()).toBe(false);
    });

    it('renders the patch type selector above the patch-method picker', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        const html = wrapper.find('.patch-method-section').html();
        expect(html.indexOf('patch-type-label')).toBeLessThan(
            html.indexOf('method-picker'),
        );
    });

    it('offers Replace as a patch type and shows its warning only when selected', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        const select = wrapper.find('.patch-type-label select');
        const optionValues = select
            .findAll('option')
            .map((option) => option.attributes('value'));
        expect(optionValues).toContain('Replace');
        expect(wrapper.find('.patch-method-section .warning').exists()).toBe(
            false,
        );

        await select.setValue('Replace');

        expect(wrapper.find('.patch-method-section .warning').text()).toBe(
            'HotPatch.ReplaceWarning',
        );
    });

    it('drives Change on the patch-method picker when the patch type changes with a method selected', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        const patchMethod = patchMethodResult();
        await selectMethod(
            wrapper,
            'patch-method-section',
            'Prefix',
            patchMethod,
        );
        expect(wrapper.find('.patch-method-section .selected').exists()).toBe(
            true,
        );

        vi.mocked(fetchHotPatchNamespaces).mockResolvedValueOnce([
            { name: patchMethod.namespace, typeCount: 1 },
        ]);
        vi.mocked(fetchHotPatchTypes).mockResolvedValueOnce([
            {
                name: 'Fixes',
                fullName: patchMethod.declaringTypeName,
                methodCount: 1,
            },
        ]);
        vi.mocked(fetchHotPatchMethodsOfType).mockResolvedValueOnce([
            patchMethod,
        ]);

        await wrapper.find('.patch-type-label select').setValue('Postfix');
        await flushMicrotasks();

        expect(wrapper.find('.patch-method-section .selected').exists()).toBe(
            false,
        );
        expect(fetchHotPatchMethodsOfType).toHaveBeenCalledWith(
            patchMethod.assemblyFullName,
            patchMethod.declaringTypeName,
            null,
        );
        expect(
            wrapper.find('.patch-method-section .results li').text(),
        ).toContain('Prefix');
    });

    it('does not drive Change on the patch-method picker when the patch type changes with nothing selected', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        vi.mocked(fetchHotPatchNamespaces).mockClear();

        await wrapper.find('.patch-type-label select').setValue('Postfix');
        await flushMicrotasks();

        expect(fetchHotPatchNamespaces).not.toHaveBeenCalled();
    });

    it('filters the patch-method picker by compatibility once a target method is chosen', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        const targetMethod = targetMethodResult();
        await selectMethod(
            wrapper,
            'target-method-section',
            'SomeMethod',
            targetMethod,
        );

        vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce(
            methodSearchResult([]),
        );
        await wrapper
            .find('.patch-method-section .filter-input')
            .setValue('Prefix');
        await flushDebounce();

        expect(fetchHotPatchMethods).toHaveBeenLastCalledWith(
            '/dev/patch.dll',
            'Prefix',
            {
                target: {
                    assemblyFullName: targetMethod.assemblyFullName,
                    metadataToken: targetMethod.metadataToken,
                },
                patchType: 'Prefix',
            },
        );
    });

    it('has the compatibility filter checkbox checked by default', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        expect(
            wrapper.find<HTMLInputElement>('.compatibility-filter-label input')
                .element.checked,
        ).toBe(true);
    });

    it('stops filtering the patch-method picker by compatibility once the checkbox is unchecked', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        const targetMethod = targetMethodResult();
        await selectMethod(
            wrapper,
            'target-method-section',
            'SomeMethod',
            targetMethod,
        );

        await wrapper.find('.compatibility-filter-label input').setValue(false);

        vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce(
            methodSearchResult([]),
        );
        await wrapper
            .find('.patch-method-section .filter-input')
            .setValue('Prefix');
        await flushDebounce();

        expect(fetchHotPatchMethods).toHaveBeenLastCalledWith(
            '/dev/patch.dll',
            'Prefix',
            null,
        );
    });

    it('has the highlight-matches checkbox checked by default', async () => {
        const wrapper = await mountPanel();

        expect(
            wrapper.find<HTMLInputElement>('.highlight-matches-label input')
                .element.checked,
        ).toBe(true);
    });

    it('stops highlighting matches in both pickers once the checkbox is unchecked', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce(
            methodSearchResult([targetMethodResult()]),
        );
        await wrapper
            .find('.target-method-section .filter-input')
            .setValue('SomeMethod');
        await flushDebounce();
        expect(
            wrapper.find('.target-method-section .results mark').exists(),
        ).toBe(true);

        const checkboxes = wrapper.findAll('.highlight-matches-label input');
        await checkboxes[0].setValue(false);

        vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce(
            methodSearchResult([targetMethodResult()]),
        );
        await wrapper
            .find('.target-method-section .filter-input')
            .setValue('SomeMethod');
        await flushDebounce();
        expect(
            wrapper.find('.target-method-section .results mark').exists(),
        ).toBe(false);

        vi.mocked(fetchHotPatchMethods).mockResolvedValueOnce(
            methodSearchResult([patchMethodResult()]),
        );
        await wrapper
            .find('.patch-method-section .filter-input')
            .setValue('Prefix');
        await flushDebounce();
        expect(
            wrapper.find('.patch-method-section .results mark').exists(),
        ).toBe(false);
    });

    it('applies a patch with the selected target and patch method, then refreshes the active list', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        const patchMethod = patchMethodResult();
        const targetMethod = targetMethodResult();
        await selectMethod(
            wrapper,
            'patch-method-section',
            'Prefix',
            patchMethod,
        );
        await selectMethod(
            wrapper,
            'target-method-section',
            'SomeMethod',
            targetMethod,
        );

        const activePatch: ActivePatch = {
            id: 'patch-1',
            targetDescription: 'RimWorld.SomeClass.SomeMethod',
            patchMethodDescription: 'MyPatch.Fixes.Prefix',
            patchType: 'Prefix',
            sourceAssemblyPath: '/dev/patch.dll',
            sourceAssemblyName: 'MyPatch',
            sourceAssemblyGeneration: 1,
        };
        vi.mocked(applyHotPatch).mockResolvedValueOnce({
            success: true,
            patch: activePatch,
        });
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce([activePatch]);
        // Apply auto-drives Change on the patch-method picker, which navigates back into the
        // method's own type -- irrelevant to this test, just needs to not reject unhandled.
        vi.mocked(fetchHotPatchNamespaces).mockResolvedValueOnce([]);
        vi.mocked(fetchHotPatchTypes).mockResolvedValueOnce([]);
        vi.mocked(fetchHotPatchMethodsOfType).mockResolvedValueOnce([]);

        await wrapper.find('.apply-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(applyHotPatch).toHaveBeenCalledWith(
            {
                assemblyFullName: targetMethod.assemblyFullName,
                metadataToken: targetMethod.metadataToken,
            },
            {
                assemblyFullName: patchMethod.assemblyFullName,
                metadataToken: patchMethod.metadataToken,
            },
            'Prefix',
            '/dev/patch.dll',
        );
        const rows = wrapper.findAll('.active-patches-section li');
        expect(rows).toHaveLength(1);
        expect(rows[0].text()).toContain('RimWorld.SomeClass.SomeMethod');
    });

    it('drives Change on the patch-method picker after a successful apply, landing back on its type', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        const patchMethod = patchMethodResult();
        const targetMethod = targetMethodResult();
        await selectMethod(
            wrapper,
            'patch-method-section',
            'Prefix',
            patchMethod,
        );
        await selectMethod(
            wrapper,
            'target-method-section',
            'SomeMethod',
            targetMethod,
        );

        vi.mocked(applyHotPatch).mockResolvedValueOnce({
            success: true,
            patch: {
                id: 'patch-1',
                targetDescription: 'RimWorld.SomeClass.SomeMethod',
                patchMethodDescription: 'MyPatch.Fixes.Prefix',
                patchType: 'Prefix',
                sourceAssemblyPath: '/dev/patch.dll',
                sourceAssemblyName: 'MyPatch',
                sourceAssemblyGeneration: 1,
            },
        });
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce([]);
        vi.mocked(fetchHotPatchNamespaces).mockResolvedValueOnce([
            { name: patchMethod.namespace, typeCount: 1 },
        ]);
        vi.mocked(fetchHotPatchTypes).mockResolvedValueOnce([
            {
                name: 'Fixes',
                fullName: patchMethod.declaringTypeName,
                methodCount: 1,
            },
        ]);
        vi.mocked(fetchHotPatchMethodsOfType).mockResolvedValueOnce([
            patchMethodResult(),
        ]);

        await wrapper.find('.apply-section button.primary').trigger('click');
        await flushMicrotasks();

        const compatibilityFilter = {
            target: {
                assemblyFullName: targetMethod.assemblyFullName,
                metadataToken: targetMethod.metadataToken,
            },
            patchType: 'Prefix',
        };
        expect(wrapper.find('.patch-method-section .selected').exists()).toBe(
            false,
        );
        expect(fetchHotPatchMethodsOfType).toHaveBeenCalledWith(
            patchMethod.assemblyFullName,
            patchMethod.declaringTypeName,
            compatibilityFilter,
        );
        expect(
            wrapper.find('.patch-method-section .results li').text(),
        ).toContain('Prefix');
    });

    it('shows the returned error inline when applying a patch fails, without touching the active list', async () => {
        const wrapper = await mountPanel();
        await loadAssembly(wrapper);

        await selectMethod(
            wrapper,
            'patch-method-section',
            'Prefix',
            patchMethodResult(),
        );
        await selectMethod(
            wrapper,
            'target-method-section',
            'SomeMethod',
            targetMethodResult(),
        );

        vi.mocked(applyHotPatch).mockResolvedValueOnce({
            success: false,
            error: 'wrong prefix signature',
        });

        await wrapper.find('.apply-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.apply-section .status.error').text()).toBe(
            'wrong prefix signature',
        );
        expect(fetchActiveHotPatches).toHaveBeenCalledOnce();
    });

    it('opens the remove-patches dialog for a single patch, and refreshes the list once confirmed', async () => {
        const activePatch: ActivePatch = {
            id: 'patch-1',
            targetDescription: 'RimWorld.SomeClass.SomeMethod',
            patchMethodDescription: 'MyPatch.Fixes.Prefix',
            patchType: 'Prefix',
            sourceAssemblyPath: '/dev/patch.dll',
            sourceAssemblyName: 'MyPatch',
            sourceAssemblyGeneration: 1,
        };
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce([activePatch]);
        const wrapper = mount(HotPatchView);
        await flushMicrotasks();

        expect(wrapper.findAll('.active-patches-section li')).toHaveLength(1);

        await wrapper
            .find('.active-patches-section .active-list-row button')
            .trigger('click');
        await flushMicrotasks();

        expect(removeManyHotPatches).not.toHaveBeenCalled();
        expect(wrapper.find('.patch-list').exists()).toBe(true);
        expect(wrapper.find('.patch-list').text()).toContain(
            'RimWorld.SomeClass.SomeMethod',
        );

        vi.mocked(removeManyHotPatches).mockResolvedValueOnce(['patch-1']);
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce([]);

        await wrapper.find('.remove-confirm').trigger('click');
        await flushMicrotasks();

        expect(removeManyHotPatches).toHaveBeenCalledWith(['patch-1']);
        expect(wrapper.findAll('.active-patches-section li')).toHaveLength(0);
        expect(wrapper.find('.patch-list').exists()).toBe(false);
    });

    it('closes the remove-patches dialog without calling removeManyHotPatches', async () => {
        const activePatch: ActivePatch = {
            id: 'patch-1',
            targetDescription: 'RimWorld.SomeClass.SomeMethod',
            patchMethodDescription: 'MyPatch.Fixes.Prefix',
            patchType: 'Prefix',
            sourceAssemblyPath: '/dev/patch.dll',
            sourceAssemblyName: 'MyPatch',
            sourceAssemblyGeneration: 1,
        };
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce([activePatch]);
        const wrapper = mount(HotPatchView);
        await flushMicrotasks();

        await wrapper
            .find('.active-patches-section .active-list-row button')
            .trigger('click');
        await flushMicrotasks();

        await wrapper.find('.cancel').trigger('click');
        await flushMicrotasks();

        expect(removeManyHotPatches).not.toHaveBeenCalled();
        expect(wrapper.find('.patch-list').exists()).toBe(false);
        expect(wrapper.findAll('.active-patches-section li')).toHaveLength(1);
    });

    function twoActivePatches(): ActivePatch[] {
        return [
            {
                id: 'patch-1',
                targetDescription: 'RimWorld.SomeClass.SomeMethod',
                patchMethodDescription: 'MyPatch.Fixes.Prefix',
                patchType: 'Prefix',
                sourceAssemblyPath: '/dev/patch.dll',
                sourceAssemblyName: 'MyPatch',
                sourceAssemblyGeneration: 1,
            },
            {
                id: 'patch-2',
                targetDescription: 'RimWorld.OtherClass.OtherMethod',
                patchMethodDescription: 'MyPatch.Fixes.Postfix',
                patchType: 'Postfix',
                sourceAssemblyPath: '/dev/patch.dll',
                sourceAssemblyName: 'MyPatch',
                sourceAssemblyGeneration: 1,
            },
        ];
    }

    it('opens the remove-patches dialog with only the selected patches, and removes them once confirmed', async () => {
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce(
            twoActivePatches(),
        );
        const wrapper = mount(HotPatchView);
        await flushMicrotasks();

        const checkboxes = wrapper.findAll(
            '.active-patches-section .active-list-row input[type=checkbox]',
        );
        await checkboxes[0].setValue(true);
        await wrapper.find('.active-list-toolbar button').trigger('click');
        await flushMicrotasks();

        expect(removeManyHotPatches).not.toHaveBeenCalled();
        expect(wrapper.findAll('.patch-list li')).toHaveLength(1);

        vi.mocked(removeManyHotPatches).mockResolvedValueOnce(['patch-1']);
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce([
            twoActivePatches()[1],
        ]);

        await wrapper.find('.remove-confirm').trigger('click');
        await flushMicrotasks();

        expect(removeManyHotPatches).toHaveBeenCalledWith(['patch-1']);
        expect(wrapper.findAll('.active-patches-section li')).toHaveLength(1);
    });

    it('selects and deselects every patch via the select-all checkbox', async () => {
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce(
            twoActivePatches(),
        );
        const wrapper = mount(HotPatchView);
        await flushMicrotasks();

        await wrapper
            .find('.active-list-toolbar input[type=checkbox]')
            .setValue(true);

        const rowCheckboxes = wrapper.findAll(
            '.active-patches-section .active-list-row input[type=checkbox]',
        );
        for (const checkbox of rowCheckboxes) {
            expect((checkbox.element as HTMLInputElement).checked).toBe(true);
        }

        await wrapper
            .find('.active-list-toolbar input[type=checkbox]')
            .setValue(false);
        for (const checkbox of wrapper.findAll(
            '.active-patches-section .active-list-row input[type=checkbox]',
        )) {
            expect((checkbox.element as HTMLInputElement).checked).toBe(false);
        }
    });

    it('closes the bulk remove-patches dialog without calling removeManyHotPatches', async () => {
        vi.mocked(fetchActiveHotPatches).mockResolvedValueOnce(
            twoActivePatches(),
        );
        const wrapper = mount(HotPatchView);
        await flushMicrotasks();

        const checkboxes = wrapper.findAll(
            '.active-patches-section .active-list-row input[type=checkbox]',
        );
        await checkboxes[0].setValue(true);
        await wrapper.find('.active-list-toolbar button').trigger('click');
        await flushMicrotasks();

        await wrapper.find('.cancel').trigger('click');
        await flushMicrotasks();

        expect(removeManyHotPatches).not.toHaveBeenCalled();
        expect(wrapper.find('.patch-list').exists()).toBe(false);
        expect(wrapper.findAll('.active-patches-section li')).toHaveLength(2);
    });

    // The "Patch this method" entry point: a captured frame's resolved method arrives as
    // the initialTarget prop and must show up already-selected right away -- the target-method
    // picker doesn't depend on a patch assembly being loaded, so it must not wait on one either.
    it('pre-fills the target method from an initialTarget prop without requiring a patch assembly to be loaded', async () => {
        const target = targetMethodResult();
        const wrapper = await mountPanel(target);

        const selected = wrapper.find('.target-method-section .selected');
        expect(selected.exists()).toBe(true);
        expect(selected.text()).toContain(target.declaringTypeName);
    });

    it('leaves the target method unset when no initialTarget prop is given', async () => {
        const wrapper = await mountPanel();

        expect(wrapper.find('.target-method-section .selected').exists()).toBe(
            false,
        );
    });

    it('keeps the target-method picker usable before any patch assembly is loaded', async () => {
        const wrapper = await mountPanel();

        expect(wrapper.find('.target-method-section').exists()).toBe(true);
        expect(wrapper.find('.patch-method-section').exists()).toBe(false);
        expect(wrapper.find('.apply-section').exists()).toBe(false);
    });

    // The scaffolder is reachable without a patch assembly ever being loaded, and
    // without a target method being selected either (the topbar's from-scratch entry point).
    it('generates a project with no target when none is selected, and reports success', async () => {
        const wrapper = await mountPanel();
        vi.mocked(scaffoldHotPatchProject).mockResolvedValueOnce({
            success: true,
        });

        await wrapper
            .find('.scaffold-section .path-input')
            .setValue('/dev/patches/MyPatch');
        await wrapper.find('.scaffold-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(fetchSuggestedScaffoldProjectName).toHaveBeenCalledWith(null);
        expect(scaffoldHotPatchProject).toHaveBeenCalledWith(
            '/dev/patches/MyPatch',
            'DebugAssistancePatch',
            null,
        );
        expect(
            wrapper.find('.scaffold-section .status:not(.error)').exists(),
        ).toBe(true);
        expect(wrapper.find('.scaffold-section .status.error').exists()).toBe(
            false,
        );
    });

    // The "Patch this method" entry point: the pre-filled target method flows into the
    // scaffold request too, not just the apply-patch flow, and is also used to ask the server for
    // a suggested project name (ProjectScaffolder.SuggestProjectName) to seed the field with.
    it('pre-fills the project name and includes the target when opened from a frame', async () => {
        const target = targetMethodResult();
        vi.mocked(fetchSuggestedScaffoldProjectName).mockResolvedValueOnce(
            'DebugAssistancePatch_SomeClass_SomeMethod',
        );
        const wrapper = await mountPanel(target);
        vi.mocked(scaffoldHotPatchProject).mockResolvedValueOnce({
            success: true,
        });

        expect(fetchSuggestedScaffoldProjectName).toHaveBeenCalledWith({
            assemblyFullName: target.assemblyFullName,
            metadataToken: target.metadataToken,
        });
        const nameInput = wrapper.find<HTMLInputElement>(
            '.scaffold-name-input',
        );
        expect(nameInput.element.value).toBe(
            'DebugAssistancePatch_SomeClass_SomeMethod',
        );

        await wrapper
            .find('.scaffold-section .path-input')
            .setValue('/dev/patches/FromFrame');
        await wrapper.find('.scaffold-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(scaffoldHotPatchProject).toHaveBeenCalledWith(
            '/dev/patches/FromFrame',
            'DebugAssistancePatch_SomeClass_SomeMethod',
            {
                assemblyFullName: target.assemblyFullName,
                metadataToken: target.metadataToken,
            },
        );
    });

    it('shows the returned error inline when generating the project fails', async () => {
        const wrapper = await mountPanel();
        vi.mocked(scaffoldHotPatchProject).mockResolvedValueOnce({
            success: false,
            error: 'Destination directory already exists and is not empty.',
        });

        await wrapper
            .find('.scaffold-section .path-input')
            .setValue('/dev/patches/Taken');
        await wrapper.find('.scaffold-section button.primary').trigger('click');
        await flushMicrotasks();

        expect(wrapper.find('.scaffold-section .status.error').text()).toBe(
            'Destination directory already exists and is not empty.',
        );
    });

    it('fills in the patch assembly path with where the generated project will build to', async () => {
        const wrapper = await mountPanel();

        await scaffoldSuccessfully(
            wrapper,
            '/dev/patches/Generated',
            '/dev/patches/Generated/bin/Debug/net481/Generated.dll',
        );

        expect(
            (
                wrapper.find('.assembly-section .path-input')
                    .element as HTMLInputElement
            ).value,
        ).toBe('/dev/patches/Generated/bin/Debug/net481/Generated.dll');
    });

    // The debug-prompt button only makes sense when the panel knows which error it's for --
    // opening it from the topbar (no dedupeKey) must never offer it, no matter what else happens.
    it('never shows the debug-prompt button when opened without a dedupeKey', async () => {
        const wrapper = await mountPanel();

        await scaffoldSuccessfully(wrapper);

        expect(wrapper.find('.copy-debug-prompt').exists()).toBe(false);
    });

    it('never shows the debug-prompt button when the AI prompt generator setting is disabled', async () => {
        settings.aiPromptGeneratorEnabled = false;
        const wrapper = await mountPanel(undefined, 'error-key');

        await scaffoldSuccessfully(wrapper);

        expect(wrapper.find('.copy-debug-prompt').exists()).toBe(false);
    });

    it('shows the debug-prompt button disabled until a project has been generated', async () => {
        const wrapper = await mountPanel(undefined, 'error-key');

        const button = wrapper.find('.copy-debug-prompt');
        expect(button.exists()).toBe(true);
        expect(button.attributes('disabled')).toBeDefined();

        await scaffoldSuccessfully(wrapper);

        expect(
            wrapper.find('.copy-debug-prompt').attributes('disabled'),
        ).toBeUndefined();
    });

    it('copies the generated debug prompt using the scaffolded directory and selected target', async () => {
        const writeText = vi.fn().mockResolvedValueOnce(undefined);
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText },
            configurable: true,
        });
        const target = targetMethodResult();
        const wrapper = await mountPanel(target, 'error-key');
        await scaffoldSuccessfully(wrapper, '/dev/patches/Generated');
        vi.mocked(fetchHotPatchDebugPrompt).mockResolvedValueOnce(
            '# the prompt',
        );

        await wrapper.find('.copy-debug-prompt').trigger('click');
        await flushMicrotasks();

        expect(fetchHotPatchDebugPrompt).toHaveBeenCalledWith(
            'error-key',
            '/dev/patches/Generated',
            {
                assemblyFullName: target.assemblyFullName,
                metadataToken: target.metadataToken,
            },
        );
        expect(writeText).toHaveBeenCalledWith('# the prompt');
    });

    it('shows an error message when copying the debug prompt fails', async () => {
        const wrapper = await mountPanel(undefined, 'error-key');
        await scaffoldSuccessfully(wrapper);
        vi.mocked(fetchHotPatchDebugPrompt).mockRejectedValueOnce(
            new Error('server unreachable'),
        );

        await wrapper.find('.copy-debug-prompt').trigger('click');
        await flushMicrotasks();

        expect(wrapper.text()).toContain('server unreachable');
    });

    it('remembers the assembly and scaffold directory paths across mounts', async () => {
        const wrapper = await mountPanel();
        await wrapper
            .find('.assembly-section .path-input')
            .setValue('/dev/patch.dll');
        await wrapper
            .find('.scaffold-section .path-input')
            .setValue('/dev/patches/Generated');

        const remounted = await mountPanel();

        expect(
            (
                remounted.find('.assembly-section .path-input')
                    .element as HTMLInputElement
            ).value,
        ).toBe('/dev/patch.dll');
        expect(
            (
                remounted.find('.scaffold-section .path-input')
                    .element as HTMLInputElement
            ).value,
        ).toBe('/dev/patches/Generated');
    });

    it('starts with empty paths when nothing was remembered yet', async () => {
        const wrapper = await mountPanel();

        expect(
            (
                wrapper.find('.assembly-section .path-input')
                    .element as HTMLInputElement
            ).value,
        ).toBe('');
        expect(
            (
                wrapper.find('.scaffold-section .path-input')
                    .element as HTMLInputElement
            ).value,
        ).toBe('');
    });
});
