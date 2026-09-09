import { describe, expect, it, vi } from 'vitest';

import { fetchTranslations } from '../src/api';
import { loadTranslations, t } from '../src/translations';

vi.mock('../src/api', () => ({
    fetchTranslations: vi.fn(),
}));

describe('t', () => {
    it('falls back to the raw key when it has not been loaded', () => {
        expect(t('Some.UnknownKey')).toBe('Some.UnknownKey');
    });

    it('substitutes {0}-style placeholders from a loaded translation', async () => {
        vi.mocked(fetchTranslations).mockResolvedValue({
            'Greeting.Hello': 'Hello, {0}! You have {1} messages.',
        });

        await loadTranslations();

        expect(t('Greeting.Hello', 'Alex', 3)).toBe(
            'Hello, Alex! You have 3 messages.',
        );
    });

    it('leaves already-loaded translations in place when a later fetch fails', async () => {
        vi.mocked(fetchTranslations).mockRejectedValue(new Error('offline'));

        await loadTranslations();

        expect(t('Greeting.Hello', 'Sam', 1)).toBe(
            'Hello, Sam! You have 1 messages.',
        );
    });
});
