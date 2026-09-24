import { describe, it, expect } from 'vitest';
import { installLocaleSensitiveBuiltins } from '../i18n/runtime';
import { formatBucketLabel, formatTooltipTime } from './chartTime';

describe('chart time labels', () => {
  it('format in the chosen app language, not the browser language', () => {
    let locale = 'de-DE';
    installLocaleSensitiveBuiltins(() => locale);
    const ts = Date.UTC(2026, 2, 5, 14, 30);

    const german = formatTooltipTime(ts);
    expect(german).toBe(new Date(ts).toLocaleString('de-DE', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }));
    expect(formatBucketLabel(ts, 60, 72)).toContain(new Date(ts).toLocaleDateString('de-DE', { month: 'short', day: 'numeric' }));

    locale = 'en-US';
    expect(formatTooltipTime(ts)).toBe(new Date(ts).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }));
    expect(formatTooltipTime(ts)).not.toBe(german);
  });
});
