import type { ErrorListEntry } from './types';

// Pure filter/sort logic for the error list pane, mirroring the mod's own
// (deleted) ErrorListFilter.cs: case-insensitive substring match against
// the error's own type/message and its top frame's resolved mod name,
// most-recently-seen first.
export function matchesFilter(
    entry: ErrorListEntry,
    filterText: string,
): boolean {
    const needle = filterText.trim().toLowerCase();
    if (needle.length === 0) {
        return true;
    }

    const haystacks = [
        entry.errorTypeName,
        entry.message,
        entry.topFrameModName ?? '',
    ];
    return haystacks.some((haystack) =>
        haystack.toLowerCase().includes(needle),
    );
}

export function filterAndSort(
    entries: ErrorListEntry[],
    filterText: string,
): ErrorListEntry[] {
    return entries
        .filter((entry) => matchesFilter(entry, filterText))
        .slice()
        .sort((a, b) => Date.parse(b.lastSeen) - Date.parse(a.lastSeen));
}
