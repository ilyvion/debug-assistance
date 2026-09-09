import { describe, expect, it } from 'vitest';

import { formatErrorTitle } from '../src/errorTitle';

describe('formatErrorTitle', () => {
    it('joins a known error type name and message with a colon', () => {
        expect(
            formatErrorTitle(
                'System.InvalidOperationException',
                'Sequence contains no elements',
            ),
        ).toBe(
            'System.InvalidOperationException: Sequence contains no elements',
        );
    });

    it('omits the separator when there is no error type name', () => {
        expect(formatErrorTitle('', 'Plain Log.Error text')).toBe(
            'Plain Log.Error text',
        );
    });
});
