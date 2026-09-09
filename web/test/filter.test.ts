import { describe, expect, it } from 'vitest';

import { filterAndSort, matchesFilter } from '../src/filter';
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

describe('matchesFilter', () => {
    it('is true for empty or whitespace filter text', () => {
        const entry = makeEntry();

        expect(matchesFilter(entry, '')).toBe(true);
        expect(matchesFilter(entry, '   ')).toBe(true);
    });

    it('finds a match in the error type name', () => {
        const entry = makeEntry({
            errorTypeName: 'System.InvalidOperationException',
        });

        expect(matchesFilter(entry, 'invalidoperation')).toBe(true);
    });

    it('finds a match in the message', () => {
        const entry = makeEntry({ message: 'Something went wrong with Foo' });

        expect(matchesFilter(entry, 'went wrong')).toBe(true);
    });

    it('finds a match in the top frame mod name', () => {
        const entry = makeEntry({ topFrameModName: 'Hospitality' });

        expect(matchesFilter(entry, 'hospitality')).toBe(true);
    });

    it('is false when nothing matches', () => {
        const entry = makeEntry({
            errorTypeName: 'System.Exception',
            message: 'message',
            topFrameModName: 'SomeMod',
        });

        expect(matchesFilter(entry, 'no such text anywhere')).toBe(false);
    });
});

describe('filterAndSort', () => {
    it('excludes non-matching entries', () => {
        const matching = makeEntry({ dedupeKey: 'a', message: 'findme' });
        const nonMatching = makeEntry({ dedupeKey: 'b', message: 'other' });

        const result = filterAndSort([matching, nonMatching], 'findme');

        expect(result).toEqual([matching]);
    });

    it('orders by lastSeen descending', () => {
        const oldest = makeEntry({
            dedupeKey: 'oldest',
            message: 'oldest',
            lastSeen: '2026-01-01T00:00:00.000Z',
        });
        const newest = makeEntry({
            dedupeKey: 'newest',
            message: 'newest',
            lastSeen: '2026-01-03T00:00:00.000Z',
        });
        const middle = makeEntry({
            dedupeKey: 'middle',
            message: 'middle',
            lastSeen: '2026-01-02T00:00:00.000Z',
        });

        const result = filterAndSort([oldest, newest, middle], '');

        expect(result.map((e) => e.dedupeKey)).toEqual([
            'newest',
            'middle',
            'oldest',
        ]);
    });
});
