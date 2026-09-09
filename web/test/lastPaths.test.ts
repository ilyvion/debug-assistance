import { beforeEach, describe, expect, it } from 'vitest';

import { loadLastPath, saveLastPath } from '../src/lastPaths';

describe('lastPaths', () => {
    beforeEach(() => {
        localStorage.clear();
    });

    it('returns an empty string when nothing was saved for a key', () => {
        expect(loadLastPath('assembly')).toBe('');
    });

    it('round-trips a saved path', () => {
        saveLastPath('assembly', '/dev/patch.dll');

        expect(loadLastPath('assembly')).toBe('/dev/patch.dll');
    });

    it('keeps different keys independent', () => {
        saveLastPath('assembly', '/dev/patch.dll');
        saveLastPath('scaffold-directory', '/dev/patches/Generated');

        expect(loadLastPath('assembly')).toBe('/dev/patch.dll');
        expect(loadLastPath('scaffold-directory')).toBe(
            '/dev/patches/Generated',
        );
    });

    it('clears the stored value when saving an empty (or blank) path', () => {
        saveLastPath('assembly', '/dev/patch.dll');

        saveLastPath('assembly', '   ');

        expect(loadLastPath('assembly')).toBe('');
    });
});
