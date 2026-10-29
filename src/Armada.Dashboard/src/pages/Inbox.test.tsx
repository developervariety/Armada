import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Inbox from './Inbox';
import { getInbox, retryMissionLanding } from '../api/client';
import type { InboxItem } from '../types/models';

vi.mock('../api/client', () => ({ getInbox: vi.fn(), retryMissionLanding: vi.fn() }));
vi.mock('../context/LocaleContext', () => {
  const context = { t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v };
  return { useLocale: () => context };
});
const pushToast = vi.fn();
vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ notifications: [], unreadCount: 0, markRead: vi.fn(), markAllRead: vi.fn(), pushToast }),
}));
vi.mock('../lib/useAutoRefresh', () => ({ useAutoRefresh: () => ({ seconds: 0, setSeconds: vi.fn() }) }));
vi.mock('../components/shared/AutoRefreshSelect', () => ({ default: () => null }));
vi.mock('../components/shared/RefreshButton', () => ({ default: () => null }));
vi.mock('../components/shared/ErrorModal', () => ({ default: (props: { error: string }) => props.error ? <div role="alert">{props.error}</div> : null }));

const proposalItem: InboxItem = {
  kind: 'ask_proposal', severity: 'Warning', title: 'Ask approval: Create mission "Repair" on vessel Harbor Alpha',
  detail: 'A saved Ask conversation proposed this action.', entityType: 'ask_proposal', entityId: 'aap_1', href: '/ask?thread=ath_1',
};
const missionItem: InboxItem = {
  kind: 'review', severity: 'Warning', title: 'Review: Fix it', detail: 'Awaiting your review.',
  entityType: 'mission', entityId: 'msn_1', href: '/missions/msn_1',
};

function Where() { const location = useLocation(); return <div data-testid="where">{location.pathname + location.search}</div>; }
function renderInbox() {
  return render(<MemoryRouter initialEntries={['/inbox']}><Routes>
    <Route path="/inbox" element={<Inbox />} /><Route path="*" element={<Where />} />
  </Routes></MemoryRouter>);
}

describe('Inbox Ask proposals', () => {
  beforeEach(() => { vi.clearAllMocks(); vi.mocked(getInbox).mockResolvedValue([proposalItem, missionItem]); });

  it('lists a pending Ask proposal and opens its conversation without offering a decision', async () => {
    renderInbox();
    expect(await screen.findByText(proposalItem.title)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Approve' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Reject' })).toBeNull();
    expect(screen.getAllByRole('button', { name: 'Open conversation' })).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Open conversation' }));
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/ask?thread=ath_1'));
  });

  it('offers the conversation link only on Ask proposal items', async () => {
    vi.mocked(getInbox).mockResolvedValue([missionItem]);
    renderInbox();
    expect(await screen.findByText(missionItem.title)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Open conversation' })).toBeNull();
  });
});

describe('Inbox landing failures', () => {
  const landingItem: InboxItem = {
    kind: 'landing_failed', severity: 'Critical', title: 'Landing failed: Fix it',
    detail: 'merge conflict in a.txt\n  at line 3', entityType: 'mission', entityId: 'msn_9', href: '/missions/msn_9',
  };
  beforeEach(() => { vi.clearAllMocks(); vi.mocked(getInbox).mockResolvedValue([landingItem, missionItem]); });

  it('shows the failure reason preformatted and retries the landing in place, then reloads the list', async () => {
    vi.mocked(retryMissionLanding).mockResolvedValue({});
    renderInbox();
    const reason = await screen.findByText(/merge conflict in a\.txt/);
    expect(reason.tagName).toBe('PRE');
    expect(reason.textContent).toBe(landingItem.detail);

    fireEvent.click(screen.getByRole('button', { name: 'Retry Landing' }));
    await waitFor(() => expect(retryMissionLanding).toHaveBeenCalledWith('msn_9'));
    await waitFor(() => expect(getInbox).toHaveBeenCalledTimes(2));
    expect(pushToast).toHaveBeenCalledWith('success', expect.stringContaining('Landing succeeded'));
    expect(screen.queryByTestId('where')).toBeNull();
  });

  it('reports the landing error, stays on the inbox and offers the button only on landing failures', async () => {
    vi.mocked(retryMissionLanding).mockRejectedValue(new Error('still conflicted'));
    renderInbox();
    await screen.findByText(landingItem.title);
    expect(screen.getAllByRole('button', { name: 'Retry Landing' })).toHaveLength(1);

    fireEvent.click(screen.getByRole('button', { name: 'Retry Landing' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('still conflicted');
    expect(screen.queryByTestId('where')).toBeNull();
  });
});
