import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import SubscriptionAccountsPanel from './SubscriptionAccountsPanel';
import {
  createAccountHome, getAccountLoginStatus, listCaptains, startAccountLogin, submitAccountLoginCode, submitAccountLoginKey,
} from '../api/client';
import type { PolicyRecord } from '../lib/subscriptionAccounts';

vi.mock('../api/client', () => ({
  listCaptains: vi.fn(), createCaptain: vi.fn(), createAccountHome: vi.fn(), startAccountLogin: vi.fn(),
  submitAccountLoginCode: vi.fn(), submitAccountLoginKey: vi.fn(), getAccountLoginStatus: vi.fn(), cancelAccountLogin: vi.fn(),
}));
vi.mock('../context/LocaleContext', () => ({ useLocale: () => ({ t: (s: string) => s }) }));

const account = (id: string, runtime: string, captainIds: string[] = []) => ({
  id, runtime, captainIds, homeDirectory: runtime === 'Cursor' ? null : `/data/accounts/${id}`,
  launchCredentialFile: runtime === 'Cursor' ? `/data/accounts/${id}/cursor-api-key` : null,
});

const status = (accountId: string, session: Record<string, unknown> | null = null) => ({
  accountId, runtime: null, configured: true, homeDirectory: `/data/accounts/${accountId}`, session, loginReady: false, loginReason: 'account_login_missing', loginCheckedUtc: null,
});

function renderPanel(policy: PolicyRecord, onSavePolicy = vi.fn().mockResolvedValue(undefined)) {
  render(<SubscriptionAccountsPanel savedPolicy={policy} statuses={[]} disabled={false} onSavePolicy={onSavePolicy} onRefresh={vi.fn()} />);
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
    expect((await screen.findAllByText(/codex-1/)).length).toBeGreaterThan(0);
    expect(screen.queryByText(/claude-1/)).not.toBeInTheDocument();
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
});
