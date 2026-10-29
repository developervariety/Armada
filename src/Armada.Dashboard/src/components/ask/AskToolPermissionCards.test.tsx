import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import AskToolPermissionCards from './AskToolPermissionCards';
import { decideAskToolPermission, deleteAskToolPermissionRule, listAskToolPermissionRules, listAskToolPermissions } from '../../api/client';

vi.mock('../../api/client', () => ({
  decideAskToolPermission: vi.fn(), deleteAskToolPermissionRule: vi.fn(), listAskToolPermissionRules: vi.fn(), listAskToolPermissions: vi.fn(),
}));

const request = {
  id: 'atp_1', tenantId: 't', userId: 'u', threadId: 'thread-a', requestId: 'r1', toolName: 'Bash', inputJson: '{"command":"ls"}',
  toolUseId: null, state: 'Pending' as const, decisionSource: null, ruleId: null,
  createdUtc: '2026-10-25T12:00:00Z', expiresUtc: '2099-10-25T12:00:00Z', decidedUtc: null,
};
const rule = { id: 'atr_1', tenantId: 't', userId: 'u', threadId: 'thread-a', toolName: 'Read', action: 'Allow' as const, createdUtc: '2026-10-25T12:00:00Z' };
const t = (value: string) => value;

describe('AskToolPermissionCards', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(listAskToolPermissions).mockResolvedValue([request]);
    vi.mocked(listAskToolPermissionRules).mockResolvedValue([]);
  });

  it('shows a waiting request with its input and sends the owner decision once, with the remember choice', async () => {
    vi.mocked(decideAskToolPermission).mockResolvedValue({ ...request, state: 'Allowed' });
    render(<AskToolPermissionCards threadId="thread-a" busy={false} t={t} />);
    expect(await screen.findByText('{"command":"ls"}')).toBeTruthy();
    fireEvent.click(screen.getByLabelText(/Remember this choice/));
    fireEvent.click(screen.getByRole('button', { name: 'Allow tool' }));
    await waitFor(() => expect(decideAskToolPermission).toHaveBeenCalledTimes(1));
    expect(decideAskToolPermission).toHaveBeenCalledWith('thread-a', 'atp_1', 'allow', true);
  });

  it('keeps a refused decision visible', async () => {
    vi.mocked(decideAskToolPermission).mockRejectedValue(new Error('The request was already decided or has expired.'));
    render(<AskToolPermissionCards threadId="thread-a" busy={false} t={t} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Deny tool' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('already decided or has expired');
    expect(decideAskToolPermission).toHaveBeenCalledWith('thread-a', 'atp_1', 'deny', false);
  });

  it('offers no decision for a request that was decided or has expired', async () => {
    vi.mocked(listAskToolPermissions).mockResolvedValue([{ ...request, state: 'Expired' }, { ...request, id: 'atp_2', expiresUtc: '2000-01-01T00:00:00Z' }]);
    render(<AskToolPermissionCards threadId="thread-a" busy={false} t={t} />);
    await waitFor(() => expect(listAskToolPermissions).toHaveBeenCalled());
    expect(screen.queryByRole('button', { name: 'Allow tool' })).toBeNull();
    expect(screen.queryByText('Tool permissions')).toBeNull();
  });

  it('lists the remembered rules and forgets one', async () => {
    vi.mocked(listAskToolPermissions).mockResolvedValue([]);
    vi.mocked(listAskToolPermissionRules).mockResolvedValue([rule]);
    vi.mocked(deleteAskToolPermissionRule).mockResolvedValue(undefined);
    render(<AskToolPermissionCards threadId="thread-a" busy={false} t={t} />);
    expect(await screen.findByText('Always allow')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Forget rule Read' }));
    await waitFor(() => expect(deleteAskToolPermissionRule).toHaveBeenCalledWith('thread-a', 'atr_1'));
  });
});
