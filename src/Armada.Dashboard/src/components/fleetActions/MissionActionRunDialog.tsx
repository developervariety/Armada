import { useRef, useState } from 'react';
import { useLocale } from '../../context/LocaleContext';
import type { FleetAction, FleetActionRunRequest, Vessel } from '../../types/models';

interface MissionActionRunDialogProps {
  action: FleetAction; vessels: Vessel[]; onClose: () => void;
  onStart: (request: FleetActionRunRequest) => Promise<void>;
}

function newRequestId(): string {
  return typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

export default function MissionActionRunDialog({ action, vessels, onClose, onStart }: MissionActionRunDialogProps) {
  const { t } = useLocale();
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const intent = useRef<{ key: string; requestId: string } | null>(null);
  const selected = vessels.filter((vessel) => selectedIds.includes(vessel.id));
  function toggle(id: string, checked: boolean) {
    setSelectedIds((current) => checked
      ? current.length < 50 ? [...current, id] : current
      : current.filter((value) => value !== id));
  }
  async function submit() {
    if (!selectedIds.length || busy) return;
    const vesselIds = [...selectedIds].sort();
    const key = `${action.id}:${vesselIds.join('|')}`;
    if (intent.current?.key !== key) intent.current = { key, requestId: newRequestId() };
    setBusy(true); setError('');
    try {
      await onStart({ requestId: intent.current.requestId, vesselIds });
      intent.current = null;
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Run request failed. Retrying the same selection will reuse its request ID.'));
    } finally { setBusy(false); }
  }
  return <div className="modal-overlay" onClick={busy ? undefined : onClose}>
    <div className="modal modal-lg" role="dialog" aria-modal="true" aria-labelledby="mission-action-run-title" onClick={(event) => event.stopPropagation()}>
      <h3 id="mission-action-run-title">{t('Run {{name}}', { name: action.name })}</h3>
      <p className="text-dim">{t('Select up to 50 vessels. Review the saved prompt and target list before you start.')}</p>
      <p><strong>{t('Pipeline')}:</strong> {action.pipelineId ?? t('Each vessel default')}</p>
      <div style={{ maxHeight: '14rem', overflowY: 'auto' }}>
        {vessels.map((vessel) => <label key={vessel.id} className="checkbox-row">
          <input type="checkbox" checked={selectedIds.includes(vessel.id)} disabled={busy || (!selectedIds.includes(vessel.id) && selectedIds.length >= 50)} onChange={(event) => toggle(vessel.id, event.target.checked)} />
          {vessel.name} <span className="text-dim">({vessel.id})</span>
        </label>)}
      </div>
      <p>{t('Selected targets')}: {selected.length}/50{selected.length > 0 && ` — ${selected.map((vessel) => vessel.name).join(', ')}`}</p>
      <details open><summary>{t('Prompt template to run')}</summary><pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{action.promptTemplate}</pre></details>
      {error && <p className="alert alert-error" role="alert">{error}</p>}
      <div className="modal-actions"><button className="btn" onClick={onClose} disabled={busy}>{t('Cancel')}</button><button className="btn btn-primary" onClick={() => void submit()} disabled={busy || selectedIds.length === 0}>{busy ? t('Starting...') : t('Start run')}</button></div>
    </div>
  </div>;
}
