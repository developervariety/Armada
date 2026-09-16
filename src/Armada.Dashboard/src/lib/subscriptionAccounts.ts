import type { AccountLoginHome, AccountRuntime } from '../types/models';

export type PolicyRecord = Record<string, unknown>;
export type AccountRecord = Record<string, unknown>;

export const ACCOUNT_RUNTIMES: AccountRuntime[] = ['Codex', 'ClaudeCode', 'OpenCode', 'Cursor'];

export const RUNTIME_LABELS: Record<AccountRuntime, string> = {
  Codex: 'Codex',
  ClaudeCode: 'Claude Code',
  OpenCode: 'OpenCode',
  Cursor: 'Cursor',
};

/** Matches the server's safe account ID rule: a letter or digit, then letters, digits, hyphens, underscores; at most 64. */
export const SAFE_ACCOUNT_ID = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;

/** Turn an operator-entered name into a unique, safe account ID. */
export function slugifyAccountId(name: string, existingIds: string[]): string {
  const base = name.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 56) || 'account';
  const taken = new Set(existingIds.map(id => id.toLowerCase()));
  if (!taken.has(base)) return base;
  for (let n = 2; ; n++) {
    const candidate = `${base}-${n}`;
    if (!taken.has(candidate)) return candidate;
  }
}

/** Usage collector that measures the runtime's own account, reading the login inside the account folder. */
export function collectorForRuntime(runtime: AccountRuntime): string {
  switch (runtime) {
    case 'Codex': return 'Codex';
    case 'ClaudeCode': return 'Claude';
    case 'OpenCode': return 'OpenCodeGo';
    // The Cursor usage collector needs a browser session cookie, which a key login does not provide.
    default: return 'Manual';
  }
}

/** Every account field with its default, matching the settings contract. */
export function accountTemplate(overrides: AccountRecord = {}): AccountRecord {
  return {
    id: 'account', captainIds: [], collector: 'Manual', credentialEnv: null, credentialFilePath: null,
    runtime: null, homeDirectory: null, launchCredentialEnv: null, launchCredentialFile: null, windowModels: {}, monthlyCost: 0,
    lowRemainingPercent: 25, reserveRemainingPercent: 10, recoveryRemainingPercent: 35,
    resetGraceMinutes: 0, maxAgeMinutes: 15, unknownUsagePolicy: 'Allow',
    maxConcurrentMissions: 0, reservedPersonas: [], reservedPriorityAtOrAbove: null,
    manualSnapshot: null, usageFilePath: null, overrideState: null, overrideUntilUtc: null,
    ...overrides,
  };
}

/** A new account bound to its server-derived folder: a login home, or a key file for Cursor. */
export function runtimeAccount(runtime: AccountRuntime, home: AccountLoginHome): AccountRecord {
  return accountTemplate({
    id: home.accountId,
    runtime,
    collector: collectorForRuntime(runtime),
    homeDirectory: runtime === 'Cursor' ? null : home.homeDirectory,
    launchCredentialFile: runtime === 'Cursor' ? home.cursorKeyFile : null,
  });
}

export function policyAccounts(policy: PolicyRecord | null): AccountRecord[] {
  return Array.isArray(policy?.accounts) ? (policy!.accounts as AccountRecord[]).filter(a => a && typeof a === 'object') : [];
}

export function withAccount(policy: PolicyRecord, account: AccountRecord): PolicyRecord {
  return { ...policy, accounts: [...policyAccounts(policy), account] };
}

export function withAccountCaptains(policy: PolicyRecord, accountId: string, captainIds: string[]): PolicyRecord {
  return {
    ...policy,
    accounts: policyAccounts(policy).map(a => a.id === accountId ? { ...a, captainIds: [...captainIds] } : a),
  };
}

/** A captain name not yet used, derived from a source captain and the account. */
export function cloneCaptainName(sourceName: string, accountId: string, existingNames: string[]): string {
  const taken = new Set(existingNames.map(n => n.toLowerCase()));
  const base = `${sourceName}-${accountId}`;
  if (!taken.has(base.toLowerCase())) return base;
  for (let n = 2; ; n++) {
    const candidate = `${base}-${n}`;
    if (!taken.has(candidate.toLowerCase())) return candidate;
  }
}
