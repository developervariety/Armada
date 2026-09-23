import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import SubscriptionAccountsPanel from './SubscriptionAccountsPanel';
import {
  createAccountHome, deleteUsageAccount, getAccountLoginStatus, listCaptains, refreshUsageAccount, startAccountLogin,
  submitAccountLoginCode, submitAccountLoginKey,
} from '../api/client';
import type { PolicyRecord } from '../lib/subscriptionAccounts';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listCaptains: vi.fn(), createCaptain: vi.fn(), createAccountHome: vi.fn(), startAccountLogin: vi.fn(),
  submitAccountLoginCode: vi.fn(), submitAccountLoginKey: vi.fn(), getAccountLoginStatus: vi.fn(), cancelAccountLogin: vi.fn(),
  deleteUsageAccount: vi.fn(), refreshUsageAccount: vi.fn(),
}));
vi.mock('../context/LocaleContext', () => ({
  useLocale: () => ({
    t: (s: string, params?: Record<string, string | number>) =>
      Object.entries(params ?? {}).reduce((text, [key, value]) => text.split(`{{${key}}}`).join(String(value)), s),
  }),
}));

const account = (id: string, runtime: string, captainIds: string[] = []) => ({
  id, runtime, captainIds, homeDirectory: runtime === 'Cursor' ? null : `/data/accounts/${id}`,
  launchCredentialFile: runtime === 'Cursor' ? `/data/accounts/${id}/cursor-api-key` : null,
});

const status = (accountId: string, session: Record<string, unknown> | null = null) => ({
  accountId, runtime: null, configured: true, homeDirectory: `/data/accounts/${accountId}`, session, loginReady: false, loginReason: 'account_login_missing', loginCheckedUtc: null,
});

function renderPanel(policy: PolicyRecord, onSavePolicy = vi.fn().mockResolvedValue(undefined), onRefresh = vi.fn(), statuses: Array<Record<string, unknown>> = []) {
  render(<SubscriptionAccountsPanel savedPolicy={policy} statuses={statuses} disabled={false} onSavePolicy={onSavePolicy} onRefresh={onRefresh} />);
  return onSavePolicy;
}

describe('Subscription accounts panel', () => {
  beforeEach(() => {
    vi.mocked(listCaptains).mockReset().mockResolvedValue({ objects: [
      { id: 'cpt_codex', name: 'codex-1', runtime: 'Codex', model: 'gpt-x' },
      { id: 'cpt_claude', name: 'claude-1', runtime: 'ClaudeCode', model: 'claude-x' },
    ] } as never);
    vi.mocked(getAccountLoginStatus).mockReset();
    vi.mocked(createAccountHome).mockReset();
    vi.mocked(startAccountLogin).mockReset();
    vi.mocked(submitAccountLoginKey).mockReset();
    vi.mocked(submitAccountLoginCode).mockReset();
    vi.mocked(deleteUsageAccount).mockReset();
    vi.mocked(refreshUsageAccount).mockReset();
  });

  it('lists any number of accounts per runtime and adds another with a server-derived folder', async () => {
    const policy = { enabled: false, personaRoutes: {}, accounts: [account('codex', 'Codex'), account('codex-2', 'Codex'), account('codex-3', 'Codex')] };
    vi.mocked(createAccountHome).mockResolvedValue({ accountId: 'codex-4', homeDirectory: '/data/accounts/codex-4', cursorKeyFile: '/data/accounts/codex-4/cursor-api-key', created: true });
    const onSave = renderPanel(policy);
    expect(screen.getByText('codex-3')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Account name'), { target: { value: 'Codex' } });
    expect(screen.getByText('codex-4')).toBeInTheDocument();
    fireEvent.click(screen.getAllByText('Add account').find(e => e.tagName === 'BUTTON')!);
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(createAccountHome).toHaveBeenCalledWith('codex-4');
    const saved = onSave.mock.calls[0][0](policy) as PolicyRecord;
    expect((saved.accounts as unknown[]).length).toBe(4);
    expect((saved.accounts as Array<Record<string, unknown>>)[3]).toMatchObject({ id: 'codex-4', runtime: 'Codex', homeDirectory: '/data/accounts/codex-4' });
  });

  it('shows a device login URL and code for Codex and offers only same-runtime captains', async () => {
    vi.mocked(getAccountLoginStatus).mockResolvedValue(status('codex-2') as never);
    vi.mocked(startAccountLogin).mockResolvedValue({ sessionId: 's1', accountId: 'codex-2', runtime: 'Codex', method: 'DeviceCode', state: 'Pending', reason: null,
      verificationUrl: 'https://auth.openai.com/codex/device', userCode: 'ABCD-EFGH', needsCode: false, reused: false, startedUtc: '', expiresUtc: null, completedUtc: null });
    renderPanel({ accounts: [account('codex-2', 'Codex')] });
    fireEvent.click(screen.getByText('Manage'));
    fireEvent.click(await screen.findByText('Start device login'));
    expect(await screen.findByText('https://auth.openai.com/codex/device')).toBeInTheDocument();
    expect(screen.getByText('ABCD-EFGH')).toBeInTheDocument();
    const assignList = document.querySelector('.account-captain-list');
    expect(assignList?.textContent).toMatch(/codex-1/);
    expect(assignList?.textContent).not.toMatch(/claude-1/);
  });

  it('names the missing Cursor key, the one-line fix, and that usage is unmeasurable', async () => {
    vi.mocked(getAccountLoginStatus).mockResolvedValue({
      ...status('cursor-a'), loginReady: false, loginReason: 'account_launch_credential_unavailable',
    } as never);
    renderPanel({ accounts: [account('cursor-a', 'Cursor')] }, undefined, undefined, [
      { accountId: 'cursor-a', state: 'Unknown', reason: 'account_launch_credential_unavailable' },
    ]);
    expect(screen.getByText(/Cursor usage cannot be measured with an API key/)).toBeInTheDocument();
    fireEvent.click(screen.getByText('Manage'));
    expect(await screen.findByText(/Save a Cursor API key on this card/)).toBeInTheDocument();
  });

  it('saves requireAccountLogin and lists captains that would be refused', async () => {
    vi.mocked(listCaptains).mockResolvedValue({ objects: [
      { id: 'cpt_codex', name: 'codex-1', runtime: 'Codex', model: 'gpt-x' },
      { id: 'cpt_cursor', name: 'cursor-1', runtime: 'Cursor', model: 'cursor-x' },
    ] } as never);
    const onSave = renderPanel({ accounts: [account('codex', 'Codex', ['cpt_codex'])] });
    expect(await screen.findByText(/cursor-1/)).toBeInTheDocument();
    expect(screen.getByText('account_required')).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText('Require account login'));
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    const saved = onSave.mock.calls[0][0]({ accounts: [account('codex', 'Codex', ['cpt_codex'])] }) as PolicyRecord;
    expect(saved.requireAccountLogin).toBe(true);
  });

  it('sends an OpenCode key once, clears the field, and never renders the key', async () => {
    vi.mocked(getAccountLoginStatus).mockResolvedValue(status('oc-2') as never);
    vi.mocked(submitAccountLoginKey).mockResolvedValue({ sessionId: 's2', accountId: 'oc-2', runtime: 'OpenCode', method: 'ApiKey', state: 'Succeeded', reason: null,
      verificationUrl: null, userCode: null, needsCode: false, reused: false, startedUtc: '', expiresUtc: null, completedUtc: '' });
    renderPanel({ accounts: [account('oc-2', 'OpenCode')] });
    fireEvent.click(screen.getByText('Manage'));
    const field = await screen.findByLabelText('OpenCode Go API key');
    expect(field).toHaveAttribute('type', 'password');
    fireEvent.change(field, { target: { value: 'sk-secret-value' } });
    fireEvent.click(screen.getByText('Save key'));
    await waitFor(() => expect(submitAccountLoginKey).toHaveBeenCalledWith('oc-2', 'sk-secret-value'));
    expect(field).toHaveValue('');
    expect(document.body.textContent).not.toContain('sk-secret-value');
    expect(screen.queryByText('Start device login')).not.toBeInTheDocument();
  });

  it('relays a pasted Claude code and clears it', async () => {
    const pending = { sessionId: 's3', accountId: 'cl-2', runtime: 'ClaudeCode', method: 'PasteCode', state: 'Pending', reason: null,
      verificationUrl: 'https://claude.ai/oauth/authorize?x=1', userCode: null, needsCode: true, reused: false, startedUtc: '', expiresUtc: null, completedUtc: null };
    vi.mocked(getAccountLoginStatus).mockResolvedValue(status('cl-2') as never);
    vi.mocked(startAccountLogin).mockResolvedValue(pending as never);
    vi.mocked(submitAccountLoginCode).mockResolvedValue({ ...pending, needsCode: false } as never);
    renderPanel({ accounts: [account('cl-2', 'ClaudeCode')] });
    fireEvent.click(screen.getByText('Manage'));
    fireEvent.click(await screen.findByText('Start sign-in'));
    const field = await screen.findByLabelText('2. Paste the code shown after sign-in');
    fireEvent.change(field, { target: { value: 'pasted-code' } });
    fireEvent.click(screen.getByText('Submit code'));
    await waitFor(() => expect(submitAccountLoginCode).toHaveBeenCalledWith('cl-2', 'pasted-code'));
    expect(field).toHaveValue('');
  });

  it('disables Delete while captains are assigned and says to unassign them first', async () => {
    vi.mocked(getAccountLoginStatus).mockResolvedValue(status('codex-busy') as never);
    renderPanel({ accounts: [account('codex-busy', 'Codex', ['cpt_codex'])] });
    fireEvent.click(screen.getByText('Manage'));
    const button = (await screen.findAllByText('Delete account')).find(e => e.tagName === 'BUTTON')!;
    expect(button).toBeDisabled();
    expect(screen.getByText(/Unassign this account’s 1 captains before deleting it/)).toBeInTheDocument();
    fireEvent.click(button);
    expect(deleteUsageAccount).not.toHaveBeenCalled();
  });

  it('deletes only after a confirm step that names the account and its server login files, then refreshes', async () => {
    vi.mocked(getAccountLoginStatus).mockResolvedValue(status('codex-old') as never);
    vi.mocked(deleteUsageAccount).mockResolvedValue({ accountId: 'codex-old', routesRemoved: 2, personasRemoved: ['Judge'], loginCancelled: false, homeDeleted: true, homeReason: 'account_home_deleted' });
    const onRefresh = vi.fn();
    renderPanel({ accounts: [account('codex-old', 'Codex')] }, undefined, onRefresh);
    fireEvent.click(screen.getByText('Manage'));
    fireEvent.click((await screen.findAllByText('Delete account')).find(e => e.tagName === 'BUTTON')!);
    expect(deleteUsageAccount).not.toHaveBeenCalled();
    expect(screen.getByText(/Delete account codex-old\?.*login files on the server are removed/)).toBeInTheDocument();
    fireEvent.click(screen.getByText('Confirm delete'));
    await waitFor(() => expect(deleteUsageAccount).toHaveBeenCalledWith('codex-old'));
    await waitFor(() => expect(onRefresh).toHaveBeenCalledTimes(1));
    expect(screen.getByText(/Deleted account codex-old; 2 persona routes removed/)).toBeInTheDocument();
  });

  it('shows the refresh policy and refreshes one account with a spinner, then its new observed time and state', async () => {
    let finish: (value: unknown) => void = () => undefined;
    vi.mocked(refreshUsageAccount).mockReturnValue(new Promise(resolve => { finish = resolve; }) as never);
    const observedUtc = '2030-01-02T03:04:05Z';
    render(<SubscriptionAccountsPanel savedPolicy={{ refreshIntervalMinutes: 7, accounts: [{ ...account('claude-a', 'ClaudeCode'), maxAgeMinutes: 20 }] }}
      statuses={[{ accountId: 'claude-a', state: 'Unknown', reason: 'required_usage_window_unknown_or_stale', observedUtc: null }]}
      disabled={false} onSavePolicy={vi.fn()} onRefresh={vi.fn()} />);
    expect(screen.getByText('Usage refreshes every 7 min when read; data older than 20 min counts as Unknown.')).toBeInTheDocument();
    expect(screen.getByText('Unknown')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Refresh usage'));
    expect(await screen.findByText('Refreshing…')).toBeInTheDocument();
    expect(refreshUsageAccount).toHaveBeenCalledWith('claude-a');
    finish({ accountId: 'claude-a', collected: true, reason: 'usage_refreshed', retryAfterUtc: null, loginProbeRerun: true,
      status: { accountId: 'claude-a', state: 'Normal', reason: 'measured_usage_windows', observedUtc, source: 'claude', collectionError: null, runtime: 'ClaudeCode', loginCheckedUtc: null, exhaustedUntilUtc: null, windows: [] } });
    expect(await screen.findByText('Normal')).toBeInTheDocument();
    expect(screen.getByText(new Date(observedUtc).toLocaleString())).toBeInTheDocument();
    expect(screen.getByText('Refresh usage')).toBeInTheDocument();
  });

  it('reports a rate-limited refresh without claiming a read', async () => {
    vi.mocked(refreshUsageAccount).mockResolvedValue({ accountId: 'claude-a', collected: false, reason: 'usage_refresh_rate_limited', retryAfterUtc: '2030-01-02T03:04:05Z', loginProbeRerun: true,
      status: { accountId: 'claude-a', state: 'Unknown', reason: 'required_usage_window_unknown_or_stale', observedUtc: null, source: 'none', collectionError: 'usage_provider_rate_limited', runtime: 'ClaudeCode', loginCheckedUtc: null, exhaustedUntilUtc: null, windows: [] } });
    renderPanel({ accounts: [account('claude-a', 'ClaudeCode')] });
    fireEvent.click(screen.getByText('Refresh usage'));
    expect(await screen.findByText(/The provider asked to wait; usage was not read/)).toBeInTheDocument();
  });
});
