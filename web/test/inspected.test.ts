import { describe, expect, it } from 'vitest';

import {
    isUnread,
    withAllInspected,
    withInspected,
    withoutInspected,
} from '../src/inspected';
import type { ErrorListEntry } from '../src/types';

function makeEntry(overrides: Partial<ErrorListEntry> = {}): ErrorListEntry {
    return {
        dedupeKey: 'key',
        errorTypeName: 'System.Exception',
        message: 'message',
        occurrenceCount: 1,
        firstSeen: '2026-01-01T00:00:00.000Z',
        lastSeen: '2026-01-01T00:00:00.000Z',
        harmonyRefHash: null,
        topFrameModName: null,
        ...overrides,
    };
}

describe('isUnread', () => {
    it('is true for an entry never marked inspected', () => {
        expect(isUnread(makeEntry(), {})).toBe(true);
    });

    it('is false once inspected at the current occurrence count', () => {
        const entry = makeEntry({ occurrenceCount: 3 });

        expect(isUnread(entry, { key: 3 })).toBe(false);
    });

    it('is true again once a new duplicate arrives after being inspected', () => {
        const entry = makeEntry({ occurrenceCount: 4 });

        expect(isUnread(entry, { key: 3 })).toBe(true);
    });
});

describe('withInspected', () => {
    it('records the entry occurrence count under its dedupeKey', () => {
        const entry = makeEntry({ dedupeKey: 'a', occurrenceCount: 2 });

        expect(withInspected({}, entry)).toEqual({ a: 2 });
    });

    it('leaves other keys untouched', () => {
        const entry = makeEntry({ dedupeKey: 'a', occurrenceCount: 2 });

        expect(withInspected({ b: 5 }, entry)).toEqual({ a: 2, b: 5 });
    });
});

describe('withAllInspected', () => {
    it('records every entry at its current occurrence count', () => {
        const entries = [
            makeEntry({ dedupeKey: 'a', occurrenceCount: 1 }),
            makeEntry({ dedupeKey: 'b', occurrenceCount: 5 }),
        ];

        expect(withAllInspected({}, entries)).toEqual({ a: 1, b: 5 });
    });
});

describe('withoutInspected', () => {
    it('removes just the given key', () => {
        expect(withoutInspected({ a: 1, b: 2 }, 'a')).toEqual({ b: 2 });
    });

    it('is a no-op when the key is absent', () => {
        expect(withoutInspected({ b: 2 }, 'a')).toEqual({ b: 2 });
    });
});
