import { useCallback, useEffect, useState } from 'react';
import { listJobs, cancelJob } from '../api/client';
import type { Job } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import StatusBadge from '../components/shared/StatusBadge';
import RefreshButton from '../components/shared/RefreshButton';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../lib/useAutoRefresh';

const TERMINAL = ['Succeeded', 'Failed', 'Cancelled'];

export default function Jobs() {
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { pushToast } = useNotifications();
  const [jobs, setJobs] = useState<Job[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const result = await listJobs();
      setJobs(result.objects || []);
      setError('');
    } catch {
      setError(t('Failed to load jobs.'));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { load(); }, [load]);

  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('jobs', load);

  async function handleCancel(job: Job) {
    try {
      await cancelJob(job.id);
      pushToast('warning', t('Job "{{name}}" cancelled.', { name: job.name }));
      load();
    } catch {
      setError(t('Failed to cancel job.'));
    }
  }

  return (
    <div className="jobs-page">
      <div className="view-header">
        <div>
          <h2>{t('Jobs')}</h2>
          <p className="text-dim view-subtitle">{t('Background jobs and their status.')}</p>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
          <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
          <RefreshButton onRefresh={load} title={t('Refresh jobs')} />
        </div>
      </div>

      {error && <div className="alert alert-error">{error}</div>}

      {loading ? (
        <p className="text-dim">{t('Loading...')}</p>
      ) : jobs.length === 0 ? (
        <div className="card" style={{ padding: '1.25rem' }}>
          <p className="text-muted">{t('No background jobs.')}</p>
        </div>
      ) : (
        <div className="card" style={{ overflowX: 'auto' }}>
          <table className="data-table">
            <thead>
              <tr>
                <th>{t('Name')}</th>
                <th>{t('Kind')}</th>
                <th>{t('Status')}</th>
                <th>{t('Progress')}</th>
                <th>{t('Created')}</th>
                <th>{t('Updated')}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr key={job.id}>
                  <td>
                    {job.name}
                    {job.errorReason && <div className="text-dim" style={{ fontSize: '0.72rem' }}>{job.errorReason}</div>}
                  </td>
                  <td>{job.kind}</td>
                  <td><StatusBadge status={job.status} /></td>
                  <td className="mono">{job.progress}%</td>
                  <td className="text-dim" title={formatDateTime(job.createdUtc)}>{formatRelativeTime(job.createdUtc)}</td>
                  <td className="text-dim" title={formatDateTime(job.lastUpdateUtc)}>{formatRelativeTime(job.lastUpdateUtc)}</td>
                  <td>
                    {!TERMINAL.includes(job.status) && (
                      <button type="button" className="btn btn-sm" onClick={() => handleCancel(job)}>{t('Cancel')}</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
