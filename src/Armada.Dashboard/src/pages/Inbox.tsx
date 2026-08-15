import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { getInbox } from '../api/client';
import type { InboxItem, InboxSeverity } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import ErrorModal from '../components/shared/ErrorModal';
import RefreshButton from '../components/shared/RefreshButton';

function severityColor(severity: InboxSeverity): string {
  if (severity === 'Critical') return 'var(--danger, #ff6b6b)';
  if (severity === 'Warning') return 'var(--warning, #d98a00)';
  return 'var(--text-dim)';
}

function severityLabel(severity: InboxSeverity): string {
  switch (severity) {
    case 'Critical': return 'Critical';
    case 'Warning': return 'Warning';
    default: return 'Info';
  }
}

/**
 * "Needs you" inbox: everything across the fleet awaiting a decision or intervention, ordered
 * most-urgent first. One-click navigation takes the operator to the underlying entity.
 */
export default function Inbox() {
  const navigate = useNavigate();
  const { t } = useLocale();
  const [items, setItems] = useState<InboxItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  async function load() {
    try {
      setLoading(true);
      const result = await getInbox();
      setItems(result || []);
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load inbox.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { void load(); }, []);

  const counts = useMemo(() => ({
    critical: items.filter((i) => i.severity === 'Critical').length,
    warning: items.filter((i) => i.severity === 'Warning').length,
  }), [items]);

  function openItem(item: InboxItem) {
    if (item.href) navigate(item.href);
  }

  return (
    <div>
      <div className="page-header">
        <div>
          <h2>{t('Needs You')}</h2>
          <p className="text-muted">
            {t('Everything across the fleet that is waiting on a decision or intervention from you.')}
          </p>
        </div>
        <div className="page-actions">
          <RefreshButton onRefresh={load} title={t('Refresh inbox')} />
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />

      <div className="playbook-overview-grid" style={{ marginBottom: '1rem' }}>
        <div className="card playbook-overview-card">
          <span>{t('Total')}</span>
          <strong>{items.length}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Critical')}</span>
          <strong style={{ color: severityColor('Critical') }}>{counts.critical}</strong>
        </div>
        <div className="card playbook-overview-card">
          <span>{t('Warning')}</span>
          <strong style={{ color: severityColor('Warning') }}>{counts.warning}</strong>
        </div>
      </div>

      {loading && items.length === 0 && <div className="text-dim">{t('Loading...')}</div>}

      {!loading && items.length === 0 && !error && (
        <div className="card" style={{ padding: '1rem' }}>
          <strong style={{ color: 'var(--success, #4caf50)' }}>{t('You are all caught up.')}</strong>
          <div className="text-dim">{t('Nothing needs your attention right now.')}</div>
        </div>
      )}

      {items.length > 0 && (
        <div className="card" style={{ padding: 0, overflow: 'hidden' }}>
          <table className="table" style={{ margin: 0 }}>
            <thead>
              <tr>
                <th>{t('Severity')}</th>
                <th>{t('Item')}</th>
                <th>{t('Detail')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item, index) => (
                <tr key={`${item.kind}-${item.entityId ?? index}`} className="inbox-row" onClick={() => openItem(item)}>
                  <td style={{ color: severityColor(item.severity), whiteSpace: 'nowrap' }}>{severityLabel(item.severity)}</td>
                  <td>{item.title}</td>
                  <td className="text-dim">{item.detail}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
