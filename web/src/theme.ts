export type ThemePreference = 'light' | 'system' | 'dark';

const STORAGE_KEY = 'theme-preference';

export function loadThemePreference(): ThemePreference {
    const stored = localStorage.getItem(STORAGE_KEY);
    return stored === 'light' || stored === 'dark' || stored === 'system'
        ? stored
        : 'system';
}

export function saveThemePreference(preference: ThemePreference) {
    localStorage.setItem(STORAGE_KEY, preference);
}

export function applyThemePreference(preference: ThemePreference) {
    const root = document.documentElement;
    root.classList.remove('latte', 'frappe');

    if (preference === 'light') root.classList.add('latte');
    else if (preference === 'dark') root.classList.add('frappe');
}
