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

vi.mock('../../api/client', async () => (await import('../../test/clientMock')).withAllPages({
  listCredentials: vi.fn(),
  createCredential: vi.fn(),
  updateCredential: vi.fn(),
  deleteCredential: vi.fn(),
  listUsers: vi.fn(),
  listTenants: vi.fn(),
}));

import { listCredentials, listTenants, listUsers } from '../../api/client';
import Credentials from './Credentials';

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
  id: `usr_${index}`, tenantId: `ten_${index}`, email: `user${index}@example.com`, firstName: 'U', lastName: 'T',
  isAdmin: false, isTenantAdmin: false, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
}));
const tenants = Array.from({ length: 30 }, (_, index) => ({
  id: `ten_${index}`, name: `Tenant ${index}`, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
}));
const credentials = [{
  id: 'crd_1', tenantId: 'ten_25', userId: 'usr_25', name: 'Build bot', bearerToken: 'redacted', active: true,
  createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
}];

beforeEach(() => {
  vi.mocked(listCredentials).mockImplementation(async (params?: { pageNumber?: number; pageSize?: number }) => serverPage(credentials, params) as never);
  vi.mocked(listUsers).mockImplementation(async (params?: { pageNumber?: number; pageSize?: number }) => serverPage(users, params) as never);
  vi.mocked(listTenants).mockImplementation(async (params?: { pageNumber?: number; pageSize?: number }) => serverPage(tenants, params) as never);
});

// The names also appear as filter options, so read the table cells only.
function cellTexts() {
  return Array.from(document.querySelectorAll('td')).map((cell) => cell.textContent?.trim());
}

test('names the owner and tenant of a credential past the first ten users and tenants', async () => {
  render(<Credentials />);
  await waitFor(() => expect(cellTexts()).toContain('user25@example.com'));
  expect(cellTexts()).toContain('Tenant 25');
  expect(screen.getAllByText('user25@example.com').length).toBeGreaterThan(0);
});
