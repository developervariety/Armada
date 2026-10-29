import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { getBatteryRun } from '../api/client';
import type { BatteryRun, BatteryTaskResult } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import StatusBadge from '../components/shared/StatusBadge';
import RefreshButton from '../components/shared/RefreshButton';
import AutoRefreshSelect from '../components/shared/AutoRefreshSelect';
import { useAutoRefresh } from '../lib/useAutoRefresh';

function formatDuration(durationMs: number | null | undefined) {
  if (durationMs == null) return '-';
  if (durationMs < 1000) return `${Math.round(durationMs)} ms`;
  if (durationMs >= 60_000) return `${(durationMs / 60_000).toFixed(1)} min`;
  return `${(durationMs / 1000).toFixed(1)} s`;
}

function TaskEvidence({ task }: { task: BatteryTaskResult }) {
  const { t } = useLocale();
  const details = task.details;
  if (!details || (details.commands.length === 0 && details.files.length === 0 && details.mutants.length === 0)) return null;
  return (
    <ul className="text-dim" style={{ margin: '0.25rem 0 0', paddingLeft: '1.1rem', fontSize: '0.78rem' }}>
      {details.commands.map((command, index) => (
        <li key={`c${index}`}>
          <span className="mono">{command.commandLine}</span>{' '}
          {command.passed ? t('passed') : command.timedOut ? t('timed out') : t('exit code {{code}}', { code: command.exitCode })}
        </li>
      ))}
      {details.files.map((file) => (
        <li key={`f${file.path}`}>
          <span className="mono">{file.path}</span> {file.matched ? t('matches the expected bytes') : (file.reason || t('differs from the expected bytes'))}
        </li>
      ))}
      {details.mutants.map((mutant) => (
        <li key={`m${mutant.id}`}>
          <span className="mono">{mutant.id}</span> {mutant.killed ? t('detected by the tests') : (mutant.reason || t('not detected by the tests'))}
        </li>
      ))}
    </ul>
  );
}

export default function BatteryRunDetail() {
  const { id } = useParams<{ id: string }>();
  const { t, formatDateTime } = useLocale();
  const [run, setRun] = useState<BatteryRun | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    if (!id) return;
    try {
      setRun(await getBatteryRun(id));
      setError('');
    } catch (err) {
      setError((err as { status?: number } | null)?.status === 404 ? t('Battery run not found.') : t('Failed to load the battery run.'));
    } finally {
      setLoading(false);
    }
  }, [id, t]);

  useEffect(() => { load(); }, [load]);

  const { seconds: refreshSeconds, setSeconds: setRefreshSeconds } = useAutoRefresh('battery-run-detail', load);

  return (
    <div className="battery-detail-page">
      <div className="view-header">
        <div>
          <h2>{t('Battery run')} <span className="mono">{id}</span></h2>
          <p className="text-dim view-subtitle"><Link to="/battery">{t('All battery runs')}</Link></p>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
          <AutoRefreshSelect seconds={refreshSeconds} onChange={setRefreshSeconds} />
          <RefreshButton onRefresh={load} title={t('Refresh battery run')} />
        </div>
      </div>

      {error && <div className="alert alert-error">{error}</div>}
      {loading && !run && !error && <p className="text-dim">{t('Loading...')}</p>}

      {run && (
        <>
          <div className="card" style={{ padding: '1rem', marginBottom: '1rem' }}>
            <dl className="detail-grid">
              <dt>{t('Status')}</dt><dd><StatusBadge status={run.status} /></dd>
              <dt>{t('Captain')}</dt>
              <dd>{run.captainName || '-'} {run.captainId && <Link className="mono" to={`/captains/${run.captainId}`}>{run.captainId}</Link>}</dd>
              <dt>{t('Pack')}</dt><dd>{run.packId} <span className="text-dim">v{run.packVersion}</span></dd>
              <dt>{t('Started')}</dt><dd>{formatDateTime(run.startedUtc)}</dd>
              <dt>{t('Completed')}</dt><dd>{run.completedUtc ? formatDateTime(run.completedUtc) : '-'}</dd>
              <dt>{t('Grades')}</dt>
              <dd>{t('{{passed}} passed, {{failed}} failed, {{unjudged}} could not run', { passed: run.passed, failed: run.failed, unjudged: run.couldNotRun })}</dd>
            </dl>
            {run.error && <div className="alert alert-error" style={{ marginTop: '0.75rem' }}>{run.error}</div>}
          </div>

          <div className="card" style={{ overflowX: 'auto' }}>
            <table className="data-table">
              <thead>
                <tr>
                  <th>{t('Task')}</th>
                  <th>{t('Grade')}</th>
                  <th>{t('Score')}</th>
                  <th>{t('Task time')}</th>
                  <th>{t('Mission time')}</th>
                  <th>{t('Mission')}</th>
                </tr>
              </thead>
              <tbody>
                {run.results.map((task) => (
                  <tr key={task.taskId}>
                    <td>
                      {task.title || task.taskId}
                      <div className="text-dim mono" style={{ fontSize: '0.72rem' }}>{task.taskId}</div>
                      {task.reason && <div className="text-dim" style={{ fontSize: '0.78rem' }}>{task.reason}</div>}
                      <TaskEvidence task={task} />
                    </td>
                    <td><StatusBadge status={task.grade} /></td>
                    <td>{task.score == null ? '-' : task.score.toFixed(2)}</td>
                    <td>{formatDuration(task.durationMs)}</td>
                    <td>{formatDuration(task.missionDurationMs)}</td>
                    <td className="mono">{task.missionId ? <Link to={`/missions/${task.missionId}`}>{task.missionId}</Link> : '-'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
