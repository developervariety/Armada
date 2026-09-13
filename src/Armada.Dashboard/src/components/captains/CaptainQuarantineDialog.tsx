import { useEffect, useState } from 'react';
import type { CaptainQuarantineRequest } from '../../types/models';
import {
  buildQuarantineRequest,
  EMPTY_QUARANTINE_FORM,
  type QuarantineFormFields,
  type QuarantineHoldMode,
} from '../../lib/captainQuarantine';

interface CaptainQuarantineDialogProps {
  open: boolean;
  captainName: string;
  t: (value: string, params?: Record<string, string | number>) => string;
  submitting?: boolean;
  onSubmit: (request: CaptainQuarantineRequest) => void;
  onCancel: () => void;
}

/** Collects a manual quarantine reason and hold length. The server decides whether the captain may be held. */
export default function CaptainQuarantineDialog({
  open,
  captainName,
  t,
  submitting = false,
  onSubmit,
  onCancel,
}: CaptainQuarantineDialogProps) {
  const [form, setForm] = useState<QuarantineFormFields>(EMPTY_QUARANTINE_FORM);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setForm(EMPTY_QUARANTINE_FORM);
      setError(null);
    }
  }, [open]);

  if (!open) return null;

  function submit() {
    const result = buildQuarantineRequest(form);
    if (!result.request) {
      setError(result.error);
      return;
    }
    setError(null);
    onSubmit(result.request);
  }

  return (
    <div className="modal-overlay" style={{ zIndex: 1500 }} onClick={onCancel}>
      <div className="modal-box" role="dialog" aria-label={t('Quarantine Captain')} onClick={e => e.stopPropagation()}>
        <h3 style={{ marginTop: 0, marginBottom: '1rem' }}>{t('Quarantine Captain')}</h3>
        <p style={{ fontSize: '0.9rem', marginBottom: '1rem', color: 'var(--text)' }}>
          {t('Hold "{{name}}" out of assignment. A captain that still owns a mission, dock or process is refused; stop it first.', { name: captainName })}
        </p>
        <label className="form-label">
          {t('Reason')}
          <input
            aria-label={t('Reason')}
            value={form.reason}
            onChange={e => setForm(f => ({ ...f, reason: e.target.value }))}
          />
        </label>
        <label className="form-label">
          {t('Hold')}
          <select
            aria-label={t('Hold')}
            value={form.mode}
            onChange={e => setForm(f => ({ ...f, mode: e.target.value as QuarantineHoldMode }))}
          >
            <option value="duration">{t('For a duration')}</option>
            <option value="until">{t('Until a time')}</option>
            <option value="indefinite">{t('Until released')}</option>
          </select>
        </label>
        {form.mode === 'duration' && (
          <label className="form-label">
            {t('Minutes')}
            <input
              aria-label={t('Minutes')}
              type="number"
              min={1}
              value={form.durationMinutes}
              onChange={e => setForm(f => ({ ...f, durationMinutes: e.target.value }))}
            />
          </label>
        )}
        {form.mode === 'until' && (
          <label className="form-label">
            {t('Until')}
            <input
              aria-label={t('Until')}
              type="datetime-local"
              value={form.untilLocal}
              onChange={e => setForm(f => ({ ...f, untilLocal: e.target.value }))}
            />
          </label>
        )}
        {error && <p role="alert" style={{ color: 'var(--danger)', fontSize: '0.85rem' }}>{t(error)}</p>}
        <div className="modal-actions">
          <button type="button" className="btn" onClick={onCancel}>{t('Cancel')}</button>
          <button type="button" className="btn btn-primary" onClick={submit} disabled={submitting}>{t('Quarantine')}</button>
        </div>
      </div>
    </div>
  );
}
