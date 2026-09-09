import { reactive } from 'vue';

import { fetchSettings, setErrorCaptureEnabled } from './api';

// Populated once at startup from GET /api/settings, mirroring translations.ts, so the mod's own
// Settings.cs stays the single source of truth for which of these features are enabled.
export const settings = reactive({
    aiPromptGeneratorEnabled: false,
    errorCaptureEnabled: true,
});

export async function loadSettings(): Promise<void> {
    try {
        Object.assign(settings, await fetchSettings());
    } catch {
        // Leave the defaults above in place if the server is briefly unreachable.
    }
}

// Unlike the rest of `settings`, this one is also writable from the web UI itself (the
// error-capture toggle) rather than only mirroring Settings.cs - optimistically flips the
// local flag so the button reacts immediately, then reverts it if the write fails.
export async function toggleErrorCaptureEnabled(): Promise<void> {
    const next = !settings.errorCaptureEnabled;
    settings.errorCaptureEnabled = next;
    try {
        Object.assign(settings, await setErrorCaptureEnabled(next));
    } catch {
        settings.errorCaptureEnabled = !next;
    }
}
