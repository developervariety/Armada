import type { CaptainQuarantineRequest } from '../types/models';

/** How long a manual quarantine lasts. */
export type QuarantineHoldMode = 'duration' | 'until' | 'indefinite';

export interface QuarantineFormFields {
  reason: string;
  mode: QuarantineHoldMode;
  durationMinutes: string;
  untilLocal: string;
}

export const EMPTY_QUARANTINE_FORM: QuarantineFormFields = {
  reason: '',
  mode: 'duration',
  durationMinutes: '60',
  untilLocal: '',
};

export interface QuarantineFormResult {
  request: CaptainQuarantineRequest | null;
  error: string | null;
}

/**
 * Validate the form and build the request body. This checks only the form input; whether the captain may be
 * quarantined is decided by the server, which refuses a captain that owns work.
 */
export function buildQuarantineRequest(form: QuarantineFormFields, now: Date = new Date()): QuarantineFormResult {
  const reason = form.reason.trim();
  if (!reason) return { request: null, error: 'A reason is required.' };

  if (form.mode === 'indefinite') return { request: { reason }, error: null };

  if (form.mode === 'duration') {
    const minutes = Number(form.durationMinutes);
    if (!Number.isInteger(minutes) || minutes <= 0) {
      return { request: null, error: 'Duration must be a positive whole number of minutes.' };
    }
    return { request: { reason, durationMinutes: minutes }, error: null };
  }

  const until = new Date(form.untilLocal);
  if (!form.untilLocal || Number.isNaN(until.getTime())) return { request: null, error: 'Choose an expiry time.' };
  if (until.getTime() <= now.getTime()) return { request: null, error: 'The expiry must be in the future.' };
  return { request: { reason, untilUtc: until.toISOString() }, error: null };
}
