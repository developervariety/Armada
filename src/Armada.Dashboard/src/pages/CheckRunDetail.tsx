import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { deleteCheckRun, getCheckRun, getVessel, getWorkflowProfile, retryCheckRun } from '../api/client';
import type { CheckRun, Vessel, WorkflowProfile } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import CopyButton from '../components/shared/CopyButton';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import StatusBadge from '../components/shared/StatusBadge';

export default function CheckRunDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { t, formatDateTime } = useLocale();
  const { pushToast } = useNotifications();

  const [run, setRun] = useState<CheckRun | null>(null);
  const [vessel, setVessel] = useState<Vessel | null>(null);
  const [profile, setProfile] = useState<WorkflowProfile | null>(null);
  const [loading, setLoading] = useState(true);
  const [retrying, setRetrying] = useState(false);
  const [error, setError] = useState('');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({
    open: false,
    title: '',
    message: '',
    onConfirm: () => {},
  });

  useEffect(() => {
    if (!id) return;
    let mounted = true;
    const runId = id;

    async function load() {
      try {
        setLoading(true);
        const nextRun = await getCheckRun(runId);
        if (!mounted) return;
        setRun(nextRun);

        const [nextVessel, nextProfile] = await Promise.all([
          nextRun.vesselId ? getVessel(nextRun.vesselId).catch(() => null) : Promise.resolve(null),
          nextRun.workflowProfileId ? getWorkflowProfile(nextRun.workflowProfileId).catch(() => null) : Promise.resolve(null),
        ]);

        if (!mounted) return;
        setVessel(nextVessel);
        setProfile(nextProfile);
        setError('');
      } catch (err: unknown) {
        if (mounted) setError(err instanceof Error ? err.message : t('Failed to load check run.'));
      } finally {
        if (mounted) setLoading(false);
      }
    }

    load();
    return () => { mounted = false; };
  }, [id, t]);

  const outputLineCount = useMemo(() => Math.max(1, (run?.output || '').split(/\r?\n/).length), [run?.output]);

  async function handleRetry() {
    if (!run) return;
    try {
      setRetrying(true);
      const retried = await retryCheckRun(run.id);
      pushToast(retried.status === 'Passed' ? 'success' : 'warning', t('Retry completed with status {{status}}.', { status: retried.status }));
      navigate(`/checks/${retried.id}`);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Retry failed.'));
    } finally {
      setRetrying(false);
    }
  }

  function handleDelete() {
    if (!run) return;
    setConfirm({
      open: true,
      title: t('Delete Check Run'),
      message: t('Delete check run "{{id}}"?', { id: run.id }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteCheckRun(run.id);
          pushToast('warning', t('Check run deleted.'));
          navigate('/checks');
        } catch (err: unknown) {
          setError(err instanceof Error ? err.message : t('Delete failed.'));
        }
      },
    });
  }

  if (loading) return <p className="text-dim">{t('Loading...')}</p>;
  if (!run) return <p className="text-dim">{t('Check run not found.')}</p>;

  return (
    <div>
      <div className="breadcrumb">
        <Link to="/checks">{t('Checks')}</Link> <span className="breadcrumb-sep">&gt;</span> <span>{run.label || run.id}</span>
      </div>

      <div className="detail-header">
        <h2>{run.label || run.type}</h2>
        <div className="inline-actions">
          <StatusBadge status={run.status} />
          <button className="btn btn-sm" onClick={() => setJsonData({ open: true, title: run.label || run.id, data: run })}>
            {t('View JSON')}
          </button>
          <button className="btn btn-sm" disabled={retrying} onClick={handleRetry}>
            {retrying ? t('Retrying...') : t('Retry')}
          </button>
          <button className="btn btn-sm btn-danger" onClick={handleDelete}>
            {t('Delete')}
          </button>
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />
      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />
      <ConfirmDialog
        open={confirm.open}
        title={confirm.title}
        message={confirm.message}
        onConfirm={confirm.onConfirm}
        onCancel={() => setConfirm((current) => ({ ...current, open: false }))}
      />

      <div className="detail-grid" style={{ marginBottom: '1rem' }}>
        <div className="detail-field">
          <span className="detail-label">{t('ID')}</span>
          <span className="id-display">
            <span className="mono">{run.id}</span>
            <CopyButton text={run.id} />
          </span>
        </div>
        <div className="detail-field"><span className="detail-label">{t('Type')}</span><span>{run.type}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Status')}</span><StatusBadge status={run.status} /></div>
        <div className="detail-field"><span className="detail-label">{t('Exit Code')}</span><span>{run.exitCode ?? '-'}</span></div>
        <div className="detail-field">
          <span className="detail-label">{t('Vessel')}</span>
          <span>{run.vesselId ? <Link to={`/vessels/${run.vesselId}`}>{vessel?.name || run.vesselId}</Link> : '-'}</span>
        </div>
        <div className="detail-field">
          <span className="detail-label">{t('Workflow Profile')}</span>
          <span>{run.workflowProfileId ? <Link to={`/workflow-profiles/${run.workflowProfileId}`}>{profile?.name || run.workflowProfileId}</Link> : t('Resolved override only')}</span>
        </div>
        <div className="detail-field"><span className="detail-label">{t('Environment')}</span><span>{run.environmentName || '-'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Duration')}</span><span>{run.durationMs != null ? `${Math.round(run.durationMs)} ms` : '-'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Mission ID')}</span><span>{run.missionId ? <Link to={`/missions/${run.missionId}`}>{run.missionId}</Link> : '-'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Voyage ID')}</span><span>{run.voyageId ? <Link to={`/voyages/${run.voyageId}`}>{run.voyageId}</Link> : '-'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Branch')}</span><span className="mono">{run.branchName || '-'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Commit')}</span><span className="mono">{run.commitHash || '-'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Created')}</span><span>{formatDateTime(run.createdUtc)}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Started')}</span><span>{run.startedUtc ? formatDateTime(run.startedUtc) : '-'}</span></div>
        <div className="detail-field"><span className="detail-label">{t('Completed')}</span><span>{run.completedUtc ? formatDateTime(run.completedUtc) : '-'}</span></div>
      </div>

      <div className="card" style={{ marginBottom: '1rem' }}>
        <h3>{t('Command')}</h3>
        <pre className="detail-context-block" style={{ whiteSpace: 'pre-wrap' }}>{run.command}</pre>
      </div>

      {run.summary && (
        <div className="card" style={{ marginBottom: '1rem' }}>
          <h3>{t('Summary')}</h3>
          <p style={{ margin: 0 }}>{run.summary}</p>
        </div>
      )}

      <div className="card" style={{ marginBottom: '1rem' }}>
        <h3>{t('Artifacts')}</h3>
        {run.artifacts.length === 0 ? (
          <p className="text-dim">{t('No artifacts were collected for this run.')}</p>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>{t('Path')}</th>
                  <th>{t('Size')}</th>
                  <th>{t('Last Write')}</th>
                </tr>
              </thead>
              <tbody>
                {run.artifacts.map((artifact) => (
                  <tr key={artifact.path}>
                    <td className="mono">{artifact.path}</td>
                    <td>{artifact.sizeBytes.toLocaleString()} {t('bytes')}</td>
                    <td>{formatDateTime(artifact.lastWriteUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="card">
        <h3>{t('Output')}</h3>
        <div className="workspace-code-view" style={{ maxHeight: '32rem' }}>
          <div className="workspace-code-line-numbers" aria-hidden="true">
            {Array.from({ length: outputLineCount }, (_, index) => (
              <span key={index + 1}>{index + 1}</span>
            ))}
          </div>
          <pre className="workspace-code-block" style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{run.output || t('No output captured.')}</pre>
        </div>
      </div>
    </div>
  );
}
