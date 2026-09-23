import { useMemo } from 'react';
import { useLocale } from '../context/LocaleContext';
import { accountTemplate } from '../lib/subscriptionAccounts';
import { modelAvailability, modelOptions, type DeadPersonaModelEntry } from '../lib/smartRouting';
import type { Captain } from '../types/models';
import PersonaModelsEditor from './routing/PersonaModelsEditor';
import PersonaRestrictionsEditor from './routing/PersonaRestrictionsEditor';
import SmartRoutingPreview from './routing/SmartRoutingPreview';

export const emptyUsageRouting = {
  enabled: false, requireAccountLogin: false, monthlyBudget: 0, currency: 'USD', accounts: [], personaRoutes: {}, personaModels: {},
};

interface Props {
  value: string;
  onChange: (value: string) => void;
  statuses: Array<Record<string, unknown>>;
  /** Persona names from the personas catalogue. */
  personas?: string[];
  captains?: Captain[];
  deadEntries?: DeadPersonaModelEntry[];
}

const NO_CAPTAINS: Captain[] = [];
const NO_NAMES: string[] = [];

/**
 * The Smart Routing policy editor. The routing mode switch, persona model lists, persona restrictions, and the
 * Advanced JSON editor all edit the same draft policy text, so every view stays in sync. Preview never saves.
 */
export default function UsageRoutingEditor({ value, onChange, statuses, personas = NO_NAMES, captains = NO_CAPTAINS, deadEntries = [] }: Props) {
  const { t } = useLocale();
  let policy: Record<string, unknown> | null = null;
  try {
    const parsed: unknown = JSON.parse(value);
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) policy = parsed as Record<string, unknown>;
  } catch { /* The editor keeps invalid draft text until corrected. */ }
  const update = (changes: Record<string, unknown>) => {
    if (policy) onChange(JSON.stringify({ ...policy, ...changes }, null, 2));
  };
  const options = useMemo(() => modelOptions(captains), [captains]);
  const availability = modelAvailability(options, captains, policy, statuses);
  const addAccount = () => {
    const accounts = Array.isArray(policy?.accounts) ? policy.accounts : [];
    update({ accounts: [...accounts, accountTemplate({ id: `account-${accounts.length + 1}` })] });
  };
  const cost = (Array.isArray(policy?.accounts) ? policy.accounts : [])
    .reduce((sum: number, a: Record<string, unknown> | null) => sum + Number(a?.monthlyCost || 0), 0);
  const smart = Boolean(policy?.enabled);
  return <section className="settings-section" style={{ marginTop: '1.5rem' }}>
    <h3>{t('Smart Routing')}</h3>
    <fieldset className="routing-mode" disabled={!policy}>
      <legend>{t('Routing mode')}</legend>
      <label className="routing-mode-option">
        <input type="radio" name="routing-mode" aria-label={t('Legacy Routing')} checked={!smart} onChange={() => update({ enabled: false })} />
        <span><strong>{t('Legacy Routing')}</strong><br />
          <span className="text-muted">{t('Works without a Jev API key. Persona locks, minimum tiers, and Legacy Routing order pick the captain.')}</span></span>
      </label>
      <label className="routing-mode-option">
        <input type="radio" name="routing-mode" aria-label={t('Smart Routing')} checked={smart} onChange={() => update({ enabled: true })} />
        <span><strong>{t('Smart Routing')}</strong><br />
          <span className="text-muted">{t('Adds account usage and persona model lists. Jev selects a list when available; without Jev, Default goes first.')}</span></span>
      </label>
    </fieldset>
    <div className="settings-grid">
      <div className="form-group"><label htmlFor="usage-budget">{t('Monthly budget (informational)')}</label>
        <input id="usage-budget" type="number" min={0} value={Number(policy?.monthlyBudget || 0)} disabled={!policy}
          onChange={e => update({ monthlyBudget: Number(e.target.value) })} /></div>
      <div className="form-group"><label htmlFor="usage-currency">{t('Currency')}</label>
        <input id="usage-currency" value={String(policy?.currency || 'USD')} disabled={!policy} onChange={e => update({ currency: e.target.value })} /></div>
    </div>
    <p>{t('Configured monthly costs')}: {String(policy?.currency || 'USD')} {cost.toFixed(2)}
      {Number(policy?.monthlyBudget) > 0 && cost > Number(policy?.monthlyBudget) && <strong> — {t('Above budget')}</strong>}</p>
    <PersonaModelsEditor policy={policy} personas={personas} options={options} availability={availability}
      deadEntries={deadEntries}
      onChange={personaModels => update({ personaModels })} />
    <PersonaRestrictionsEditor policy={policy} personas={personas} captains={captains} options={options} availability={availability}
      onChange={personaRoutes => update({ personaRoutes })} />
    <details className="usage-policy-advanced"><summary>{t('Advanced: edit the account and persona policy as JSON')}</summary>
    <details><summary>{t('Policy fields and data sources')}</summary>
      <p>{t('Accounts map captainIds to one shared allowance. collector supports Manual, File, Codex, Claude, Cursor, and OpenCodeGo. Codex queries the server user’s existing login without starting a task. Claude uses OAuth credentials, Cursor uses its account API key from launchCredentialEnv or launchCredentialFile, or a legacy cookie header from credentialEnv or credentialFilePath. OpenCodeGo uses an API key. Enter only credential references, never secrets.')}</p>
      <p>{t('To give captains their own login, set runtime (ClaudeCode, Codex, OpenCode, or Cursor) and homeDirectory, an absolute login home the owner signed in to. Cursor uses launchCredentialFile, the key file in its account folder, or launchCredentialEnv, the name of a server variable holding its API key, instead of a home. The Subscription accounts section above creates these for you. Every listed captain must use that runtime. A missing login blocks the account with a named reason. A quota, billing, or authentication failure on one captain holds the whole account Exhausted. Adding another subscription account needs an owner decision under the provider terms.')}</p>
      <p>{t('Set reserveRemainingPercent ≤ lowRemainingPercent < recoveryRemainingPercent. reservedPersonas and reservedPriorityAtOrAbove can use the reserve; lower priority numbers mean more important work. Exhausted accounts block all work. unknownUsagePolicy is Allow, Conserve, or Block.')}</p>
      <p>{t('personaModels maps a persona to default, lighter, and stronger model lists; default must not be empty. personaRoutes is optional: it restricts a persona to the listed accounts, and to the listed models when models is not empty. Captain IDs are available on the Captains page.')}</p>
      <p>{t('windowModels maps exact usage window names to model IDs. Map Cursor pools and model-specific Claude windows before enabling. Unmapped windows apply to every model conservatively.')}</p>
      <p>{t('A manualSnapshot has observedUtc, source, and windows: [{name, remainingPercent, resetsUtc, models}]. Use UTC timestamps ending in Z. Missing, stale, or expired windows are unknown. overrideState needs overrideUntilUtc. Do not enter credentials.')}</p>
    </details>
    <div className="form-group"><label htmlFor="usage-policy">{t('Account and persona policy (JSON)')}</label>
      <textarea id="usage-policy" className="mono" rows={18} value={value} spellCheck={false}
        onChange={e => onChange(e.target.value)} /></div>
    <button type="button" className="btn btn-secondary" onClick={addAccount} disabled={!policy}>{t('Add account template')}</button>
    </details>
    <h4>{t('Saved account usage')}</h4>
    {statuses.length === 0 ? <p className="text-muted">{t('No usage accounts configured.')}</p> :
      <div className="table-wrap"><table className="data-table"><thead><tr>{['Account', 'Runtime', 'State', 'Observed', 'Source', 'Details'].map(x => <th key={x}>{t(x)}</th>)}</tr></thead>
        <tbody>{statuses.map(s => <tr key={String(s.accountId)}>
          <td>{String(s.accountId)}</td><td>{s.runtime ? String(s.runtime) : t('Shared login')}</td>
          <td>{String(s.state)}{s.exhaustedUntilUtc ? ` — ${t('until')} ${new Date(String(s.exhaustedUntilUtc)).toLocaleString()}` : ''}</td><td>{s.observedUtc ? new Date(String(s.observedUtc)).toLocaleString() : t('Unknown')}</td>
          <td>{String(s.source)}</td><td>{String(s.reason)}{s.collectionError ? ` — ${String(s.collectionError)}` : ''}
            <details><summary>{t('Usage windows')}</summary><pre>{JSON.stringify(s.windows, null, 2)}</pre></details></td>
        </tr>)}</tbody></table></div>}
    <SmartRoutingPreview value={value} personas={personas} captains={captains} />
  </section>;
}
