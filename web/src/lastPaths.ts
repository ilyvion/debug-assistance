const PREFIX = 'hot-patch-last-path:';

export function loadLastPath(key: string): string {
    return localStorage.getItem(PREFIX + key) ?? '';
}

export function saveLastPath(key: string, path: string) {
    if (path.trim() === '') {
        localStorage.removeItem(PREFIX + key);
    } else {
        localStorage.setItem(PREFIX + key, path);
    }
}
