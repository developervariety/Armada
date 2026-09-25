import { useEffect, useState } from 'react';

interface IncidentCloseDialogProps {
  open: boolean;
  /** Text the incident was opened with, usually an automatic failure reason. */
  openedReason: string | null;
  /** A root cause a person already wrote, offered for editing; null when the cause is still automatic. */
  writtenRootCause: string | null;
  t: (value: string, params?: Record<string, string | number>) => string;
  submitting?: boolean;
  /** The server's refusal of the last attempt, shown in the dialog. */
  refusal?: string | null;
  onSubmit: (rootCause: string) => void;
  onCancel: () => void;
}

/**
 * Asks for the root cause the person closing the incident determined. The server owns the rule (a non-empty
 * cause that differs from the opened reason); the dialog sends what was typed and shows the server's refusal.
 */
export default function IncidentCloseDialog({
  open,
  openedReason,
  writtenRootCause,
  t,
  submitting = false,
  refusal = null,
  onSubmit,
  onCancel,
}: IncidentCloseDialogProps) {
  const [rootCause, setRootCause] = useState('');

  useEffect(() => {
    if (open) setRootCause(writtenRootCause ?? '');
  }, [open, writtenRootCause]);

  if (!open) return null;

  return (
    <div className="modal-overlay" style={{ zIndex: 1500 }} onClick={onCancel}>
      <div className="modal-box" role="dialog" aria-label={t('Close Incident')} onClick={e => e.stopPropagation()}>
        <h3 style={{ marginTop: 0, marginBottom: '1rem' }}>{t('Close Incident')}</h3>
        <p style={{ fontSize: '0.9rem', marginBottom: '1rem', color: 'var(--text)' }}>
          {t('Write the root cause you determined. The text the incident was opened with is an automatic reading and is not accepted as the cause.')}
        </p>
        {openedReason && (
          <p style={{ fontSize: '0.85rem', marginBottom: '1rem', color: 'var(--text-dim)' }}>
            {t('Opened with: {{reason}}', { reason: openedReason })}
          </p>
        )}
        <label className="form-label">
          {t('Root Cause')}
          <textarea
            aria-label={t('Root Cause')}
            rows={4}
            value={rootCause}
            onChange={e => setRootCause(e.target.value)}
          />
        </label>
        {refusal && <p role="alert" style={{ color: 'var(--danger)', fontSize: '0.85rem' }}>{refusal}</p>}
        <div className="modal-actions">
          <button type="button" className="btn" onClick={onCancel}>{t('Cancel')}</button>
          <button type="button" className="btn btn-primary" onClick={() => onSubmit(rootCause)} disabled={submitting}>
            {submitting ? t('Closing...') : t('Close Incident')}
          </button>
        </div>
      </div>
    </div>
  );
}
