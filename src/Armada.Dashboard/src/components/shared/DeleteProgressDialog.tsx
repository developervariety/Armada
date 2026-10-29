import { useLocale } from '../../context/LocaleContext';
import LoadingIndicator from './LoadingIndicator';

export interface DeleteProgressState {
  status: 'idle' | 'pending' | 'error';
  title: string;
  error: string;
}

interface DeleteProgressDialogProps {
  state: DeleteProgressState;
  onRetry: () => void;
  onDismiss: () => void;
}

/**
 * Modal shown while a slow delete runs. It cannot be dismissed while the request is pending; a failure
 * turns it into an error with Retry and Dismiss.
 */
export default function DeleteProgressDialog({ state, onRetry, onDismiss }: DeleteProgressDialogProps) {
  const { t } = useLocale();
  if (state.status === 'idle') return null;
  const failed = state.status === 'error';

  return (
    <div className="modal-overlay" style={{ zIndex: 1600 }} role="alertdialog" aria-modal="true" aria-label={state.title}>
      <div className="modal-box" onClick={e => e.stopPropagation()}>
        <h3 style={{ marginTop: 0, marginBottom: '1rem' }}>{state.title}</h3>
        {failed ? (
          <p style={{ fontSize: '0.9rem', color: 'var(--danger, var(--text))' }}>{state.error}</p>
        ) : (
          <>
            <LoadingIndicator label={t('This may take some time.')} />
            <p style={{ fontSize: '0.85rem', color: 'var(--text-dim)', textAlign: 'center' }}>
              {t('Keep this page open until the delete finishes.')}
            </p>
          </>
        )}
        {failed && (
          <div className="modal-actions">
            <button className="btn" onClick={onDismiss}>{t('Dismiss')}</button>
            <button className="btn btn-primary" onClick={onRetry}>{t('Retry')}</button>
          </div>
        )}
      </div>
    </div>
  );
}
