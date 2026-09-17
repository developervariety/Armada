import { useCallback, useEffect, useState, type FormEvent } from 'react';
import {
  getTypedDecisions, removeTypedDecisionKey, saveTypedDecisionKey, updateTypedDecisions,
} from '../api/client';
import PageHeader from '../components/shared/PageHeader';
import RefreshButton from '../components/shared/RefreshButton';
import { useLocale } from '../context/LocaleContext';
import type { TypedDecisionMode, TypedDecisionStatus, TypedDecisionsUpdate } from '../types/models';

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
    </>}
    {message && <p role="status">{message}</p>}
    {error && <p role="alert" className="text-danger">{error}</p>}
  </div>;
}
