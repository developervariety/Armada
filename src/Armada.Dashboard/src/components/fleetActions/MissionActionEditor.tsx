import { useState, type FormEvent } from 'react';
import { useLocale } from '../../context/LocaleContext';
import type { FleetAction, FleetActionUpsertRequest, Pipeline } from '../../types/models';

interface MissionActionEditorProps {
  action: FleetAction | null; pipelines: Pipeline[]; onClose: () => void;
  onSave: (request: FleetActionUpsertRequest) => Promise<void>;
}

export default function MissionActionEditor({ action, pipelines, onClose, onSave }: MissionActionEditorProps) {
  const { t } = useLocale();
  const [name, setName] = useState(action?.name ?? '');
  const [promptTemplate, setPromptTemplate] = useState(action?.promptTemplate ?? '');
  const [pipelineId, setPipelineId] = useState(action?.pipelineId ?? '');
  const [concurrency, setConcurrency] = useState(action?.concurrency ?? 1);
  const [active, setActive] = useState(action?.active ?? true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!name.trim() || !promptTemplate.trim() || busy) return;
    setBusy(true); setError('');
    try {
      await onSave({ name: name.trim(), promptTemplate, pipelineId: pipelineId || null, concurrency, active });
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Could not save this mission action.'));
    } finally { setBusy(false); }
  }
  return <div className="modal-overlay" onClick={busy ? undefined : onClose}>
    <form className="modal modal-lg" role="dialog" aria-modal="true" aria-labelledby="mission-action-editor-title" onClick={(event) => event.stopPropagation()} onSubmit={(event) => void submit(event)}>
      <h3 id="mission-action-editor-title">{action ? t('Edit mission action') : t('New mission action')}</h3>
      <label>{t('Name')}<input disabled={busy} value={name} onChange={(event) => setName(event.target.value)} required /></label>
      <label>{t('Mission prompt')}<textarea disabled={busy} value={promptTemplate} onChange={(event) => setPromptTemplate(event.target.value)} rows={7} required /></label>
      <label>{t('Pipeline (optional)')}<select disabled={busy} value={pipelineId} onChange={(event) => setPipelineId(event.target.value)}>
        <option value="">{t('Use the vessel default')}</option>{pipelines.filter((pipeline) => pipeline.active).map((pipeline) => <option key={pipeline.id} value={pipeline.id}>{pipeline.name}</option>)}
      </select></label>
      <label>{t('Concurrent vessels (1–8)')}<input disabled={busy} type="number" min={1} max={8} value={concurrency} onChange={(event) => setConcurrency(Math.max(1, Math.min(8, Number(event.target.value) || 1)))} /></label>
      <label className="checkbox-row"><input disabled={busy} type="checkbox" checked={active} onChange={(event) => setActive(event.target.checked)} />{t('Active')}</label>
      {error && <p className="alert alert-error" role="alert">{error}</p>}
      <div className="modal-actions"><button type="button" className="btn" onClick={onClose} disabled={busy}>{t('Cancel')}</button><button type="submit" className="btn btn-primary" disabled={busy || !name.trim() || !promptTemplate.trim()}>{busy ? t('Saving...') : t('Save action')}</button></div>
    </form>
  </div>;
}
