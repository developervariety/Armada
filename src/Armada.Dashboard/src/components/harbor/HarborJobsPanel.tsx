import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { getHarborJobs } from '../../api/client';
import { useLocale } from '../../context/LocaleContext';
import type { HarborJob, HarborJobsQuery } from '../../types/models';
import StatusBadge from '../shared/StatusBadge';

interface LoadError { kind: 'unavailable' | 'unauthorized' | 'forbidden' | 'failure'; message: string; }
function classifyError(error: unknown, t: (value: string) => string): LoadError {
  const status = typeof error === 'object' && error !== null && 'status' in error ? error.status : undefined;
  if (status === 404) return { kind: 'unavailable', message: t('Harbor jobs are unavailable. Harbor may be disabled on this server.') };
  if (status === 403) return { kind: 'forbidden', message: t('You do not have access to these Harbor jobs.') };
  if (status === 401 || (error instanceof Error && error.message === 'Unauthorized')) return { kind: 'unauthorized', message: t('Your session is not authorized. Sign in again to view Harbor jobs.') };
  return { kind: 'failure', message: error instanceof Error ? error.message : t('Could not load Harbor jobs.') };
}

export default function HarborJobsPanel() {
  const { t, formatDateTime } = useLocale();
  const [jobs, setJobs] = useState<HarborJob[]>([]); const [runnerId, setRunnerId] = useState('');
  const [activeOnly, setActiveOnly] = useState(false); const [limit, setLimit] = useState('100');
  const [loading, setLoading] = useState(false); const [loaded, setLoaded] = useState(false); const [error, setError] = useState<LoadError | null>(null);
  const request = useRef(0); const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; request.current++; }; }, []);
  const load = useCallback(async (query: HarborJobsQuery) => {
    const current = ++request.current; setLoading(true); setLoaded(false); setError(null); setJobs([]);
    try {
      const result = await getHarborJobs(query);
      if (mounted.current && current === request.current) { setJobs(result.jobs); setLoaded(true); }
    } catch (err: unknown) {
      if (mounted.current && current === request.current) setError(classifyError(err, t));
    } finally { if (mounted.current && current === request.current) setLoading(false); }
  }, [t]);
  useEffect(() => { void load({ activeOnly: false, limit: 100 }); return () => { request.current++; }; }, [load]);
  function submit(event: FormEvent) {
    event.preventDefault();
    const parsed = Number.parseInt(limit, 10);
    void load({ runnerId: runnerId.trim() || undefined, activeOnly, limit: Number.isFinite(parsed) ? Math.max(1, Math.min(1000, parsed)) : 100 });
  }
  return <section aria-labelledby="harbor-jobs-title">
    <div className="page-header"><div><h2 id="harbor-jobs-title">{t('Harbor jobs')}</h2><p className="text-dim">{t('Read-only records for jobs visible to your account. This view does not report runner connection status.')}</p></div></div>
    <form className="filter-bar" onSubmit={submit}>
      <label>{t('Runner ID')}<input disabled={loading} value={runnerId} onChange={(event) => setRunnerId(event.target.value)} /></label>
      <label className="checkbox-row"><input disabled={loading} type="checkbox" checked={activeOnly} onChange={(event) => setActiveOnly(event.target.checked)} />{t('Active jobs only')}</label>
      <label>{t('Limit (1–1000)')}<input disabled={loading} type="number" min={1} max={1000} value={limit} onChange={(event) => setLimit(event.target.value)} /></label>
      <button className="btn btn-primary" type="submit" disabled={loading}>{loading ? t('Loading...') : t('Refresh jobs')}</button>
    </form>
    {loading && <p role="status">{t('Loading Harbor jobs...')}</p>}
    {error && <p className={error.kind === 'unavailable' ? 'alert alert-warning' : 'alert alert-error'} role="alert">{error.message}</p>}
    {loaded && jobs.length === 0 && <p className="text-dim" role="status">{t('No Harbor jobs match these filters.')}</p>}
    {loaded && jobs.length > 0 && <JobsTable jobs={jobs} formatDateTime={formatDateTime} t={t} />}
  </section>;
}

function JobsTable({ jobs, formatDateTime, t }: { jobs: HarborJob[]; formatDateTime: (value: string | null | undefined) => string; t: (value: string) => string }) {
  return <div className="table-wrap"><table className="table"><thead><tr><th>{t('Job')}</th><th>{t('Runner')}</th><th>{t('State')}</th><th>{t('Mission / Captain')}</th><th>{t('Stop requested')}</th><th>{t('Stop confirmed')}</th><th>{t('Exit / failure')}</th><th>{t('Times')}</th></tr></thead>
    <tbody>{jobs.map((job) => <JobRow key={job.jobId} job={job} formatDateTime={formatDateTime} t={t} />)}</tbody>
  </table></div>;
}

function JobRow({ job, formatDateTime, t }: { job: HarborJob; formatDateTime: (value: string | null | undefined) => string; t: (value: string) => string }) {
  return <tr><td className="mono">{job.jobId}</td><td className="mono">{job.runnerId}</td><td><StatusBadge status={job.state} />{job.state === 'Uncertain' && <p className="text-dim">{t('Outcome is uncertain; this does not confirm that the job stopped.')}</p>}</td>
    <td>{job.missionId && <Link to={`/missions/${encodeURIComponent(job.missionId)}`}>{job.missionId}</Link>}{job.captainId && <div><Link to={`/captains/${encodeURIComponent(job.captainId)}`}>{job.captainId}</Link></div>}</td>
    <td>{job.stopRequested ? t('Yes') : t('No')}</td><td>{job.stopConfirmed ? t('Yes') : t('No')}</td><td>{job.exitCode ?? '—'}{job.failureReason && <div>{job.failureReason}</div>}</td>
    <td><small>{t('Created')}: <time dateTime={job.createdUtc}>{formatDateTime(job.createdUtc)}</time><br />{t('Updated')}: <time dateTime={job.lastUpdateUtc}>{formatDateTime(job.lastUpdateUtc)}</time>{job.completedUtc && <><br />{t('Completed')}: <time dateTime={job.completedUtc}>{formatDateTime(job.completedUtc)}</time></>}</small></td></tr>;
}
