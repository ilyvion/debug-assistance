// Support for the decompiled-code accordion panel's line highlighting/scrolling, replacing the
// mod's own (deleted) HighlightedCode.cs text-marker/manual-scroll-math approach: Prism's
// line-highlight plugin does the actual highlighting from a `data-line` attribute, and the browser's
// native scrollIntoView does the scrolling — this module only computes the attribute value and
// finds the element to scroll to.

// Prism's line-highlight plugin reads `data-line` off the <pre> as a 1-based line number (or
// range, e.g. "3-5", not used here). Returns undefined (omit the attribute) when there is nothing
// to highlight, mirroring HighlightedCode.ApplyMarker's "no highlight line -> unchanged" case.
export function dataLineAttr(
    highlightLine: number | null | undefined,
): string | undefined {
    return highlightLine != null && highlightLine > 0
        ? String(highlightLine)
        : undefined;
}

export interface ScrollTarget {
    querySelector(
        selector: string,
    ): { scrollIntoView(options?: ScrollIntoViewOptions): void } | null;
}

// Prism's line-highlight plugin renders the highlighted line as a `.line-highlight` element
// absolutely positioned inside the <pre>; scrolling that element into view (rather than computing
// a pixel offset by hand, as HighlightedCode.ScrollOffsetForLine did) scrolls the whole panel to
// it. A no-op when there is no highlighted line (the plugin renders nothing to find).
export function scrollToHighlightedLine(container: ScrollTarget): void {
    container
        .querySelector('.line-highlight')
        ?.scrollIntoView({ block: 'center' });
}
