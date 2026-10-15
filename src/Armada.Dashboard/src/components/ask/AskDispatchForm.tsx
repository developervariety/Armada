import { useEffect, useState } from 'react';
import { listPipelines, listVessels } from '../../api/client';
import { listAll } from '../../api/paging';
import type { Pipeline, Vessel } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import { buildDispatchArguments, validateDispatch, type DispatchDraft } from '../../lib/askQuickActions';

interface AskDispatchFormProps {
  busy: boolean;
  onSubmit: (args: Record<string, unknown>) => void;
  onCancel: () => void;
}

const MAX_MISSIONS = 25;

/**
 * Inline `/dispatch` form: a vessel, one or more missions (title + optional description), an optional voyage
 * title, and an optional pipeline. Submitting runs the MCP `dispatch` tool through the thread's actions endpoint.
 */
export default function AskDispatchForm({ busy, onSubmit, onCancel }: AskDispatchFormProps) {
  const { t } = useLocale();
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [pipelines, setPipelines] = useState<Pipeline[]>([]);
  const [loadError, setLoadError] = useState('');
  const [loading, setLoading] = useState(true);
  const [draft, setDraft] = useState<DispatchDraft>({ vesselId: '', title: '', missions: [{ title: '', description: '' }], pipelineId: '' });
  const [errors, setErrors] = useState<Record<string, string>>({});

  useEffect(() => {
    let active = true;
    Promise.all([listAll(listVessels), listAll(listPipelines).catch(() => null)])
      .then(([v, p]) => {
        if (!active) return;
        const list = (v.objects || []).slice().sort((a, b) => a.name.localeCompare(b.name));
        setVessels(list);
        setPipelines(p?.objects || []);
        if (list.length === 1) setDraft((d) => ({ ...d, vesselId: list[0].id }));
      })
      .catch((err: unknown) => { if (active) setLoadError(err instanceof Error ? err.message : t('Failed to load vessels.')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [t]);

  function updateMission(index: number, field: 'title' | 'description', value: string) {
    setDraft((d) => ({ ...d, missions: d.missions.map((m, i) => (i === index ? { ...m, [field]: value } : m)) }));
  }

  function submit() {
    const found = validateDispatch(draft);
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    onSubmit(buildDispatchArguments(draft));
  }

  return (
    <form
      className="ask-inline-form"
      aria-label={t('Dispatch a voyage')}
      onSubmit={(e) => { e.preventDefault(); submit(); }}
      onKeyDown={(e) => { if (e.key === 'Escape') { e.preventDefault(); onCancel(); } }}
    >
      <div className="ask-inline-form-head">
        <code>/dispatch</code>
        <span>{t('Dispatch a voyage')}</span>
      </div>
      {loadError && <div className="alert alert-error" role="alert">{loadError}</div>}

      <div className="ask-form-grid">
        <label className="ask-field" title={t('The repository the captains work in. Required.')}>
          <span>{t('Vessel')}</span>
          <select
            title={t('The repository the captains work in. Required.')}
            value={draft.vesselId}
            onChange={(e) => setDraft((d) => ({ ...d, vesselId: e.target.value }))}
            disabled={loading || busy}
            aria-invalid={!!errors.vessel || undefined}
            autoFocus
          >
            <option value="">{loading ? t('Loading...') : t('Choose a vessel')}</option>
            {vessels.map((v) => <option key={v.id} value={v.id}>{v.name}</option>)}
          </select>
          {errors.vessel && <span className="ask-field-error">{t(errors.vessel)}</span>}
        </label>
        <label className="ask-field" title={t('Pipeline the missions run through, for example a plan, implement, and review sequence. Vessel default uses the pipeline set on the vessel or fleet.')}>
          <span>{t('Pipeline (optional)')}</span>
          <select title={t('Pipeline the missions run through, for example a plan, implement, and review sequence. Vessel default uses the pipeline set on the vessel or fleet.')} value={draft.pipelineId} onChange={(e) => setDraft((d) => ({ ...d, pipelineId: e.target.value }))} disabled={busy}>
            <option value="">{t('Vessel default')}</option>
            {pipelines.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
        </label>
        <label className="ask-field ask-field-wide" title={t('Name for the voyage, the batch that groups these missions. Blank uses the first mission title.')}>
          <span>{t('Voyage title (optional)')}</span>
          <input
            title={t('Name for the voyage, the batch that groups these missions. Blank uses the first mission title.')}
            value={draft.title}
            maxLength={200}
            placeholder={t('Defaults to the first mission title')}
            onChange={(e) => setDraft((d) => ({ ...d, title: e.target.value }))}
            disabled={busy}
          />
        </label>
      </div>

      <fieldset className="ask-mission-list">
        <legend>{t('Missions')}</legend>
        {draft.missions.map((mission, index) => (
          <div className="ask-mission-draft" key={index}>
            <div className="ask-mission-draft-fields">
              <input
                value={mission.title}
                maxLength={200}
                placeholder={t('Mission title')}
                title={t('Short name for the mission, for example Fix login redirect. Required.')}
                aria-label={t('Mission {{number}} title', { number: index + 1 })}
                onChange={(e) => updateMission(index, 'title', e.target.value)}
                disabled={busy}
              />
              <textarea
                value={mission.description}
                rows={2}
                placeholder={t('What should the captain do? (optional)')}
                title={t('Instructions for the captain: what to change and how to know it is done. Blank sends just the title.')}
                aria-label={t('Mission {{number}} description', { number: index + 1 })}
                onChange={(e) => updateMission(index, 'description', e.target.value)}
                disabled={busy}
              />
            </div>
            {draft.missions.length > 1 && (
              <button
                type="button"
                className="ask-icon-btn"
                onClick={() => setDraft((d) => ({ ...d, missions: d.missions.filter((_, i) => i !== index) }))}
                aria-label={t('Remove mission {{number}}', { number: index + 1 })}
                title={t('Remove mission {{number}} from this voyage before dispatching.', { number: index + 1 })}
                disabled={busy}
              >
                &times;
              </button>
            )}
          </div>
        ))}
        {errors.missions && <span className="ask-field-error">{t(errors.missions)}</span>}
        <button
          type="button"
          className="btn btn-sm"
          onClick={() => setDraft((d) => ({ ...d, missions: [...d.missions, { title: '', description: '' }] }))}
          disabled={busy || draft.missions.length >= MAX_MISSIONS}
          title={t('Add another mission to the same voyage. Each mission is picked up by its own captain.')}
        >
          + {t('Add mission')}
        </button>
      </fieldset>

      <div className="ask-inline-form-actions">
        <span className="text-dim">{t('Submitting this form is the confirmation; the voyage starts right away.')}</span>
        <button type="button" className="btn btn-sm" onClick={onCancel} disabled={busy} title={t('Close the form without dispatching (Escape).')}>{t('Cancel')}</button>
        <button type="submit" className="btn btn-sm btn-primary" disabled={busy || loading} title={t('Start the voyage now. There is no separate confirmation step; captains begin work as soon as they are free.')}>{busy ? t('Dispatching...') : t('Dispatch')}</button>
      </div>
    </form>
  );
}
