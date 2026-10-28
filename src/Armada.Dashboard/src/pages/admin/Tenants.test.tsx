import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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

import { createTenant, listTenants } from '../../api/client';
import Tenants from './Tenants';

const tenants = Array.from({ length: 30 }, (_, index) => ({
  id: `ten_${index}`, name: `Tenant ${index}`, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
}));

beforeEach(() => {
  vi.mocked(createTenant).mockReset();
  localStorage.clear();
  sessionStorage.clear();
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

async function submitNewTenant(adminPassword?: string) {
  const user = userEvent.setup();
  render(<Tenants />);
  await user.click(await screen.findByRole('button', { name: '+ Tenant' }));
  await user.type(screen.getByLabelText('Name'), 'Acme');
  if (adminPassword) await user.type(screen.getByLabelText('Admin Password (optional)'), adminPassword);
  await user.click(screen.getByRole('button', { name: 'Save' }));
  return user;
}

const created = { id: 'ten_new', name: 'Acme', active: true, isProtected: false, createdUtc: '', lastUpdateUtc: '', adminEmail: 'admin@armada' };

test('shows a generated admin password once and drops it when the dialog closes', async () => {
  vi.mocked(createTenant).mockResolvedValue({ ...created, adminPassword: 'gen-secret-1' } as never);

  const user = await submitNewTenant();

  expect(await screen.findByText('gen-secret-1')).toBeInTheDocument();
  expect(screen.getByText('admin@armada')).toBeInTheDocument();
  expect(screen.getByText(/shown only once/i)).toBeInTheDocument();
  expect(vi.mocked(createTenant).mock.calls[0][0]).not.toHaveProperty('adminPassword');

  await user.click(screen.getByTitle('Copy password'));
  expect(await navigator.clipboard.readText()).toBe('gen-secret-1');

  await user.click(screen.getByRole('button', { name: 'I have copied it' }));
  expect(screen.queryByText('gen-secret-1')).not.toBeInTheDocument();
  expect(JSON.stringify({ ...localStorage })).not.toContain('gen-secret-1');
  expect(JSON.stringify({ ...sessionStorage })).not.toContain('gen-secret-1');
});

test('sends a chosen admin password and shows no password dialog', async () => {
  vi.mocked(createTenant).mockResolvedValue({ ...created, adminPassword: null } as never);

  await submitNewTenant('chosen-pass-1');

  await waitFor(() => expect(createTenant).toHaveBeenCalledTimes(1));
  expect(vi.mocked(createTenant).mock.calls[0][0]).toMatchObject({ name: 'Acme', adminPassword: 'chosen-pass-1' });
  expect(screen.queryByText(/shown only once/i)).not.toBeInTheDocument();
  expect(screen.queryByText('chosen-pass-1')).not.toBeInTheDocument();
});
