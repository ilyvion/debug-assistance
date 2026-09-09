import { describe, expect, it, vi } from 'vitest';

import { fetchSettings, setErrorCaptureEnabled } from '../src/api';
import {
    loadSettings,
    settings,
    toggleErrorCaptureEnabled,
} from '../src/settings';

vi.mock('../src/api', () => ({
    fetchSettings: vi.fn(),
    setErrorCaptureEnabled: vi.fn(),
}));

describe('loadSettings', () => {
    it('defaults aiPromptGeneratorEnabled to false before loading', () => {
        expect(settings.aiPromptGeneratorEnabled).toBe(false);
    });

    it('defaults errorCaptureEnabled to true before loading', () => {
        expect(settings.errorCaptureEnabled).toBe(true);
    });

    it('adopts the fetched settings', async () => {
        vi.mocked(fetchSettings).mockResolvedValue({
            aiPromptGeneratorEnabled: true,
            errorCaptureEnabled: false,
        });

        await loadSettings();

        expect(settings.aiPromptGeneratorEnabled).toBe(true);
        expect(settings.errorCaptureEnabled).toBe(false);
    });

    it('leaves the current settings in place when the fetch fails', async () => {
        vi.mocked(fetchSettings).mockRejectedValue(new Error('offline'));

        await loadSettings();

        expect(settings.aiPromptGeneratorEnabled).toBe(true);
        expect(settings.errorCaptureEnabled).toBe(false);
    });
});

describe('toggleErrorCaptureEnabled', () => {
    it('flips the flag optimistically, then adopts the server response', async () => {
        settings.errorCaptureEnabled = true;
        vi.mocked(setErrorCaptureEnabled).mockResolvedValue({
            aiPromptGeneratorEnabled: true,
            errorCaptureEnabled: false,
        });

        await toggleErrorCaptureEnabled();

        expect(setErrorCaptureEnabled).toHaveBeenCalledWith(false);
        expect(settings.errorCaptureEnabled).toBe(false);
    });

    it('reverts the optimistic flip if the request fails', async () => {
        settings.errorCaptureEnabled = true;
        vi.mocked(setErrorCaptureEnabled).mockRejectedValue(
            new Error('offline'),
        );

        await toggleErrorCaptureEnabled();

        expect(settings.errorCaptureEnabled).toBe(true);
    });
});
