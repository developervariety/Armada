import { buildQuarantineRequest, EMPTY_QUARANTINE_FORM } from './captainQuarantine';

describe('buildQuarantineRequest', () => {
  const now = new Date('2026-09-13T12:00:00Z');

  it('requires a reason', () => {
    const result = buildQuarantineRequest({ ...EMPTY_QUARANTINE_FORM, reason: '   ' }, now);
    expect(result.request).toBeNull();
    expect(result.error).toBe('A reason is required.');
  });

  it('builds a duration hold with a trimmed reason', () => {
    const result = buildQuarantineRequest({ ...EMPTY_QUARANTINE_FORM, reason: '  provider out of balance ', durationMinutes: '45' }, now);
    expect(result.error).toBeNull();
    expect(result.request).toEqual({ reason: 'provider out of balance', durationMinutes: 45 });
  });

  it('rejects a non-positive or fractional duration', () => {
    expect(buildQuarantineRequest({ ...EMPTY_QUARANTINE_FORM, reason: 'hold', durationMinutes: '0' }, now).request).toBeNull();
    expect(buildQuarantineRequest({ ...EMPTY_QUARANTINE_FORM, reason: 'hold', durationMinutes: '1.5' }, now).request).toBeNull();
  });

  it('builds an indefinite hold without an expiry', () => {
    const result = buildQuarantineRequest({ ...EMPTY_QUARANTINE_FORM, reason: 'hold', mode: 'indefinite' }, now);
    expect(result.request).toEqual({ reason: 'hold' });
  });

  it('requires a future expiry for an until hold', () => {
    expect(buildQuarantineRequest({ ...EMPTY_QUARANTINE_FORM, reason: 'hold', mode: 'until', untilLocal: '' }, now).error).toBe('Choose an expiry time.');
    expect(buildQuarantineRequest({ ...EMPTY_QUARANTINE_FORM, reason: 'hold', mode: 'until', untilLocal: '2026-09-13T11:00:00Z' }, now).error)
      .toBe('The expiry must be in the future.');
    const future = buildQuarantineRequest({ ...EMPTY_QUARANTINE_FORM, reason: 'hold', mode: 'until', untilLocal: '2026-09-13T15:00:00Z' }, now);
    expect(future.request).toEqual({ reason: 'hold', untilUtc: '2026-09-13T15:00:00.000Z' });
  });
});
