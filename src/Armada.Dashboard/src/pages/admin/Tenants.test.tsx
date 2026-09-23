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
  listTenants: vi.fn(),
  createTenant: vi.fn(),
  updateTenant: vi.fn(),
  deleteTenant: vi.fn(),
}));

import { listTenants } from '../../api/client';
import Tenants from './Tenants';

const tenants = Array.from({ length: 30 }, (_, index) => ({
  id: `ten_${index}`, name: `Tenant ${index}`, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
}));

beforeEach(() => {
  // The server returns 10 rows when no page size is sent.
  vi.mocked(listTenants).mockImplementation(async (params?: { pageNumber?: number; pageSize?: number }) => {
    const pageSize = Math.min(params?.pageSize ?? 10, 1000);
    const pageNumber = params?.pageNumber ?? 1;
    const start = (pageNumber - 1) * pageSize;
    return {
      success: true, pageNumber, pageSize, totalRecords: tenants.length, totalMs: 1,
      totalPages: Math.max(1, Math.ceil(tenants.length / pageSize)),
      objects: tenants.slice(start, start + pageSize),
    } as never;
  });
});

test('lists every tenant, not the ten the server returns by default', async () => {
  render(<Tenants />);
  await waitFor(() => expect(screen.getByText('30 records')).toBeInTheDocument());
});
