import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { listBatteryRuns } from '../api/client';
import type { BatteryRun } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import StatusBadge from '../components/shared/StatusBadge';
import RefreshButton from '../components/shared/RefreshButton';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../lib/useAutoRefresh';

export default function BatteryRuns() {
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const [runs, setRuns] = useState<BatteryRun[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [refused, setRefused] = useState(false);
  // Battery runs need a global administrator. Once refused, the page stops requesting them.
  const refusedRef = useRef(false);

  const load = useCallback(async () => {
    if (refusedRef.current) return;
    setLoading(true);
    try {
      setRuns(await listBatteryRuns({ limit: 100 }));
      setError('');
    } catch (err) {
      if ((err as { status?: number } | null)?.status === 403) {
        refusedRef.current = true;
        setRefused(true);
        setError('');
      } else {
        setError(t('Failed to load battery runs.'));
      }
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { load(); }, [load]);

  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('battery-runs', load);

  return (
    <div className="battery-page">
      <div className="view-header">
        <div>
          <h2>{t('Battery')}</h2>
          <p className="text-dim view-subtitle">
            {t('Fixture battery runs. Each run gives one captain a pack of small repository tasks and grades the resulting worktrees. Start a run with POST /api/v1/battery/runs.')}
          </p>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
          <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
          <RefreshButton onRefresh={load} title={t('Refresh battery runs')} />
        </div>
      </div>

      {error && <div className="alert alert-error">{error}</div>}

      {refused ? (
        <div className="card" style={{ padding: '1.25rem' }}>
          <p className="text-muted">{t('Battery runs are available to global administrators.')}</p>
        </div>
      ) : loading && runs.length === 0 ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : runs.length === 0 ? (
        <div className="card" style={{ padding: '1.25rem' }}>
          <p className="text-muted">{t('No battery runs.')}</p>
        </div>
      ) : (
        <div className="card" style={{ overflowX: 'auto' }}>
          <table className="data-table">
            <thead>
              <tr>
                <th>{t('Run')}</th>
                <th>{t('Status')}</th>
                <th>{t('Captain')}</th>
                <th>{t('Pack')}</th>
                <th>{t('Passed')}</th>
                <th>{t('Failed')}</th>
                <th>{t('Could not run')}</th>
                <th>{t('Started')}</th>
              </tr>
            </thead>
            <tbody>
              {runs.map((run) => (
                <tr key={run.id}>
                  <td className="mono"><Link to={`/battery/${run.id}`}>{run.id}</Link></td>
                  <td><StatusBadge status={run.status} /></td>
                  <td>
                    {run.captainName || '-'}
                    {run.captainId && <div className="text-dim mono" style={{ fontSize: '0.72rem' }}><Link to={`/captains/${run.captainId}`}>{run.captainId}</Link></div>}
                  </td>
                  <td>{run.packId} <span className="text-dim">v{run.packVersion}</span></td>
                  <td>{run.passed}</td>
                  <td>{run.failed}</td>
                  <td>{run.couldNotRun}</td>
                  <td><span className="text-dim" title={formatDateTime(run.startedUtc)}>{formatRelativeTime(run.startedUtc)}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
