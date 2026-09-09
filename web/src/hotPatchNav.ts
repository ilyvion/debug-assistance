import type { BrowsedMethod } from './types';

interface PendingHotPatchTarget {
    target: BrowsedMethod | null;
    dedupeKey: string | null;
}

// Carries the target method + dedupeKey from wherever the hot patch view is opened (the topbar
// button, or a captured frame's "Patch this method" action) across the router navigation to
// /hotpatch -- a BrowsedMethod doesn't round-trip through a URL query string, so it travels here
// instead. Consumed once by HotPatchView on mount; a direct/refreshed navigation to /hotpatch
// finds nothing pending and falls back to the from-scratch entry point.
let pending: PendingHotPatchTarget | null = null;

export function setPendingHotPatchTarget(
    target: BrowsedMethod | null,
    dedupeKey: string | null,
): void {
    pending = { target, dedupeKey };
}

export function consumePendingHotPatchTarget(): PendingHotPatchTarget {
    const value = pending ?? { target: null, dedupeKey: null };
    pending = null;
    return value;
}
