import { Link } from 'react-router-dom';
import { useLocale } from '../../context/LocaleContext';
import StatusBadge from '../shared/StatusBadge';
import type { FleetActionRun, FleetActionTargetSnapshot, FleetActionTargetState } from '../../types/models';

interface Props { run: FleetActionRun; onCancel: (id: string) => Promise<void>; }
export default function MissionActionRunCard({ run, onCancel }: Props) {
  const { t } = useLocale();
  const states = new Map(run.state.targetStates.map((state) => [state.targetId, state]));
  const cancellable = run.state.status === 'Pending' || run.state.status === 'Running';
  return <article className="card">
    <div className="card-header"><h3>{run.snapshot.actionName}</h3><StatusBadge status={run.state.status} /></div>
    <p className="text-dim">{t('Run ID')}: {run.id} · {run.state.targetStates.length} {t('targets')}</p>
    {run.state.status === 'Uncertain' && <p className="alert alert-warning">{t('The run outcome is unknown. It is blocked and will not be retried automatically.')}</p>}
    <ul className="list-unstyled">{run.snapshot.targets.map((target) => <Target key={target.targetId} target={target} state={states.get(target.targetId)} />)}</ul>
    {cancellable && <button className="btn btn-sm" onClick={() => void onCancel(run.id)}>{t('Cancel run')}</button>}
  </article>;
}

function Target({ target, state }: { target: FleetActionTargetSnapshot; state?: FleetActionTargetState }) {
  const { t } = useLocale();
  const status = state?.status ?? 'Pending';
  return <li className="card" style={{ margin: '.5rem 0' }}>
    <div className="card-header"><strong>{target.vesselNameSnapshot}</strong><StatusBadge status={status} /></div>
    <p className="text-dim">{target.vesselId} · {t('Pipeline ID')}: {target.resolvedPipelineId ?? t('Vessel default')}{target.resolvedPipelineHash ? ` · ${target.resolvedPipelineHash}` : ''}</p>
    {status === 'Completed' && <p>{t('Voyage completed. Source delivery is not confirmed.')}</p>}
    {status === 'WorkProduced' && <p>{t('Work was produced. Delivery is not confirmed.')}</p>}
    {status === 'Uncertain' && <p className="alert alert-warning">{t('The outcome is unknown. Do not retry this target automatically.')}</p>}
    {state?.reason && <p>{state.reason}</p>}{state?.error && <p className="alert alert-error">{state.error}</p>}
    {state?.voyageId && <p>{t('Voyage')}: <Link to={`/voyages/${encodeURIComponent(state.voyageId)}`}>{state.voyageId}</Link></p>}
    <details><summary>{t('Rendered prompt')}</summary><pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{target.renderedPrompt}</pre></details>
  </li>;
}
