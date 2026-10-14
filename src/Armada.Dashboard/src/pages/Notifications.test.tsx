import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Notification } from '../context/NotificationContext';
import Notifications from './Notifications';

const state = vi.hoisted(() => ({
  markRead: vi.fn(),
  markAllRead: vi.fn(),
  clearHistory: vi.fn(),
  notifications: [] as Notification[],
}));

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({
    notifications: state.notifications,
    unreadCount: state.notifications.filter((item) => !item.read).length,
    markRead: state.markRead,
    markAllRead: state.markAllRead,
    clearHistory: state.clearHistory,
  }),
}));

vi.mock('../context/LocaleContext', () => ({
  useLocale: () => ({ t: (value: string) => value, formatRelativeTime: () => 'just now', formatDateTime: () => 'today' }),
}));

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname}</div>;
}

describe('Notifications history page', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    state.notifications = [{
      id: 'n-1', severity: 'warning', title: 'Mission failed', message: 'Review mission',
      timestampUtc: '2026-10-14T12:00:00Z', missionId: 'msn-1', voyageId: null,
      captainId: null, read: false,
    }];
  });

  it('shows notification history and exposes read and clear actions', () => {
    render(<MemoryRouter initialEntries={['/notifications']}><Notifications /></MemoryRouter>);
    expect(screen.getByText('Mission failed')).toBeInTheDocument();
    expect(screen.getByText('(1 unread)')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Mark All Read' }));
    fireEvent.click(screen.getByRole('button', { name: 'Clear History' }));
    expect(state.markAllRead).toHaveBeenCalledOnce();
    expect(state.clearHistory).toHaveBeenCalledOnce();
  });

  it('marks a row read and opens its mission', () => {
    render(<MemoryRouter initialEntries={['/notifications']}>
      <LocationProbe />
      <Routes><Route path="/notifications" element={<Notifications />} /><Route path="/missions/:id" element={<div>mission detail</div>} /></Routes>
    </MemoryRouter>);
    fireEvent.click(screen.getByText('Mission failed'));
    expect(state.markRead).toHaveBeenCalledWith('n-1');
    expect(screen.getByText('mission detail')).toBeInTheDocument();
    expect(screen.getByTestId('location')).toHaveTextContent('/missions/msn-1');
  });
});
