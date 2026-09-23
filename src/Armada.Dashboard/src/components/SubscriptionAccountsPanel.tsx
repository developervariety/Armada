import { useCallback, useEffect, useRef, useState } from 'react';
import {
  cancelAccountLogin,
  createAccountHome,
  createCaptain,
  deleteUsageAccount,
  getAccountLoginStatus,
  refreshUsageAccount,
  startAccountLogin,
  submitAccountLoginCode,
  submitAccountLoginKey,
  listAllCaptains,
} from '../api/client';
import { copyToClipboard } from './shared/CopyButton';
import { useLocale } from '../context/LocaleContext';
import type {
  AccountLoginSession, AccountLoginStatus, AccountRuntime, Captain, UsageAccountDeleteResult, UsageAccountRefreshResult,
} from '../types/models';
import {
  ACCOUNT_RUNTIMES, RUNTIME_LABELS, SAFE_ACCOUNT_ID, cloneCaptainName, policyAccounts, runtimeAccount, slugifyAccountId,
  withAccount, withAccountCaptains,
} from '../lib/subscriptionAccounts';
import type { AccountRecord, PolicyRecord } from '../lib/subscriptionAccounts';

type StatusRecord = Record<string, unknown>;

interface Props {
  /** The SAVED usage routing policy; logins act only on saved accounts. */
  savedPolicy: PolicyRecord | null;
  statuses: StatusRecord[];
  disabled: boolean;
  /** Apply a change to the saved policy and save it through the settings save path. */
  onSavePolicy: (mutate: (policy: PolicyRecord) => PolicyRecord) => Promise<void>;
  onRefresh: () => void;
}

const POLL_MS = 3000;

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

/** A positive whole number from a policy field, or the server default when the field is absent. */
function policyMinutes(value: unknown, fallback: number): number {
  const n = Number(value);
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

function formatUtc(value: unknown): string {
  if (typeof value !== 'string' || !value) return '';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString();
}

function isRuntime(value: unknown): value is AccountRuntime {
  return typeof value === 'string' && (ACCOUNT_RUNTIMES as string[]).includes(value);
}

function accountHasLogin(account: AccountRecord): boolean {
  return Boolean(account.homeDirectory || account.launchCredentialEnv || account.launchCredentialFile);
}

function loginRefusalReason(captain: Captain, accounts: AccountRecord[], statuses: StatusRecord[]): string | null {
  if (!isRuntime(captain.runtime) || captain.apiKey || captain.apiBaseUrl) return null;
  const account = accounts.find(item => Array.isArray(item.captainIds) && (item.captainIds as string[]).includes(captain.id));
  if (!account || !accountHasLogin(account)) return 'account_required';
  const reason = String(statuses.find(item => String(item.accountId) === String(account.id))?.reason || '');
  return reason.startsWith('account_') && reason !== 'account_provider_failure' ? reason : null;
}

/**
 * Guided subscription accounts: add any number of accounts per runtime, log each in from the browser, and
 * assign captains. Account folders are derived by the server; keys are sent once and never shown again.
 */
export default function SubscriptionAccountsPanel({ savedPolicy, statuses, disabled, onSavePolicy, onRefresh }: Props) {
  const { t } = useLocale();
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [runtime, setRuntime] = useState<AccountRuntime>('Codex');
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [open, setOpen] = useState<string | null>(null);
  const [notice, setNotice] = useState('');

  const accounts = policyAccounts(savedPolicy);
  const runtimeAccounts = accounts.filter(a => isRuntime(a.runtime));
  const ids = accounts.map(a => String(a.id));
  const proposedId = name.trim() ? slugifyAccountId(name, ids) : '';
  const refreshMinutes = policyMinutes(savedPolicy?.refreshIntervalMinutes, 5);
  const maxAges = runtimeAccounts.map(a => policyMinutes(a.maxAgeMinutes, 15));
  const minAge = maxAges.length ? Math.min(...maxAges) : 15;
  const maxAge = maxAges.length ? Math.max(...maxAges) : 15;
  const maxAgeText = minAge === maxAge ? String(minAge) : `${minAge}-${maxAge}`;

  const accountDeleted = (result: UsageAccountDeleteResult) => {
    setOpen(null);
    const folder = result.homeReason === 'account_home_not_managed'
      ? t('Its configured home folder is not the server-derived folder, so it was left in place.')
      : result.homeDeleted ? t('Its login files were removed.') : t('No login folder was found.');
    setNotice(`${t('Deleted account {{id}}; {{routes}} persona routes removed.', { id: result.accountId, routes: result.routesRemoved })} ${folder}`);
    onRefresh();
  };

  const loadCaptains = useCallback(async () => {
    try {
      const result = await listAllCaptains();
      setCaptains(result);
    } catch {
      // Captain assignment stays empty; the account list and logins still work.
    }
  }, []);

  useEffect(() => { void loadCaptains(); }, [loadCaptains]);

  const addAccount = async () => {
    if (!proposedId || !SAFE_ACCOUNT_ID.test(proposedId)) return;
    setBusy(true); setError('');
    try {
      const home = await createAccountHome(proposedId);
      await onSavePolicy(policy => withAccount(policy, runtimeAccount(runtime, home)));
      setName('');
      setOpen(proposedId);
    } catch (e) { setError(errorText(e)); }
    finally { setBusy(false); }
  };

  return <section className="settings-section subscription-accounts" style={{ marginTop: '1.5rem' }}>
    <h3>{t('Subscription accounts')}</h3>
    <p className="text-muted account-refresh-policy">{t('Usage refreshes every {{refresh}} min when read; data older than {{maxAge}} min counts as Unknown.', { refresh: refreshMinutes, maxAge: maxAgeText })}</p>
    <p className="text-muted">{t('Each account is its own provider login with its own allowance. Add as many accounts per runtime as you need, log each one in here, then assign captains. Confirm that each additional subscription is permitted under the provider terms before you use it.')}</p>
    <label className="routing-mode-option">
      <input type="checkbox" checked={Boolean(savedPolicy?.requireAccountLogin)} disabled={disabled || !savedPolicy}
        aria-label={t('Require account login')}
        onChange={() => { if (savedPolicy) void onSavePolicy(policy => ({ ...policy, requireAccountLogin: !policy.requireAccountLogin })); }} />
      <span><strong>{t('Require account login')}</strong><br />
        <span className="text-muted">{t('When on, Claude Code, Codex, OpenCode, and Cursor captains with no account login cannot launch, chat, plan, or refine. Off by default.')}</span></span>
    </label>

    {(() => {
      const refusals = captains.map(captain => {
        const reason = loginRefusalReason(captain, runtimeAccounts, statuses);
        return reason ? { captain, reason } : null;
      }).filter((item): item is { captain: Captain; reason: string } => item !== null);
      return refusals.length === 0 ? null : <div className="account-login-refusals">
        <h4>{savedPolicy?.requireAccountLogin
          ? t('Captains refused with no account login')
          : t('Captains that would be refused if account login is required')}</h4>
        <ul>{refusals.map(({ captain, reason }) => <li key={captain.id}>
          {captain.name || captain.id} — <span className="mono">{reason}</span>
        </li>)}</ul>
      </div>;
    })()}

    {runtimeAccounts.length === 0
      ? <p className="text-muted">{t('No subscription accounts yet.')}</p>
      : <ul className="account-list">{runtimeAccounts.map(account => <AccountCard key={String(account.id)} account={account}
          status={statuses.find(s => s.accountId === account.id)} accounts={accounts} captains={captains} disabled={disabled}
          expanded={open === account.id} onToggle={() => setOpen(open === account.id ? null : String(account.id))}
          onSavePolicy={onSavePolicy} onRefresh={onRefresh} onCaptainsChanged={loadCaptains} onDeleted={accountDeleted} />)}</ul>}
    {notice && <p role="status">{notice}</p>}

    <fieldset className="account-add" disabled={disabled || busy}>
      <legend>{t('Add account')}</legend>
      <div className="account-add-row">
        <div className="form-group">
          <label htmlFor="account-runtime">{t('Runtime')}</label>
          <select id="account-runtime" value={runtime} onChange={e => setRuntime(e.target.value as AccountRuntime)}>
            {ACCOUNT_RUNTIMES.map(r => <option key={r} value={r}>{RUNTIME_LABELS[r]}</option>)}
          </select>
        </div>
        <div className="form-group">
          <label htmlFor="account-name">{t('Account name')}</label>
          <input id="account-name" value={name} maxLength={80} placeholder={t('for example codex-work-2')} onChange={e => setName(e.target.value)} />
        </div>
      </div>
      {proposedId && <p className="text-muted">{t('Account ID')}: <code>{proposedId}</code></p>}
      <button type="button" className="btn btn-primary" onClick={addAccount} disabled={!proposedId}>{busy ? t('Adding…') : t('Add account')}</button>
    </fieldset>
    {error && <p role="alert" className="text-danger">{error}</p>}
  </section>;
}

interface CardProps {
  account: AccountRecord;
  status: StatusRecord | undefined;
  accounts: AccountRecord[];
  captains: Captain[];
  disabled: boolean;
  expanded: boolean;
  onToggle: () => void;
  onSavePolicy: Props['onSavePolicy'];
  onRefresh: () => void;
  onCaptainsChanged: () => Promise<void>;
  onDeleted: (result: UsageAccountDeleteResult) => void;
}

function AccountCard({ account, status, accounts, captains, disabled, expanded, onToggle, onSavePolicy, onRefresh, onCaptainsChanged, onDeleted }: CardProps) {
  const { t } = useLocale();
  const id = String(account.id);
  const runtime = account.runtime as AccountRuntime;
  const captainIds = Array.isArray(account.captainIds) ? (account.captainIds as string[]) : [];
  const [refreshing, setRefreshing] = useState(false);
  const [refreshed, setRefreshed] = useState<UsageAccountRefreshResult | null>(null);
  const [refreshError, setRefreshError] = useState('');

  // A settings reload brings the server's newer status, which then replaces the hard refresh result.
  useEffect(() => { setRefreshed(null); }, [status]);

  const refreshUsage = async () => {
    setRefreshing(true); setRefreshError('');
    try {
      const result = await refreshUsageAccount(id);
      setRefreshed(result);
    } catch (e) { setRefreshError(errorText(e)); }
    finally { setRefreshing(false); }
  };

  const shown: StatusRecord | undefined = refreshed ? (refreshed.status as unknown as StatusRecord) : status;
  const observed = formatUtc(shown?.observedUtc);
  const reason = shown?.reason ? String(shown.reason) : '';
  const loginProblem = reason.startsWith('account_') && reason !== 'account_provider_failure' ? reason : '';
  const loginLabel = !shown ? t('Not checked yet') : loginProblem ? `${t('Not logged in')} (${loginProblem})` : t('Logged in');

  return <li className="account-card">
    <div className="account-card-head">
      <div className="account-card-title">
        <strong>{id}</strong>
        <span className="text-muted"> · {RUNTIME_LABELS[runtime]}</span>
      </div>
      <button type="button" className="btn btn-secondary" aria-expanded={expanded} onClick={onToggle}>{expanded ? t('Close') : t('Manage')}</button>
    </div>
    <dl className="account-card-facts">
      <div><dt>{t('Login')}</dt><dd className={loginProblem ? 'text-danger' : undefined}>{loginLabel}</dd></div>
      <div><dt>{t('Captains')}</dt><dd>{captainIds.length}</dd></div>
      <div><dt>{t('Usage')}</dt><dd>{shown ? String(shown.state) : t('Unknown')}</dd></div>
      <div><dt>{t('Observed')}</dt><dd>{observed || t('Never')}</dd></div>
    </dl>
    {runtime === 'Cursor' && <p className="text-muted">{t('Cursor usage cannot be measured with an API key. Unknown still routes under unknownUsagePolicy Allow.')}</p>}
    <div className="account-inline account-refresh">
      <button type="button" className="btn btn-secondary" disabled={disabled || refreshing} aria-busy={refreshing}
        onClick={() => void refreshUsage()}>
        {refreshing ? <><span className="btn-spinner" aria-hidden="true" /> {t('Refreshing…')}</> : t('Refresh usage')}
      </button>
      {refreshed?.reason === 'usage_refresh_rate_limited' && <span className="text-muted" role="status">
        {t('The provider asked to wait; usage was not read. Next read after {{time}}.', { time: formatUtc(refreshed.retryAfterUtc) })}</span>}
      {refreshed && refreshed.reason !== 'usage_refresh_rate_limited' && <span className="text-muted" role="status">
        {refreshed.collected ? t('Usage read just now.') : `${t('Usage not read')} (${refreshed.reason})`}</span>}
      {refreshError && <span role="alert" className="text-danger">{refreshError}</span>}
    </div>
    {expanded && <>
      <LoginSection accountId={id} runtime={runtime} disabled={disabled} onRefresh={onRefresh}
        loginReason={typeof shown?.loginReason === 'string' ? shown.loginReason : loginProblem} />
      <CaptainSection account={account} accounts={accounts} captains={captains} disabled={disabled}
        onSavePolicy={onSavePolicy} onCaptainsChanged={onCaptainsChanged} />
      <DeleteSection accountId={id} captainCount={captainIds.length} disabled={disabled} onDeleted={onDeleted} />
    </>}
  </li>;
}

interface LoginProps {
  accountId: string;
  runtime: AccountRuntime;
  disabled: boolean;
  onRefresh: () => void;
  loginReason?: string;
}

function LoginSection({ accountId, runtime, disabled, onRefresh, loginReason }: LoginProps) {
  const { t } = useLocale();
  const [status, setStatus] = useState<AccountLoginStatus | null>(null);
  const [session, setSession] = useState<AccountLoginSession | null>(null);
  const [key, setKey] = useState('');
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [copied, setCopied] = useState('');
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const mounted = useRef(true);
  const sessionRef = useRef<AccountLoginSession | null>(null);

  // A login that reaches Succeeded reloads settings, so the account's login state and usage update.
  const applySession = useCallback((next: AccountLoginSession | null) => {
    const wasPending = sessionRef.current?.state === 'Pending' && sessionRef.current.sessionId === next?.sessionId;
    sessionRef.current = next;
    setSession(next);
    if (wasPending && next?.state === 'Succeeded') onRefresh();
  }, [onRefresh]);

  const refresh = useCallback(async () => {
    try {
      const next = await getAccountLoginStatus(accountId);
      if (!mounted.current) return;
      setStatus(next);
      applySession(next.session);
    } catch (e) { if (mounted.current) setError(errorText(e)); }
  }, [accountId, applySession]);

  useEffect(() => {
    mounted.current = true;
    void refresh();
    return () => { mounted.current = false; if (timer.current) clearTimeout(timer.current); };
  }, [refresh]);

  // Poll only while a login is pending.
  useEffect(() => {
    if (timer.current) clearTimeout(timer.current);
    if (session?.state === 'Pending') timer.current = setTimeout(() => { void refresh(); }, POLL_MS);
  }, [session, refresh]);

  const act = async (action: () => Promise<AccountLoginSession | AccountLoginStatus>) => {
    setBusy(true); setError('');
    try {
      const result = await action();
      if ('sessionId' in result) applySession(result);
      else { setStatus(result); applySession(result.session); }
    } catch (e) { setError(errorText(e)); }
    finally { setBusy(false); }
  };

  const submitKey = () => {
    const value = key;
    setKey('');
    void act(() => submitAccountLoginKey(accountId, value)).then(() => { onRefresh(); return refresh(); });
  };

  const submitCode = () => {
    const value = code;
    setCode('');
    void act(() => submitAccountLoginCode(accountId, value));
  };

  const copy = (label: string, text: string) => {
    copyToClipboard(text).then(() => { setCopied(label); setTimeout(() => setCopied(''), 2000); }).catch(() => { /* Selecting the text still works. */ });
  };

  const pending = session?.state === 'Pending';
  const usesKey = runtime === 'OpenCode' || runtime === 'Cursor';
  const usesBrowser = runtime !== 'OpenCode';

  const keyForm = <div className="form-group">
    <label htmlFor={`account-key-${accountId}`}>{runtime === 'OpenCode' ? t('OpenCode Go API key') : t('Cursor API key')}</label>
    <div className="account-inline">
      <input id={`account-key-${accountId}`} type="password" autoComplete="off" spellCheck={false} value={key}
        onChange={e => setKey(e.target.value)} disabled={disabled || busy || pending} />
      <button type="button" className="btn btn-primary" onClick={submitKey} disabled={disabled || busy || pending || !key.trim()}>{t('Save key')}</button>
    </div>
    <p className="text-muted">{t('The key is stored only in this account’s folder on the server and is never shown again.')}</p>
  </div>;

  const browserStart = <button type="button" className="btn btn-primary" disabled={disabled || busy || pending}
    onClick={() => void act(() => startAccountLogin(accountId))}>
    {busy && !pending ? t('Starting…') : runtime === 'ClaudeCode' ? t('Start sign-in') : t('Start device login')}
  </button>;

  return <div className="account-login">
    <h4>{t('Log in')}</h4>
    {status && <p>{t('Server login check')}: {status.loginReady === true ? t('ready')
      : status.loginReady === false ? <span className="text-danger">{status.loginReason}</span> : t('not configured')}</p>}
    {(loginReason === 'account_launch_credential_unavailable' || status?.loginReason === 'account_launch_credential_unavailable') &&
      <p className="text-danger">{t('Save a Cursor API key on this card. Captains use the key file, not the browser login.')}</p>}

    {usesKey && keyForm}

    {usesBrowser && (runtime === 'Cursor'
      ? <details><summary>{t('Browser login instead')}</summary>
          <p className="text-muted">{t('Cursor captains on an account launch with its API key. A browser login is stored in the account folder, not used by captains.')}</p>
          {browserStart}
        </details>
      : browserStart)}

    {session && <div className="account-session" aria-live="polite">
      <p>{t('Login')}: <strong>{t(session.state)}</strong>{session.reason ? ` (${session.reason})` : ''}
        {pending && session.expiresUtc ? ` — ${t('expires')} ${new Date(session.expiresUtc).toLocaleTimeString()}` : ''}</p>
      {pending && session.verificationUrl && <div className="account-login-step">
        <span>{runtime === 'ClaudeCode' ? t('1. Open this page and sign in:') : t('1. Open this page:')}</span>
        <a className="account-login-url" href={session.verificationUrl} target="_blank" rel="noreferrer noopener">{session.verificationUrl}</a>
        <button type="button" className="btn btn-secondary" onClick={() => copy('url', session.verificationUrl!)}>{copied === 'url' ? t('Copied') : t('Copy link')}</button>
      </div>}
      {pending && session.userCode && <div className="account-login-step">
        <span>{t('2. Enter this code:')}</span>
        <code className="account-login-code">{session.userCode}</code>
        <button type="button" className="btn btn-secondary" onClick={() => copy('code', session.userCode!)}>{copied === 'code' ? t('Copied') : t('Copy code')}</button>
      </div>}
      {pending && session.method === 'PasteCode' && <div className="form-group">
        <label htmlFor={`account-code-${accountId}`}>{t('2. Paste the code shown after sign-in')}</label>
        <div className="account-inline">
          <input id={`account-code-${accountId}`} type="password" autoComplete="off" spellCheck={false} value={code}
            onChange={e => setCode(e.target.value)} disabled={disabled || busy} />
          <button type="button" className="btn btn-primary" onClick={submitCode} disabled={disabled || busy || !code.trim()}>{t('Submit code')}</button>
        </div>
      </div>}
      {pending && <p className="text-muted">{t('Waiting for you to finish in the browser. This page checks every few seconds.')}</p>}
      {pending && <button type="button" className="btn btn-secondary" disabled={busy}
        onClick={() => void act(() => cancelAccountLogin(accountId))}>{t('Cancel login')}</button>}
    </div>}
    {error && <p role="alert" className="text-danger">{error}</p>}
  </div>;
}

interface CaptainProps {
  account: AccountRecord;
  accounts: AccountRecord[];
  captains: Captain[];
  disabled: boolean;
  onSavePolicy: Props['onSavePolicy'];
  onCaptainsChanged: () => Promise<void>;
}

function CaptainSection({ account, accounts, captains, disabled, onSavePolicy, onCaptainsChanged }: CaptainProps) {
  const { t } = useLocale();
  const id = String(account.id);
  const runtime = String(account.runtime);
  const current = Array.isArray(account.captainIds) ? (account.captainIds as string[]) : [];
  const [selected, setSelected] = useState<string[]>(current);
  const [cloneSource, setCloneSource] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const currentKey = current.join('\n');

  useEffect(() => { setSelected(currentKey ? currentKey.split('\n') : []); }, [currentKey]);

  const owner = (captainId: string) => accounts.find(a => a.id !== id && Array.isArray(a.captainIds) && (a.captainIds as string[]).includes(captainId));
  const eligible = captains.filter(c => c.runtime === runtime);
  const dirty = [...selected].sort().join('\n') !== [...current].sort().join('\n');

  const save = async (captainIds: string[]) => {
    setBusy(true); setError('');
    try { await onSavePolicy(policy => withAccountCaptains(policy, id, captainIds)); }
    catch (e) { setError(errorText(e)); }
    finally { setBusy(false); }
  };

  const cloneCaptain = async () => {
    const source = captains.find(c => c.id === cloneSource);
    if (!source) return;
    setBusy(true); setError('');
    try {
      const created = await createCaptain({
        name: cloneCaptainName(source.name, id, captains.map(c => c.name)),
        runtime: source.runtime,
        model: source.model,
        systemInstructions: source.systemInstructions,
        allowedPersonas: source.allowedPersonas,
        preferredPersona: source.preferredPersona,
        runtimeOptionsJson: source.runtimeOptionsJson,
      });
      await onSavePolicy(policy => withAccountCaptains(policy, id, [...current, created.id]));
      setCloneSource('');
      await onCaptainsChanged();
    } catch (e) { setError(errorText(e)); }
    finally { setBusy(false); }
  };

  return <div className="account-captains">
    <h4>{t('Captains on this account')}</h4>
    {eligible.length === 0 ? <p className="text-muted">{t('No captains use this runtime.')}</p> :
      <ul className="account-captain-list">{eligible.map(c => {
        const other = owner(c.id);
        const ownProvider = Boolean(c.apiKey || c.apiBaseUrl);
        return <li key={c.id}>
          <label className="settings-checkbox-label">
            <input type="checkbox" checked={selected.includes(c.id)} disabled={disabled || busy || Boolean(other) || ownProvider}
              onChange={e => setSelected(e.target.checked ? [...selected, c.id] : selected.filter(x => x !== c.id))} />
            <span>{c.name}{c.model ? ` · ${c.model}` : ''}</span>
            {other && <span className="text-muted"> ({t('on')} {String(other.id)})</span>}
            {ownProvider && <span className="text-muted"> ({t('uses its own provider')})</span>}
          </label>
        </li>;
      })}</ul>}
    <button type="button" className="btn btn-primary" disabled={disabled || busy || !dirty} onClick={() => void save(selected)}>{t('Save captains')}</button>

    {eligible.length > 0 && <div className="form-group" style={{ marginTop: '0.75rem' }}>
      <label htmlFor={`account-clone-${id}`}>{t('Clone a captain onto this account')}</label>
      <div className="account-inline">
        <select id={`account-clone-${id}`} value={cloneSource} onChange={e => setCloneSource(e.target.value)} disabled={disabled || busy}>
          <option value="">{t('Choose a captain')}</option>
          {eligible.filter(c => !c.apiKey && !c.apiBaseUrl).map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
        <button type="button" className="btn btn-secondary" disabled={disabled || busy || !cloneSource} onClick={() => void cloneCaptain()}>{t('Clone')}</button>
      </div>
      <p className="text-muted">{t('Creates a new captain with the same runtime, model, and personas, and assigns it here.')}</p>
    </div>}
    {error && <p role="alert" className="text-danger">{error}</p>}
  </div>;
}

interface DeleteProps {
  accountId: string;
  captainCount: number;
  disabled: boolean;
  onDeleted: (result: UsageAccountDeleteResult) => void;
}

/**
 * Delete an account after a confirm step. Refused while captains are assigned: a captain left on a deleted account would
 * silently launch with the shared login instead.
 */
function DeleteSection({ accountId, captainCount, disabled, onDeleted }: DeleteProps) {
  const { t } = useLocale();
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const blocked = captainCount > 0;

  const remove = async () => {
    setBusy(true); setError('');
    try {
      const result = await deleteUsageAccount(accountId);
      setConfirming(false);
      onDeleted(result);
    } catch (e) { setError(errorText(e)); }
    finally { setBusy(false); }
  };

  return <div className="account-delete">
    <h4>{t('Delete account')}</h4>
    {blocked && <p className="text-muted">{t('Unassign this account’s {{count}} captains before deleting it, so no captain silently falls back to the shared login.', { count: captainCount })}</p>}
    {!confirming
      ? <button type="button" className="btn btn-danger" disabled={disabled || busy || blocked} onClick={() => setConfirming(true)}>{t('Delete account')}</button>
      : <div className="account-delete-confirm" role="group" aria-label={t('Confirm delete')}>
          <p>{t('Delete account {{id}}? It is removed from every persona route, and its login files on the server are removed. This cannot be undone.', { id: accountId })}</p>
          <div className="account-inline">
            <button type="button" className="btn btn-danger" disabled={disabled || busy || blocked} onClick={() => void remove()}>{busy ? t('Deleting…') : t('Confirm delete')}</button>
            <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => setConfirming(false)}>{t('Cancel')}</button>
          </div>
        </div>}
    {error && <p role="alert" className="text-danger">{error}</p>}
  </div>;
}
