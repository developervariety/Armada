import { useState } from 'react';
import { previewUsageRouting } from '../../api/client';
import { useLocale } from '../../context/LocaleContext';
import type { Captain, SmartRoutingPreviewCaptain, SmartRoutingPreviewResult } from '../../types/models';

interface Props {
  /** The draft policy text; preview sends the draft and never saves it. */
  value: string;
  personas: string[];
  captains: Captain[];
}

function captainLabel(c: SmartRoutingPreviewCaptain | null | undefined): string {
  if (!c) return '';
  return `${c.name || c.id}${c.model ? ` (${c.model})` : ''}`;
}

const OUTCOME_CLASS: Record<string, string> = { kept: 'complete', demoted: 'warn', removed: 'failed', outside_routes: 'cancelled', excluded: 'cancelled' };

/** Runs the Smart Routing selector for one persona over the draft policy and shows each step of the selection. */
export default function SmartRoutingPreview({ value, personas, captains }: Props) {
  const { t } = useLocale();
  const [persona, setPersona] = useState(personas.includes('Worker') || personas.length === 0 ? 'Worker' : personas[0]);
  const [priority, setPriority] = useState(100);
  const [model, setModel] = useState('');
  const [title, setTitle] = useState('');
  const [text, setText] = useState('');
  const [result, setResult] = useState<SmartRoutingPreviewResult | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  let valid = true;
  try { JSON.parse(value); } catch { valid = false; }
  const names = new Map(captains.map((c) => [c.id, c.name]));
  const personaOptions = personas.includes(persona) ? personas : [persona, ...personas];
  const clear = () => setResult(null);

  const run = async () => {
    setBusy(true); setError(''); setResult(null);
    try {
      const usageRouting: unknown = JSON.parse(value);
      const body: Record<string, unknown> = { persona, priority, preferredModel: model.trim() || null, usageRouting };
      if (title.trim()) body.missionTitle = title.trim();
      if (text.trim()) body.missionText = text.trim();
      setResult(await previewUsageRouting(body));
    } catch (e) { setError(e instanceof Error ? e.message : t('Preview failed')); }
    finally { setBusy(false); }
  };

  const capacity = result?.capacity;
  return <div className="smart-routing-preview">
    <h4>{t('Preview Smart Routing')}</h4>
    <p className="text-muted">{t('Preview uses the unsaved draft, captain tiers and ranks, and idle captains. It does not save, reserve, or launch work.')}</p>
    <div className="settings-grid">
      <div className="form-group"><label htmlFor="usage-persona">{t('Persona')}</label>
        {personas.length > 0
          ? <select id="usage-persona" value={persona} onChange={(e) => { setPersona(e.target.value); clear(); }}>
            {personaOptions.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
          : <input id="usage-persona" value={persona} onChange={(e) => { setPersona(e.target.value); clear(); }} />}
      </div>
      <div className="form-group"><label htmlFor="usage-priority">{t('Priority')}</label>
        <input id="usage-priority" type="number" value={priority} onChange={(e) => { setPriority(Number(e.target.value)); clear(); }} /></div>
      <div className="form-group"><label htmlFor="usage-model">{t('Preferred model (optional)')}</label>
        <input id="usage-model" value={model} onChange={(e) => { setModel(e.target.value); clear(); }} /></div>
      <div className="form-group"><label htmlFor="usage-title">{t('Mission title (optional)')}</label>
        <input id="usage-title" value={title} onChange={(e) => { setTitle(e.target.value); clear(); }} /></div>
    </div>
    <div className="form-group"><label htmlFor="usage-text">{t('Mission text (optional; asks the capacity decision)')}</label>
      <textarea id="usage-text" rows={3} value={text} onChange={(e) => { setText(e.target.value); clear(); }} /></div>
    <button type="button" className="btn btn-secondary" onClick={run} disabled={busy || !valid}>{busy ? t('Checking usage…') : t('Run preview')}</button>
    {error && <p role="alert" className="text-danger">{error}</p>}
    {result && <div role="status" className="smart-routing-preview-result">
      <p><strong>{t('Chosen captain')}:</strong> {result.chosen ? captainLabel(result.chosen) : t('None; the mission would wait')}
        {' '}<span className="text-muted mono">{result.reason}</span></p>
      {!result.smartRoutingEnabled && <p className="text-muted">{t('Smart Routing is off in this draft, so the Legacy Routing order stands.')}</p>}

      <h5>{t('Legacy Routing order')}</h5>
      {(result.legacyOrder ?? []).length === 0 ? <p className="text-muted">{t('No idle captain passes the Legacy Routing constraints.')}</p> :
        <ol className="smart-routing-order">{result.legacyOrder.map((c) => <li key={c.id}>{captainLabel(c)}</li>)}</ol>}

      <h5>{t('Captain verdicts')}</h5>
      {(result.usageFilter ?? []).length === 0 ? <p className="text-muted">{t('No verdicts.')}</p> :
        <div className="table-wrap"><table className="data-table"><thead><tr>
          <th>{t('Captain')}</th><th>{t('Model')}</th><th>{t('Layer')}</th><th>{t('Account')}</th><th>{t('Outcome')}</th><th>{t('Reason')}</th>
        </tr></thead><tbody>{result.usageFilter.map((v) => <tr key={v.captainId}>
          <td>{names.get(v.captainId) ?? v.captainId}</td>
          <td className="mono">{v.model ?? ''}</td>
          <td>{v.layer ? t(v.layer) : ''}</td>
          <td>{v.accountId ? `${v.accountId}${v.state ? ` (${v.state})` : ''}` : t('No account')}</td>
          <td><span className={`tag ${OUTCOME_CLASS[v.outcome] ?? ''}`}>{t(v.outcome)}</span></td>
          <td className="mono">{v.reason}</td>
        </tr>)}</tbody></table></div>}

      <h5>{t('Capacity reading')}</h5>
      <p>{!capacity?.asked
        ? t('Not asked (no mission title or text). The Default list goes first.')
        : t('{{choice}} list first (source: {{source}})', { choice: capacity.choice, source: capacity.source || t('unknown') })}</p>

      {(result.modelGroups ?? []).length > 0 && <>
        <h5>{t('Model groups in the order tried')}</h5>
        <ol className="smart-routing-order">{result.modelGroups.map((g, i) => <li key={`${g.name}-${i}`}>
          <strong>{g.name}</strong>{g.models.length > 0 && <span className="mono"> [{g.models.join(', ')}]</span>}: {g.captainIds.length === 0
            ? t('no captains') : g.captainIds.map((id) => names.get(id) ?? id).join(', ')}
        </li>)}</ol>
      </>}

      {(result.warnings ?? []).map((w) => <p key={w} className="text-danger">{w}</p>)}
      <details><summary>{t('Raw preview response')}</summary>
        <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{JSON.stringify(result, null, 2)}</pre></details>
    </div>}
  </div>;
}
