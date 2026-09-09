import type { ErrorListEntry } from './types';

// Tracks, per dedupeKey, the occurrenceCount an error had the last time the player looked at
// it. In-memory only (App.vue's ref) rather than localStorage-backed, so it resets whenever the
// page reloads, mirroring the backend's own in-memory CaptureStore lifetime.
export type InspectedCounts = Record<string, number>;

export function isUnread(
    entry: ErrorListEntry,
    inspectedCounts: InspectedCounts,
): boolean {
    const inspectedAt = inspectedCounts[entry.dedupeKey] ?? -1;
    return inspectedAt < entry.occurrenceCount;
}

export function withInspected(
    inspectedCounts: InspectedCounts,
    entry: ErrorListEntry,
): InspectedCounts {
    return { ...inspectedCounts, [entry.dedupeKey]: entry.occurrenceCount };
}

export function withAllInspected(
    inspectedCounts: InspectedCounts,
    entries: ErrorListEntry[],
): InspectedCounts {
    const next = { ...inspectedCounts };
    for (const entry of entries) {
        next[entry.dedupeKey] = entry.occurrenceCount;
    }
    return next;
}

export function withoutInspected(
    inspectedCounts: InspectedCounts,
    dedupeKey: string,
): InspectedCounts {
    const { [dedupeKey]: _removed, ...rest } = inspectedCounts;
    return rest;
}
