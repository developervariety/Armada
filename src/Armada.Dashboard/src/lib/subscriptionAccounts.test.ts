import { describe, expect, it } from 'vitest';
import { SAFE_ACCOUNT_ID, cloneCaptainName, runtimeAccount, slugifyAccountId, withAccount, withAccountCaptains } from './subscriptionAccounts';

const home = (accountId: string) => ({ accountId, homeDirectory: `/data/accounts/${accountId}`, cursorKeyFile: `/data/accounts/${accountId}/cursor-api-key`, created: true });

describe('subscription account helpers', () => {
  it('slugs a name into a safe ID that is unique among existing accounts, with no limit per runtime', () => {
    expect(slugifyAccountId('Codex Work #2', [])).toBe('codex-work-2');
    expect(slugifyAccountId('../../etc', [])).toBe('etc');
    expect(slugifyAccountId('!!!', [])).toBe('account');
    let ids: string[] = [];
    for (let i = 0; i < 5; i++) ids = [...ids, slugifyAccountId('codex', ids)];
    expect(ids).toEqual(['codex', 'codex-2', 'codex-3', 'codex-4', 'codex-5']);
    ids.forEach(id => expect(SAFE_ACCOUNT_ID.test(id)).toBe(true));
  });

  it('binds home-based runtimes to the server folder and Cursor to its key file', () => {
    const codex = runtimeAccount('Codex', home('codex-2'));
    expect(codex).toMatchObject({ id: 'codex-2', runtime: 'Codex', collector: 'Codex', homeDirectory: '/data/accounts/codex-2', launchCredentialFile: null });
    const cursor = runtimeAccount('Cursor', home('cursor-2'));
    expect(cursor).toMatchObject({ runtime: 'Cursor', homeDirectory: null, launchCredentialFile: '/data/accounts/cursor-2/cursor-api-key' });
    expect(runtimeAccount('OpenCode', home('oc')).collector).toBe('OpenCodeGo');
    expect(runtimeAccount('ClaudeCode', home('cl')).collector).toBe('Claude');
  });

  it('adds accounts and assigns captains without touching other accounts', () => {
    const policy = withAccount(withAccount({ enabled: false, accounts: [], personaRoutes: {} }, runtimeAccount('Codex', home('a'))), runtimeAccount('Codex', home('b')));
    const assigned = withAccountCaptains(policy, 'b', ['cpt_1']);
    expect((assigned.accounts as Array<Record<string, unknown>>).map(a => a.captainIds)).toEqual([[], ['cpt_1']]);
    expect(cloneCaptainName('codex-1', 'b', ['codex-1-b'])).toBe('codex-1-b-2');
  });
});
