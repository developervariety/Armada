import { useState } from 'react';
import { useLocale } from '../../context/LocaleContext';
import CopyButton from './CopyButton';

interface RecordDetailModalProps {
  open: boolean;
  title: string;
  subtitle?: string;
  record: Record<string, unknown> | null;
  onClose: () => void;
  onEdit?: () => void;
  editLabel?: string;
}

const HIDDEN_KEYS = new Set(['password', 'secret', 'token', 'bearerToken']);

function isScalar(value: unknown): boolean {
  return value === null || value === undefined || ['string', 'number', 'boolean'].includes(typeof value);
}

function labelize(key: string): string {
  const withSpaces = key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/_/g, ' ')
    .replace(/\bUtc\b/gi, 'UTC')
    .replace(/\bId\b/g, 'ID')
    .replace(/\bUrl\b/gi, 'URL');
  return withSpaces.charAt(0).toUpperCase() + withSpaces.slice(1);
}

function formatValue(key: string, value: unknown): string {
  if (value === null || value === undefined || value === '') return '-';
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (typeof value === 'string' && /Utc$/i.test(key) && !Number.isNaN(Date.parse(value))) {
    return new Date(value).toLocaleString();
  }
  return String(value);
}

/**
 * Generic read-only detail ("View") modal for a record. Renders the record's scalar fields as a
 * readable key/value grid, keeps IDs copyable, offers a raw-JSON section for inspection, and shows an
 * optional Edit action. Used as the row-click affordance for tables whose entities do not have a
 * purpose-built edit modal.
 */
export default function RecordDetailModal({ open, title, subtitle, record, onClose, onEdit, editLabel }: RecordDetailModalProps) {
  const { t } = useLocale();
  const [showJson, setShowJson] = useState(false);

  if (!open || !record) return null;

  const entries = Object.entries(record).filter(([key, value]) => isScalar(value) && !HIDDEN_KEYS.has(key));
  const idValue = typeof record.id === 'string' ? record.id : null;

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal modal-large" onClick={(e) => e.stopPropagation()}>
        <div className="request-detail-header">
          <div>
            <h3>{title}</h3>
            {subtitle && <p className="text-dim" style={{ margin: 0 }}>{subtitle}</p>}
            {idValue && (
              <p className="text-dim mono" style={{ margin: '0.25rem 0 0' }}>
                {idValue} <CopyButton text={idValue} />
              </p>
            )}
          </div>
          <div className="request-detail-header-actions">
            {onEdit && <button className="btn btn-sm btn-primary" onClick={onEdit}>{editLabel || t('Edit')}</button>}
            <button className="btn btn-sm" onClick={() => setShowJson((v) => !v)}>{showJson ? t('Hide JSON') : t('View JSON')}</button>
            <button className="btn btn-sm" onClick={onClose}>{t('Close')}</button>
          </div>
        </div>

        {showJson ? (
          <pre className="request-detail-code" style={{ maxHeight: '60vh', overflow: 'auto' }}>{JSON.stringify(record, null, 2)}</pre>
        ) : (
          <div className="record-detail-grid">
            {entries.map(([key, value]) => (
              <div key={key} className="record-detail-field">
                <span className="record-detail-label">{labelize(key)}</span>
                <span className="record-detail-value">{formatValue(key, value)}</span>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
