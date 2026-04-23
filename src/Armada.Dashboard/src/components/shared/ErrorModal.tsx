import { useEffect } from 'react';
import { useLocale } from '../../context/LocaleContext';

interface ErrorModalProps {
  error: string;
  onClose: () => void;
}

export default function ErrorModal({ error, onClose }: ErrorModalProps) {
  const { t } = useLocale();

  useEffect(() => {
    const handleEsc = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', handleEsc);
    return () => window.removeEventListener('keydown', handleEsc);
  }, [onClose]);

  if (!error) return null;

  return (
    <div className="modal-overlay" style={{ zIndex: 1500 }} onClick={onClose}>
      <div className="modal error-modal" onClick={e => e.stopPropagation()}>
        <div className="error-modal-header">
          <span className="error-modal-icon">!</span>
          <h3>{t('Error')}</h3>
        </div>
        <p className="error-modal-message">{t(error)}</p>
        <div className="modal-actions">
          <button className="btn btn-primary" onClick={onClose}>{t('Dismiss')}</button>
        </div>
      </div>
    </div>
  );
}
