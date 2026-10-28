import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Inbox from './Inbox';
import { getInbox } from '../api/client';
import type { InboxItem } from '../types/models';

vi.mock('../api/client', () => ({ getInbox: vi.fn() }));
vi.mock('../context/LocaleContext', () => {
  const context = { t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v };
  return { useLocale: () => context };
});
vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ notifications: [], unreadCount: 0, markRead: vi.fn(), markAllRead: vi.fn() }),
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
