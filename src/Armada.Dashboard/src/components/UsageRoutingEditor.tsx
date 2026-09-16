import { useState } from 'react';
import { previewUsageRouting } from '../api/client';
import { useLocale } from '../context/LocaleContext';

export const emptyUsageRouting = {
  enabled: false, monthlyBudget: 0, currency: 'USD', accounts: [], personaRoutes: {},
};

interface Props {
  value: string;
  onChange: (value: string) => void;
  statuses: Array<Record<string, unknown>>;
}

/** Account policy uses the same JSON contract as settings.json; preview never saves. */
export default function UsageRoutingEditor({ value, onChange, statuses }: Props) {
  const { t } = useLocale();
  const [persona, setPersona] = useState('Worker');
  const [priority, setPriority] = useState(100);
  const [model, setModel] = useState('');
  const [preview, setPreview] = useState<Record<string, unknown> | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  let policy: Record<string, unknown> | null = null;
  try { policy = JSON.parse(value); } catch { /* The editor keeps invalid draft text until corrected. */ }
  const update = (changes: Record<string, unknown>) => {
    if (policy) onChange(JSON.stringify({ ...policy, ...changes }, null, 2));
    setPreview(null);
  };
  const runPreview = async () => {
    setBusy(true); setError(''); setPreview(null);
    try {
      const usageRouting: unknown = JSON.parse(value);
      setPreview(await previewUsageRouting({ persona, priority, preferredModel: model || null, usageRouting }));
    } catch (e) { setError(e instanceof Error ? e.message : t('Preview failed')); }
    finally { setBusy(false); }
  };
  const addAccount = () => {
    const accounts = Array.isArray(policy?.accounts) ? policy.accounts : [];
    update({ accounts: [...accounts, {
      id: `account-${accounts.length + 1}`, captainIds: [], collector: 'Manual', credentialEnv: null, credentialFilePath: null,
      runtime: null, homeDirectory: null, launchCredentialEnv: null, windowModels: {}, monthlyCost: 0,
      lowRemainingPercent: 25, reserveRemainingPercent: 10, recoveryRemainingPercent: 35,
      resetGraceMinutes: 0, maxAgeMinutes: 15, unknownUsagePolicy: 'Allow',
      maxConcurrentMissions: 0, reservedPersonas: [], reservedPriorityAtOrAbove: null,
      manualSnapshot: null, usageFilePath: null, overrideState: null, overrideUntilUtc: null,
    }] });
  };
  const cost = (Array.isArray(policy?.accounts) ? policy.accounts : [])
    .reduce((sum: number, a: Record<string, unknown> | null) => sum + Number(a?.monthlyCost || 0), 0);
  return <section className="settings-section" style={{ marginTop: '1.5rem' }}>
    <h3>{t('Smart Routing and usage conservation')}</h3>
    <p className="text-muted">{t('When enabled, V2 replaces legacy preference overrides. Use preferred routes normally. Move routine work only when allowance is low. Reserve capacity for selected personas or priorities. This does not change running missions or enforce a billing cap.')}</p>
    <div className="settings-grid">
      <label className="settings-checkbox-label"><input type="checkbox" checked={Boolean(policy?.enabled)} disabled={!policy}
        onChange={e => update({ enabled: e.target.checked })} />{t('Enable Smart Routing')}</label>
      <div className="form-group"><label htmlFor="usage-budget">{t('Monthly budget (informational)')}</label>
        <input id="usage-budget" type="number" min={0} value={Number(policy?.monthlyBudget || 0)} disabled={!policy}
          onChange={e => update({ monthlyBudget: Number(e.target.value) })} /></div>
      <div className="form-group"><label htmlFor="usage-currency">{t('Currency')}</label>
        <input id="usage-currency" value={String(policy?.currency || 'USD')} disabled={!policy} onChange={e => update({ currency: e.target.value })} /></div>
    </div>
    <p>{t('Configured monthly costs')}: {String(policy?.currency || 'USD')} {cost.toFixed(2)}
      {Number(policy?.monthlyBudget) > 0 && cost > Number(policy?.monthlyBudget) && <strong> — {t('Above budget')}</strong>}</p>
    <details><summary>{t('Policy fields and data sources')}</summary>
      <p>{t('Accounts map captainIds to one shared allowance. collector supports Manual, File, Codex, Claude, Cursor, and OpenCodeGo. Codex queries the server user’s existing login without starting a task. Claude uses OAuth credentials, Cursor uses a cookie header, and OpenCodeGo uses an API key. Set credentialEnv or credentialFilePath; enter only the reference, never the secret.')}</p>
      <p>{t('To give captains their own login, set runtime (ClaudeCode, Codex, OpenCode, or Cursor) and homeDirectory, an absolute login home the owner signed in to. Cursor uses launchCredentialEnv, the name of a server variable holding its API key, instead of a home. Every listed captain must use that runtime. A missing login blocks the account with a named reason. A quota, billing, or authentication failure on one captain holds the whole account Exhausted. Adding a second subscription account needs an owner decision under the provider terms.')}</p>
      <p>{t('Set reserveRemainingPercent ≤ lowRemainingPercent < recoveryRemainingPercent. reservedPersonas and reservedPriorityAtOrAbove can use the reserve; lower priority numbers mean more important work. Exhausted accounts block all work. unknownUsagePolicy is Allow, Conserve, or Block.')}</p>
      <p>{t('personaRoutes maps each persona to an ordered list of {accountId, models}. An empty models list accepts all eligible models on that account. Unlisted routes are not used for that persona. A missing persona route waits unless a * default route exists. Captain IDs are available on the Captains page.')}</p>
      <p>{t('windowModels maps exact usage window names to model IDs. Map Cursor pools and model-specific Claude windows before enabling. Unmapped windows apply to every model conservatively.')}</p>
      <p>{t('A manualSnapshot has observedUtc, source, and windows: [{name, remainingPercent, resetsUtc, models}]. Use UTC timestamps ending in Z. Missing, stale, or expired windows are unknown. overrideState needs overrideUntilUtc. Do not enter credentials.')}</p>
    </details>
    <div className="form-group"><label htmlFor="usage-policy">{t('Account and persona policy (JSON)')}</label>
      <textarea id="usage-policy" className="mono" rows={18} value={value} spellCheck={false}
        onChange={e => { onChange(e.target.value); setPreview(null); }} /></div>
    <button type="button" className="btn btn-secondary" onClick={addAccount} disabled={!policy}>{t('Add account template')}</button>
    <h4>{t('Saved account usage')}</h4>
    {statuses.length === 0 ? <p className="text-muted">{t('No usage accounts configured.')}</p> :
      <table className="data-table"><thead><tr>{['Account', 'Runtime', 'State', 'Observed', 'Source', 'Details'].map(x => <th key={x}>{t(x)}</th>)}</tr></thead>
        <tbody>{statuses.map(s => <tr key={String(s.accountId)}>
          <td>{String(s.accountId)}</td><td>{s.runtime ? String(s.runtime) : t('Shared login')}</td>
          <td>{String(s.state)}{s.exhaustedUntilUtc ? ` — ${t('until')} ${new Date(String(s.exhaustedUntilUtc)).toLocaleString()}` : ''}</td><td>{s.observedUtc ? new Date(String(s.observedUtc)).toLocaleString() : t('Unknown')}</td>
          <td>{String(s.source)}</td><td>{String(s.reason)}{s.collectionError ? ` — ${String(s.collectionError)}` : ''}
            <details><summary>{t('Usage windows')}</summary><pre>{JSON.stringify(s.windows, null, 2)}</pre></details></td>
        </tr>)}</tbody></table>}
    <h4>{t('Preview draft usage admission')}</h4>
    <p className="text-muted">{t('Preview uses saved tier membership and current captain state. It does not reserve capacity, save the draft, or launch work. Other dispatch gates still apply.')}</p>
    <div className="settings-grid">
      <div className="form-group"><label htmlFor="usage-persona">{t('Persona')}</label><input id="usage-persona" value={persona} onChange={e => { setPersona(e.target.value); setPreview(null); }} /></div>
      <div className="form-group"><label htmlFor="usage-priority">{t('Priority')}</label><input id="usage-priority" type="number" value={priority} onChange={e => { setPriority(Number(e.target.value)); setPreview(null); }} /></div>
      <div className="form-group"><label htmlFor="usage-model">{t('Required tier or model (optional)')}</label><input id="usage-model" value={model} onChange={e => { setModel(e.target.value); setPreview(null); }} /></div>
    </div>
    <button type="button" className="btn btn-secondary" onClick={runPreview} disabled={busy || !policy}>{busy ? t('Checking usage…') : t('Preview Smart Routing')}</button>
    {error && <p role="alert" className="text-danger">{error}</p>}
    {preview && <pre role="status" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{JSON.stringify(preview, null, 2)}</pre>}
  </section>;
}
