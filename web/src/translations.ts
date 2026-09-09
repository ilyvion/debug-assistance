import { reactive } from 'vue';

import { fetchTranslations } from './api';

// Populated once at startup from GET /api/translations, which resolves each
// DebugAssistance.FrontEnd.* key through RimWorld's own translation system — so the web UI follows
// the player's chosen game language instead of hard-coding English. `reactive` so any component
// calling t() inside its template re-renders once the fetch resolves.
const translations = reactive<Record<string, string>>({});

export async function loadTranslations(): Promise<void> {
    try {
        Object.assign(translations, await fetchTranslations());
    } catch {
        // Leave `translations` empty; t() falls back to the raw key below, keeping the UI usable
        // even if the server is briefly unreachable.
    }
}

// Substitutes `{0}`, `{1}`, ... placeholders with `args`, mirroring the `{0}`-style placeholders
// RimWorld's own `.Translate(args)` uses server-side for the same keys.
export function t(key: string, ...args: (string | number)[]): string {
    const template = translations[key] ?? key;
    return template.replace(/\{(\d+)\}/g, (_match, index: string) => {
        const i = Number(index);
        return i < args.length ? String(args[i]) : '';
    });
}
