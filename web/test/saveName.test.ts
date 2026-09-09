import { describe, expect, it } from 'vitest';

import { defaultSaveName } from '../src/saveName';

describe('defaultSaveName', () => {
    it("fits within GenText.IsValidFilename's 40-character limit", () => {
        expect(
            defaultSaveName(new Date('2026-09-05T12:50:55.073Z')).length,
        ).toBeLessThanOrEqual(40);
    });

    it('contains no characters GenText.IsValidFilename rejects', () => {
        expect(defaultSaveName(new Date('2026-09-05T12:50:55.073Z'))).toMatch(
            /^[A-Za-z0-9_-]+$/,
        );
    });

    it('formats a fixed date deterministically', () => {
        expect(defaultSaveName(new Date('2026-09-05T12:50:55.073Z'))).toBe(
            'Capture_20260905-125055',
        );
    });
});
