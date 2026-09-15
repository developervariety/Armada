import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, expect, test, vi } from 'vitest';

vi.mock('../../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number>) =>
      (params ? text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(params[key] ?? '')) : text),
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ user: null, isAdmin: true, isTenantAdmin: false }),
}));

vi.mock('../../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../../lib/useProxySessionContext', () => ({ useProxySessionContext: () => null }));

vi.mock('../../api/client', () => ({
  listUsers: vi.fn(),
  listTenants: vi.fn(),
  createUser: vi.fn(),
  updateUser: vi.fn(),
  deleteUser: vi.fn(),
}));

import { listTenants, listUsers } from '../../api/client';
import Users from './Users';

// The server returns 10 rows when no page size is sent.
function serverPage<T>(all: T[], params?: { pageNumber?: number; pageSize?: number }) {
  const pageSize = Math.min(params?.pageSize ?? 10, 1000);
  const pageNumber = params?.pageNumber ?? 1;
  const start = (pageNumber - 1) * pageSize;
  return {
    success: true, pageNumber, pageSize, totalRecords: all.length, totalMs: 1,
    totalPages: Math.max(1, Math.ceil(all.length / pageSize)),
    objects: all.slice(start, start + pageSize),
  };
}

const users = Array.from({ length: 30 }, (_, index) => ({
  id: `usr_${index}`, tenantId: 'ten_1', email: `user${index}@example.com`, firstName: `User${index}`, lastName: 'Test',
  isAdmin: false, isTenantAdmin: false, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
}));

beforeEach(() => {
  vi.mocked(listUsers).mockImplementation(async (params?: { pageNumber?: number; pageSize?: number }) => serverPage(users, params) as never);
  vi.mocked(listTenants).mockImplementation(async (params?: { pageNumber?: number; pageSize?: number }) =>
    serverPage([{ id: 'ten_1', name: 'Tenant One', active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z' }], params) as never);
});

test('lists every user, not the ten the server returns by default', async () => {
  render(<Users />);
  await waitFor(() => expect(screen.getByText('30 records')).toBeInTheDocument());
});
