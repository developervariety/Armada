import { useCallback, useEffect, useState, type FormEvent } from 'react';
import {
  getTypedDecisions, removeTypedDecisionKey, saveTypedDecisionKey, updateTypedDecisions,
  upsertCustomTypedDecision, deleteCustomTypedDecision, installCustomTypedDecisionSeeds,
} from '../api/client';
import PageHeader from '../components/shared/PageHeader';
import RefreshButton from '../components/shared/RefreshButton';
import { useLocale } from '../context/LocaleContext';
import type {
  TypedDecisionMode, TypedDecisionStatus, TypedDecisionsUpdate,
  CustomTypedDecision, CustomTypedQuestion, CustomDecisionSurface, CustomDecisionBinding,
} from '../types/models';

const MODES: TypedDecisionMode[] = ['Off', 'Shadow', 'Gate'];
const NO_KEY_REASON = 'typed_decisions_no_key';

interface RowDraft { mode: TypedDecisionMode; threshold: string }

function draftFrom(status: TypedDecisionStatus): { mode: TypedDecisionMode; rows: Record<string, RowDraft> } {
  const rows: Record<string, RowDraft> = {};
  for (const d of status.decisions ?? []) rows[d.key] = { mode: d.mode, threshold: String(d.threshold) };
  return { mode: status.storedMode, rows };
}

function validThreshold(text: string): boolean {
  if (text.trim() === '') return false;
  const n = Number(text);
  return Number.isFinite(n) && n >= 0 && n <= 1;
}

/**
 * Typed-decision administration: the effective global mode and its reason, the provider key (write-only; the page
 * never receives or shows a key), the global mode, and each decision's mode and gate threshold. Only changed fields
 * are sent, and the page reloads the server state after every save.
 */
export default function TypedDecisionsSettings() {
  const { t } = useLocale();
  const [status, setStatus] = useState<TypedDecisionStatus | null>(null);
  const [mode, setMode] = useState<TypedDecisionMode>('Gate');
  const [rows, setRows] = useState<Record<string, RowDraft>>({});
  const [keyText, setKeyText] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const apply = useCallback((next: TypedDecisionStatus) => {
    setStatus(next);
    const draft = draftFrom(next);
    setMode(draft.mode);
    setRows(draft.rows);
  }, []);

  const load = useCallback(async () => {
    setError('');
    try { apply(await getTypedDecisions()); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
  }, [apply]);

  useEffect(() => { void load(); }, [load]);

  const update: TypedDecisionsUpdate = {};
  if (status && mode !== status.storedMode) update.mode = mode;
  let invalid = false;
  for (const d of status?.decisions ?? []) {
    const row = rows[d.key];
    if (!row) continue;
    const change: { mode?: TypedDecisionMode; gateThreshold?: number } = {};
    if (row.mode !== d.mode) change.mode = row.mode;
    if (!validThreshold(row.threshold)) invalid = true;
    else if (Number(row.threshold) !== d.threshold) change.gateThreshold = Number(row.threshold);
    if (Object.keys(change).length > 0) update.decisions = { ...update.decisions, [d.key]: change };
  }
  const dirty = update.mode !== undefined || update.decisions !== undefined;

  const save = async () => {
    setBusy(true); setMessage(''); setError('');
    try {
      await updateTypedDecisions(update);
      await load();
      setMessage(t('Typed-decision settings saved.'));
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };

  const saveKey = async (e: FormEvent) => {
    e.preventDefault();
    const apiKey = keyText.trim();
    // The field is cleared before the request, so the key is not kept in page state on success or failure.
    setKeyText('');
    if (!apiKey) return;
    setBusy(true); setMessage(''); setError('');
    try {
      await saveTypedDecisionKey(apiKey);
      await load();
      setMessage(t('Key saved.'));
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  };

  const removeKey = async () => {
    setBusy(true); setMessage(''); setError('');
    try {
      const result = await removeTypedDecisionKey();
      await load();
      if (result?.environmentSuppliesKey) setMessage(t('The key file was removed, but the environment variable still supplies a key, so the effective mode is unchanged.'));
      else setMessage(result?.fileRemoved ? t('Key file removed.') : t('No key file was present.'));
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  };

  const noKey = status?.effectiveMode === 'Off' && status.effectiveReason === NO_KEY_REASON;
  const sourceLabel = status?.keySource === 'env' ? t('environment variable') : status?.keySource === 'file' ? t('key file') : status?.keySource ?? '';

  return <div>
    <PageHeader
      title={t('Typed decisions')}
      subtitle={t('The Jev classifier answers closed questions at decision points. The global mode caps every decision.')}
      actions={<RefreshButton onRefresh={load} title={t('Refresh typed decisions')} />}
    />
    {!status ? (error ? null : <p>{t('Loading typed decisions...')}</p>) : <>
      {noKey && <div className="dashboard-alert dashboard-alert-warning typed-decisions-banner" data-testid="typed-decisions-no-key">
        <strong>{t('Off — no Jev key')}</strong>
        <span>{t('Every decision runs its deterministic rule until a key is saved.')}</span>
      </div>}

      <section className="settings-section">
        <h3>{t('Effective mode')}</h3>
        <p><strong>{status.effectiveMode}</strong>{status.effectiveReason && <> <span className="text-muted mono">({status.effectiveReason})</span></>}</p>
        <p className="text-muted">{t('Stored global mode: {{mode}}', { mode: status.storedMode })}</p>
      </section>

      <section className="settings-section">
        <h3>{t('Provider key')}</h3>
        <p>{status.keyPresent
          ? t('Key present (source: {{source}}).', { source: sourceLabel })
          : t('No key.')}</p>
        {status.keySource === 'env' && <p className="text-muted">{t('The environment variable supplies the key and wins over the key file. Remove key deletes only the key file; unset the variable on the server to remove that key.')}</p>}
        <form className="typed-decisions-key" onSubmit={saveKey}>
          <div className="form-group">
            <label htmlFor="typed-decisions-key">{t('Jev API key')}</label>
            <input id="typed-decisions-key" type="password" autoComplete="off" spellCheck={false} value={keyText}
              onChange={(e) => setKeyText(e.target.value)} disabled={busy} />
          </div>
          <div className="typed-decisions-actions">
            <button type="submit" className="btn btn-primary" disabled={busy || !keyText.trim()}>{t('Save key')}</button>
            <button type="button" className="btn btn-secondary" onClick={removeKey} disabled={busy || !status.keyPresent}>{t('Remove key')}</button>
          </div>
        </form>
        <p className="text-muted">{t('The key is written to the server key file and is never shown again.')}</p>
      </section>

      <section className="settings-section">
        <h3>{t('Modes and thresholds')}</h3>
        <div className="form-group typed-decisions-global">
          <label htmlFor="typed-decisions-mode">{t('Global mode')}</label>
          <select id="typed-decisions-mode" value={mode} onChange={(e) => setMode(e.target.value as TypedDecisionMode)} disabled={busy}>
            {MODES.map((m) => <option key={m} value={m}>{t(m)}</option>)}
          </select>
        </div>
        <p className="text-muted">{t('Off runs the rule only. Shadow asks and records while the rule stands. Gate lets an answer at or above the threshold decide.')}</p>
        <div className="table-wrap"><table className="data-table typed-decisions-table">
          <thead><tr><th>{t('Decision')}</th><th>{t('Mode')}</th><th>{t('Threshold')}</th></tr></thead>
          <tbody>{(status.decisions ?? []).map((d) => {
            const row = rows[d.key] ?? { mode: d.mode, threshold: String(d.threshold) };
            const bad = !validThreshold(row.threshold);
            return <tr key={d.key}>
              <td><span className="mono">{d.key}</span><div className="text-muted">{d.description}</div></td>
              <td><select aria-label={t('Mode for {{decision}}', { decision: d.key })} value={row.mode} disabled={busy}
                onChange={(e) => setRows({ ...rows, [d.key]: { ...row, mode: e.target.value as TypedDecisionMode } })}>
                {MODES.map((m) => <option key={m} value={m}>{t(m)}</option>)}
              </select></td>
              <td><input type="number" min={0} max={1} step={0.01} className="typed-decisions-threshold" value={row.threshold} disabled={busy}
                aria-label={t('Threshold for {{decision}}', { decision: d.key })} aria-invalid={bad}
                onChange={(e) => setRows({ ...rows, [d.key]: { ...row, threshold: e.target.value } })} />
                {bad && <div className="text-danger">{t('0 to 1')}</div>}</td>
            </tr>;
          })}</tbody>
        </table></div>
        <div className="typed-decisions-actions">
          <button type="button" className="btn btn-primary" onClick={save} disabled={busy || !dirty || invalid}>{busy ? t('Saving...') : t('Save modes')}</button>
          <button type="button" className="btn btn-secondary" onClick={() => apply(status)} disabled={busy || !dirty}>{t('Discard changes')}</button>
          {dirty && <span className="text-muted">{t('Unsaved changes.')}</span>}
        </div>
      </section>

      <CustomDecisionsSection status={status} onChanged={apply} setError={setError} setMessage={setMessage} />
    </>}
    {message && <p role="status">{message}</p>}
    {error && <p role="alert" className="text-danger">{error}</p>}
  </div>;
}


const SURFACES: CustomDecisionSurface[] = ['CaptainTool', 'MissionDiff'];
const BINDINGS: CustomDecisionBinding[] = ['None', 'MissionDiffFlag'];
const QUESTION_TYPES: CustomTypedQuestion['type'][] = ['noul', 'choice', 'score'];

function blankDecision(): CustomTypedDecision {
  return {
    name: '', mode: 'Off', threshold: 0.9, retainState: false, description: '',
    surface: 'MissionDiff', binding: 'None', stateFields: ['diff', 'output_tail'],
    questions: [{ id: '', type: 'noul', instructions: '', trueMeaning: '', falseMeaning: '' }],
  };
}

function optionsToText(options?: Record<string, string>): string {
  if (!options) return '';
  return Object.entries(options).map(([k, v]) => `${k}: ${v}`).join('\n');
}

function textToOptions(text: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const line of text.split('\n')) {
    const idx = line.indexOf(':');
    if (idx <= 0) continue;
    const key = line.slice(0, idx).trim();
    const val = line.slice(idx + 1).trim();
    if (key) out[key] = val;
  }
  return out;
}

/**
 * Create, edit, and delete user-defined custom typed decisions. Advisory only: a custom decision
 * records its answer and, when bound and gated, raises a flag; it never lands, dispatches, or approves.
 * The list and the editor mirror the built-in decisions, plus the surface, binding, state fields, and
 * questions that make a decision user-defined.
 */
function CustomDecisionsSection(props: {
  status: TypedDecisionStatus;
  onChanged: (next: TypedDecisionStatus) => void;
  setError: (text: string) => void;
  setMessage: (text: string) => void;
}) {
  const { t } = useLocale();
  const { status, onChanged, setError, setMessage } = props;
  const [draft, setDraft] = useState<CustomTypedDecision | null>(null);
  const [editingName, setEditingName] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const startAdd = () => { setEditingName(null); setDraft(blankDecision()); };
  const startEdit = (d: CustomTypedDecision) => { setEditingName(d.name); setDraft({ ...d, questions: d.questions.map((q) => ({ ...q })) }); };
  const cancel = () => { setDraft(null); setEditingName(null); };

  const run = async (fn: () => Promise<TypedDecisionStatus>, ok: string) => {
    setBusy(true); setError(''); setMessage('');
    try { onChanged(await fn()); setMessage(t(ok)); cancel(); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };

  const save = async () => {
    if (!draft) return;
    if (!draft.name.trim()) { setError(t('A decision name is required.')); return; }
    await run(() => upsertCustomTypedDecision(draft.name.trim(), {
      ...draft,
      name: draft.name.trim(),
      stateFields: draft.stateFields.map((f) => f.trim()).filter(Boolean),
      questions: draft.questions.map((q) => ({ ...q, id: q.id.trim() })),
    }), 'Custom decision saved.');
  };

  const remove = async (name: string) => { await run(() => deleteCustomTypedDecision(name), 'Custom decision deleted.'); };
  const installSeeds = async () => { await run(() => installCustomTypedDecisionSeeds(), 'Example decisions installed.'); };

  // Inline mode/threshold change from the list row: send the full existing decision with the
  // override, so an operator can flip Off -> Gate (or tune the threshold) without opening the editor.
  const quickSave = async (d: CustomTypedDecision, patch: Partial<CustomTypedDecision>) => {
    await run(() => upsertCustomTypedDecision(d.name, { ...d, ...patch }), 'Custom decision updated.');
  };

  const setQuestion = (i: number, patch: Partial<CustomTypedQuestion>) => {
    if (!draft) return;
    const questions = draft.questions.map((q, idx) => (idx === i ? { ...q, ...patch } : q));
    setDraft({ ...draft, questions });
  };
  const addQuestion = () => { if (draft) setDraft({ ...draft, questions: [...draft.questions, { id: '', type: 'noul', instructions: '', trueMeaning: '', falseMeaning: '' }] }); };
  const removeQuestion = (i: number) => { if (draft) setDraft({ ...draft, questions: draft.questions.filter((_, idx) => idx !== i) }); };

  return <section className="settings-section" data-testid="custom-decisions">
    <h3>{t('Custom decisions')}</h3>
    <p className="text-muted">{t('Decisions you define. They run at a generic surface, are advisory by default, and can bind only to a fixed conservative action — they never land, dispatch, or approve. The global mode caps them like any decision.')}</p>

    <div className="table-wrap"><table className="data-table">
      <thead><tr><th>{t('Name')}</th><th>{t('Mode')}</th><th>{t('Threshold')}</th><th>{t('Surface')}</th><th>{t('Binding')}</th><th>{t('Questions')}</th><th /></tr></thead>
      <tbody>{(status.custom ?? []).length === 0
        ? <tr><td colSpan={7} className="text-muted">{t('No custom decisions yet.')}</td></tr>
        : (status.custom ?? []).map((d) => <tr key={d.name}>
          <td><span className="mono">{d.name}</span><div className="text-muted">{d.description}</div></td>
          <td><select aria-label={t('Mode for {{decision}}', { decision: d.name })} value={d.mode} disabled={busy}
            onChange={(e) => quickSave(d, { mode: e.target.value as TypedDecisionMode })}>
            {MODES.map((m) => <option key={m} value={m}>{t(m)}</option>)}
          </select></td>
          <td><input type="number" min={0} max={1} step={0.01} className="typed-decisions-threshold" defaultValue={d.threshold} disabled={busy}
            aria-label={t('Threshold for {{decision}}', { decision: d.name })}
            onBlur={(e) => { const v = Number(e.target.value); if (Number.isFinite(v) && v >= 0 && v <= 1 && v !== d.threshold) void quickSave(d, { threshold: v }); }} /></td>
          <td>{d.surface}</td>
          <td>{d.binding}</td>
          <td>{d.questions.length}</td>
          <td className="typed-decisions-actions">
            <button type="button" className="btn btn-secondary btn-sm" disabled={busy} onClick={() => startEdit(d)}>{t('Edit')}</button>
            <button type="button" className="btn btn-danger btn-sm" disabled={busy} onClick={() => remove(d.name)}>{t('Delete')}</button>
          </td>
        </tr>)}
      </tbody>
    </table></div>

    <div className="typed-decisions-actions">
      <button type="button" className="btn btn-primary" disabled={busy || draft !== null} onClick={startAdd}>{t('Add custom decision')}</button>
      <button type="button" className="btn btn-secondary" disabled={busy} onClick={installSeeds}>{t('Install examples')}</button>
    </div>

    {draft && <div className="settings-subsection custom-decision-editor">
      <h4>{editingName ? t('Edit {{name}}', { name: editingName }) : t('New custom decision')}</h4>
      <div className="form-group">
        <label>{t('Name')}</label>
        <input type="text" value={draft.name} disabled={busy || editingName !== null}
          onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
        {editingName !== null && <span className="text-muted">{t('The name is fixed once created; delete and re-add to rename.')}</span>}
      </div>
      <div className="form-group">
        <label>{t('Description')}</label>
        <input type="text" value={draft.description} disabled={busy} onChange={(e) => setDraft({ ...draft, description: e.target.value })} />
      </div>
      <div className="custom-decision-row">
        <div className="form-group">
          <label>{t('Mode')}</label>
          <select value={draft.mode} disabled={busy} onChange={(e) => setDraft({ ...draft, mode: e.target.value as TypedDecisionMode })}>
            {MODES.map((m) => <option key={m} value={m}>{t(m)}</option>)}
          </select>
        </div>
        <div className="form-group">
          <label>{t('Threshold')}</label>
          <input type="number" min={0} max={1} step={0.01} value={draft.threshold} disabled={busy}
            onChange={(e) => setDraft({ ...draft, threshold: Number(e.target.value) })} />
        </div>
        <div className="form-group">
          <label>{t('Surface')}</label>
          <select value={draft.surface} disabled={busy} onChange={(e) => setDraft({ ...draft, surface: e.target.value as CustomDecisionSurface })}>
            {SURFACES.map((sf) => <option key={sf} value={sf}>{sf}</option>)}
          </select>
        </div>
        <div className="form-group">
          <label>{t('Binding')}</label>
          <select value={draft.binding} disabled={busy} onChange={(e) => setDraft({ ...draft, binding: e.target.value as CustomDecisionBinding })}>
            {BINDINGS.map((b) => <option key={b} value={b}>{b}</option>)}
          </select>
        </div>
        <div className="form-group form-check">
          <label><input type="checkbox" checked={draft.retainState} disabled={busy}
            onChange={(e) => setDraft({ ...draft, retainState: e.target.checked })} /> {t('Retain state')}</label>
        </div>
      </div>
      <div className="form-group">
        <label>{t('State fields (comma-separated)')}</label>
        <input type="text" value={draft.stateFields.join(', ')} disabled={busy}
          onChange={(e) => setDraft({ ...draft, stateFields: e.target.value.split(',').map((f) => f.trim()) })} />
        <span className="text-muted">{t('MissionDiff surface: runs when a Worker stage hands off; which mission fields to send (title, persona, diff, output_tail, changed_paths, failure_reason).')}</span>
      </div>
      <div className="form-group">
        <label>{t('Vessels (comma-separated names or ids; empty means every vessel)')}</label>
        <input type="text" value={(draft.vessels ?? []).join(', ')} disabled={busy || draft.surface !== 'MissionDiff'}
          onChange={(e) => setDraft({ ...draft, vessels: e.target.value.split(',').map((v) => v.trim()).filter(Boolean) })} />
      </div>

      <h5>{t('Questions')}</h5>
      {draft.questions.map((q, i) => <div key={i} className="custom-decision-question">
        <div className="custom-decision-row">
          <div className="form-group"><label>{t('Id')}</label>
            <input type="text" value={q.id} disabled={busy} onChange={(e) => setQuestion(i, { id: e.target.value })} /></div>
          <div className="form-group"><label>{t('Type')}</label>
            <select value={q.type} disabled={busy} onChange={(e) => setQuestion(i, { type: e.target.value as CustomTypedQuestion['type'] })}>
              {QUESTION_TYPES.map((qt) => <option key={qt} value={qt}>{qt}</option>)}
            </select></div>
          <button type="button" className="btn btn-danger btn-sm" disabled={busy || draft.questions.length <= 1} onClick={() => removeQuestion(i)}>{t('Remove')}</button>
        </div>
        <div className="form-group"><label>{t('Instructions')}</label>
          <textarea value={q.instructions} disabled={busy} rows={2} onChange={(e) => setQuestion(i, { instructions: e.target.value })} /></div>
        {q.type === 'choice' && <div className="form-group"><label>{t('Options (one per line, name: meaning)')}</label>
          <textarea value={optionsToText(q.options)} disabled={busy} rows={3} onChange={(e) => setQuestion(i, { options: textToOptions(e.target.value) })} /></div>}
        {q.type === 'choice' && <div className="form-group"><label>{t('Finding options (comma-separated; the choice flags only when one of these is picked)')}</label>
          <input type="text" value={(q.flagOptions ?? []).join(', ')} disabled={busy}
            onChange={(e) => setQuestion(i, { flagOptions: e.target.value.split(',').map((o) => o.trim()).filter(Boolean) })} /></div>}
        {q.type === 'score' && <div className="form-group"><label>{t('Levels (one per line, lowest first)')}</label>
          <textarea value={(q.levels ?? []).join('\n')} disabled={busy} rows={3} onChange={(e) => setQuestion(i, { levels: e.target.value.split('\n').map((l) => l.trim()).filter(Boolean) })} /></div>}
        {q.type === 'noul' && <div className="custom-decision-row">
          <div className="form-group"><label>{t('True meaning')}</label>
            <input type="text" value={q.trueMeaning ?? ''} disabled={busy} onChange={(e) => setQuestion(i, { trueMeaning: e.target.value })} /></div>
          <div className="form-group"><label>{t('False meaning')}</label>
            <input type="text" value={q.falseMeaning ?? ''} disabled={busy} onChange={(e) => setQuestion(i, { falseMeaning: e.target.value })} /></div>
        </div>}
      </div>)}
      <div className="typed-decisions-actions">
        <button type="button" className="btn btn-secondary btn-sm" disabled={busy} onClick={addQuestion}>{t('Add question')}</button>
      </div>

      <div className="typed-decisions-actions">
        <button type="button" className="btn btn-primary" disabled={busy} onClick={save}>{busy ? t('Saving...') : t('Save decision')}</button>
        <button type="button" className="btn btn-secondary" disabled={busy} onClick={cancel}>{t('Cancel')}</button>
      </div>
    </div>}
  </section>;
}
