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

  useEffect(() => { load(); }, []);

  const counts = useMemo(() => ({
    critical: items.filter((i) => i.severity === 'Critical').length,
    warning: items.filter((i) => i.severity === 'Warning').length,
  }), [items]);

  return (
    <div>
      <div className="view-header">
        <div>
          <h2>{t('Needs You')}</h2>
          <p className="text-dim view-subtitle">{t('Everything across the fleet that is waiting on a decision or intervention from you.')}</p>
        </div>
        <div className="view-actions">
          <RefreshButton onRefresh={load} title={t('Refresh inbox')} />
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />

      <div className="playbook-overview-grid">
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

      {loading && items.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : items.length === 0 ? (
        <div className="playbook-empty-state">
          <strong>{t('You are all caught up.')}</strong>
          <span>{t('Nothing needs your attention right now.')}</span>
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
          {items.map((item, i) => (
            <div
              key={i}
              className="card clickable"
              style={{ padding: '0.75rem 1rem', borderLeft: `3px solid ${severityColor(item.severity)}`, cursor: 'pointer' }}
              onClick={() => navigate(item.href)}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '1rem' }}>
                <div>
                  <strong>{item.title}</strong>
                  <div className="text-dim" style={{ marginTop: '0.2rem' }}>{item.detail}</div>
                </div>
                <span
                  className="badge"
                  style={{ color: severityColor(item.severity), borderColor: severityColor(item.severity), whiteSpace: 'nowrap' }}
                >
                  {t(item.severity)}
                </span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
