import { describe, expect, it, vi } from 'vitest';

import { dataLineAttr, scrollToHighlightedLine } from '../src/highlight';

describe('dataLineAttr', () => {
    it('is undefined when there is no highlight line', () => {
        expect(dataLineAttr(null)).toBeUndefined();
        expect(dataLineAttr(undefined)).toBeUndefined();
    });

    it('is undefined for a non-positive line number', () => {
        expect(dataLineAttr(0)).toBeUndefined();
        expect(dataLineAttr(-1)).toBeUndefined();
    });

    it('stringifies a positive highlight line', () => {
        expect(dataLineAttr(42)).toBe('42');
    });
});

describe('scrollToHighlightedLine', () => {
    it('scrolls the line-highlight element into view when one exists', () => {
        const scrollIntoView = vi.fn();
        const container = {
            querySelector: vi.fn().mockReturnValue({ scrollIntoView }),
        };

        scrollToHighlightedLine(container);

        expect(container.querySelector).toHaveBeenCalledWith('.line-highlight');
        expect(scrollIntoView).toHaveBeenCalledWith({ block: 'center' });
    });

    it('does nothing when there is no line-highlight element', () => {
        const container = { querySelector: vi.fn().mockReturnValue(null) };

        expect(() => {
            scrollToHighlightedLine(container);
        }).not.toThrow();
    });
});
