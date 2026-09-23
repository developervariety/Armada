import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { listJobs } from '../api/client';
import type { LongRunningJob } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import StatusBadge from '../components/shared/StatusBadge';
import RefreshButton from '../components/shared/RefreshButton';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../lib/useAutoRefresh';

export default function Jobs() {
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const [jobs, setJobs] = useState<LongRunningJob[]>([]);
  const [unreadable, setUnreadable] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [refused, setRefused] = useState(false);
  // Jobs carry no tenant or user, so the routes need a global administrator. Once refused, the page
  // stops requesting them instead of polling into repeated 403s.
  const refusedRef = useRef(false);

  const load = useCallback(async () => {
    if (refusedRef.current) return;
    setLoading(true);
    try {
      const result = await listJobs();
      setJobs(result.objects || []);
      setUnreadable(result.unreadableJournalRecords || 0);
      setError('');
    } catch (err) {
      if ((err as { status?: number } | null)?.status === 403) {
        refusedRef.current = true;
        setRefused(true);
        setError('');
      } else {
        setError(t('Failed to load jobs.'));
      }
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { load(); }, [load]);

  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('jobs', load);

  function time(value: string | null) {
    if (!value) return <span className="text-dim">-</span>;
    return <span className="text-dim" title={formatDateTime(value)}>{formatRelativeTime(value)}</span>;
  }

  return (
    <div className="jobs-page">
      <div className="view-header">
        <div>
          <h2>{t('Jobs')}</h2>
          <p className="text-dim view-subtitle">
            {t('Long-running Admiral jobs: dispatch, code-index refresh, merge processing, disk lifecycle and similar operations. Finished jobs are kept for 14 days.')}
          </p>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
          <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
          <RefreshButton onRefresh={load} title={t('Refresh jobs')} />
        </div>
      </div>

      {error && <div className="alert alert-error">{error}</div>}
      {unreadable > 0 && (
        <div className="alert alert-warning" role="status">
          {t('{{count}} job journal records could not be read and are not listed. The Admiral log names each one.', { count: unreadable })}
        </div>
      )}

      {refused ? (
        <div className="card" style={{ padding: '1.25rem' }}>
          <p className="text-muted">{t('Background jobs are available to global administrators.')}</p>
        </div>
      ) : loading && jobs.length === 0 ? (
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
                <th>{t('Operation')}</th>
                <th>{t('Status')}</th>
                <th>{t('Objective')}</th>
                <th>{t('Vessel')}</th>
                <th>{t('Submitted')}</th>
                <th>{t('Started')}</th>
                <th>{t('Completed')}</th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr key={job.jobId}>
                  <td>
                    {job.operation}
                    <div className="text-dim mono" style={{ fontSize: '0.72rem' }}>{job.jobId}</div>
                    {job.failureMessage && <div className="text-dim" style={{ fontSize: '0.72rem' }}>{job.failureMessage}</div>}
                  </td>
                  <td><StatusBadge status={job.status} /></td>
                  <td className="mono">{job.objectiveId ? <Link to={`/objectives/${job.objectiveId}`}>{job.objectiveId}</Link> : '-'}</td>
                  <td className="mono">{job.vesselId ? <Link to={`/vessels/${job.vesselId}`}>{job.vesselId}</Link> : '-'}</td>
                  <td>{time(job.submittedAtUtc)}</td>
                  <td>{time(job.startedAtUtc)}</td>
                  <td>{time(job.completedAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
