import { useLocale } from '../../context/LocaleContext';
import StatusBadge from '../shared/StatusBadge';
import type { FleetAction } from '../../types/models';

interface MissionActionCardProps {
  action: FleetAction; onRun: (action: FleetAction) => void;
  onEdit: (action: FleetAction) => void; onDelete: (action: FleetAction) => void;
}

export default function MissionActionCard({ action, onRun, onEdit, onDelete }: MissionActionCardProps) {
  const { t } = useLocale();
  return <article className="card">
    <div className="card-header"><h3>{action.name}</h3><StatusBadge status={action.active ? 'Active' : 'Inactive'} /></div>
    <p className="text-dim">{t('Mission action')} · {t('{{count}} concurrent vessel(s)', { count: action.concurrency })}</p>
    <p className="text-dim">{action.pipelineId ? `${t('Pipeline ID')}: ${action.pipelineId}` : t('Uses each vessel default pipeline')}</p>
    <details><summary>{t('Saved prompt')}</summary><pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{action.promptTemplate}</pre></details>
    <div className="modal-actions"><button className="btn btn-sm" onClick={() => onRun(action)} disabled={!action.active}>{t('Run')}</button><button className="btn btn-sm" onClick={() => onEdit(action)}>{t('Edit')}</button><button className="btn btn-sm btn-danger" onClick={() => onDelete(action)}>{t('Delete')}</button></div>
  </article>;
}
