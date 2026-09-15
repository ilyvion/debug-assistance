import { afterEach, describe, expect, it, vi } from 'vitest';

import {
    addProbe,
    clearErrors,
    clearProbes,
    decompileFrame,
    decompileProbeFrame,
    deleteError,
    fetchErrors,
    fetchProbeAiPrompt,
    fetchProbeDetail,
    fetchProbes,
    removeActiveProbe,
    removeProbeHit,
    saveCapture,
} from '../src/api';

function jsonResponse(body: unknown, status = 200): Response {
    return {
        ok: status >= 200 && status < 300,
        status,
        json: () => Promise.resolve(body),
    } as unknown as Response;
}

afterEach(() => {
    vi.unstubAllGlobals();
});

describe('fetchErrors', () => {
    it('unwraps the errors array on success', async () => {
        const entries = [{ dedupeKey: 'a' }];
        vi.stubGlobal(
            'fetch',
            vi.fn().mockResolvedValue(jsonResponse({ errors: entries })),
        );

        await expect(fetchErrors()).resolves.toEqual(entries);
    });

    it('throws an ApiError carrying the status and server-provided message', async () => {
        vi.stubGlobal(
            'fetch',
            vi.fn().mockResolvedValue(jsonResponse({ error: 'boom' }, 500)),
        );

        await expect(fetchErrors()).rejects.toMatchObject({
            message: 'boom',
            status: 500,
        });
    });
});

describe('decompileFrame', () => {
    it('returns the body as-is even when the response is a 404', async () => {
        const fetchMock = vi
            .fn()
            .mockResolvedValue(jsonResponse({ error: 'Frame not found' }, 404));
        vi.stubGlobal('fetch', fetchMock);

        const result = await decompileFrame('key', 0, false);

        expect(result).toEqual({ error: 'Frame not found' });
        expect(fetchMock).toHaveBeenCalledWith(
            '/api/errors/key/frames/0/decompile',
            expect.objectContaining({ method: 'POST' }),
        );
    });

    it('requests the patched variant when patched is true', async () => {
        const fetchMock = vi
            .fn()
            .mockResolvedValue(
                jsonResponse({ code: 'void M() {}', highlightLine: 1 }),
            );
        vi.stubGlobal('fetch', fetchMock);

        await decompileFrame('key', 2, true);

        expect(fetchMock).toHaveBeenCalledWith(
            '/api/errors/key/frames/2/decompile-patched',
            expect.objectContaining({ method: 'POST' }),
        );
    });
});

describe('deleteError', () => {
    it('sends a DELETE to the error-specific route', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse({}));
        vi.stubGlobal('fetch', fetchMock);

        await deleteError('some key');

        expect(fetchMock).toHaveBeenCalledWith(
            '/api/errors/some%20key',
            expect.objectContaining({ method: 'DELETE' }),
        );
    });

    it('throws an ApiError on a 404', async () => {
        vi.stubGlobal(
            'fetch',
            vi
                .fn()
                .mockResolvedValue(
                    jsonResponse({ error: 'Error not found' }, 404),
                ),
        );

        await expect(deleteError('missing')).rejects.toMatchObject({
            status: 404,
        });
    });
});

describe('clearErrors', () => {
    it('sends a DELETE to the errors collection and returns the cleared count', async () => {
        const fetchMock = vi
            .fn()
            .mockResolvedValue(jsonResponse({ clearedCount: 3 }));
        vi.stubGlobal('fetch', fetchMock);

        await expect(clearErrors()).resolves.toEqual({ clearedCount: 3 });
        expect(fetchMock).toHaveBeenCalledWith(
            '/api/errors',
            expect.objectContaining({ method: 'DELETE' }),
        );
    });
});

describe('saveCapture', () => {
    it('throws an ApiError on a 409 conflict so the caller can offer to overwrite', async () => {
        vi.stubGlobal(
            'fetch',
            vi
                .fn()
                .mockResolvedValue(
                    jsonResponse(
                        { error: 'A save with that name already exists' },
                        409,
                    ),
                ),
        );

        await expect(saveCapture('existing', false)).rejects.toMatchObject({
            status: 409,
        });
    });
});

describe('fetchProbes', () => {
    it('unwraps the probes array on success', async () => {
        const entries = [{ dedupeKey: 'a' }];
        vi.stubGlobal(
            'fetch',
            vi.fn().mockResolvedValue(jsonResponse({ probes: entries })),
        );

        await expect(fetchProbes()).resolves.toEqual(entries);
    });
});

describe('fetchProbeDetail', () => {
    it('requests the probe by dedupe key', async () => {
        const fetchMock = vi
            .fn()
            .mockResolvedValue(jsonResponse({ dedupeKey: 'key' }));
        vi.stubGlobal('fetch', fetchMock);

        await fetchProbeDetail('some key');

        expect(fetchMock).toHaveBeenCalledWith('/api/probes/some%20key');
    });
});

describe('addProbe', () => {
    it('posts the target to /api/probes/active', async () => {
        const fetchMock = vi
            .fn()
            .mockResolvedValue(jsonResponse({ success: true }));
        vi.stubGlobal('fetch', fetchMock);
        const target = { assemblyFullName: 'Some.Assembly', metadataToken: 1 };

        await addProbe(target);

        expect(fetchMock).toHaveBeenCalledWith(
            '/api/probes/active',
            expect.objectContaining({
                method: 'POST',
                body: JSON.stringify({ target }),
            }),
        );
    });
});

describe('removeActiveProbe', () => {
    it('sends a DELETE to the active probe by id', async () => {
        const fetchMock = vi
            .fn()
            .mockResolvedValue(jsonResponse({ removed: true }));
        vi.stubGlobal('fetch', fetchMock);

        await removeActiveProbe('some id');

        expect(fetchMock).toHaveBeenCalledWith(
            '/api/probes/active/some%20id',
            expect.objectContaining({ method: 'DELETE' }),
        );
    });
});

describe('removeProbeHit', () => {
    it('sends a DELETE to the probe hit by dedupe key', async () => {
        const fetchMock = vi.fn().mockResolvedValue(jsonResponse({}));
        vi.stubGlobal('fetch', fetchMock);

        await removeProbeHit('key');

        expect(fetchMock).toHaveBeenCalledWith(
            '/api/probes/key',
            expect.objectContaining({ method: 'DELETE' }),
        );
    });
});

describe('clearProbes', () => {
    it('sends a DELETE to the probes collection and returns the cleared count', async () => {
        const fetchMock = vi
            .fn()
            .mockResolvedValue(jsonResponse({ clearedCount: 2 }));
        vi.stubGlobal('fetch', fetchMock);

        await expect(clearProbes()).resolves.toEqual({ clearedCount: 2 });
        expect(fetchMock).toHaveBeenCalledWith(
            '/api/probes',
            expect.objectContaining({ method: 'DELETE' }),
        );
    });
});

describe('decompileProbeFrame', () => {
    it('requests the patched variant when patched is true', async () => {
        const fetchMock = vi
            .fn()
            .mockResolvedValue(
                jsonResponse({ code: 'void M() {}', highlightLine: 1 }),
            );
        vi.stubGlobal('fetch', fetchMock);

        await decompileProbeFrame('key', 2, true);

        expect(fetchMock).toHaveBeenCalledWith(
            '/api/probes/key/frames/2/decompile-patched',
            expect.objectContaining({ method: 'POST' }),
        );
    });
});

describe('fetchProbeAiPrompt', () => {
    it('returns the prompt text', async () => {
        vi.stubGlobal(
            'fetch',
            vi.fn().mockResolvedValue(jsonResponse({ prompt: '# Probe' })),
        );

        await expect(fetchProbeAiPrompt('key')).resolves.toEqual('# Probe');
    });
});
