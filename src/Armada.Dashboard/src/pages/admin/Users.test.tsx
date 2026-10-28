import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';

const auth = vi.hoisted(() => ({
  value: { user: null as unknown, isAdmin: true, isTenantAdmin: false, refreshUser: undefined as unknown },
}));

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
  useAuth: () => auth.value,
}));

vi.mock('../../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../../lib/useProxySessionContext', () => ({ useProxySessionContext: () => null }));

vi.mock('../../api/client', async () => (await import('../../test/clientMock')).withAllPages({
  listUsers: vi.fn(),
  listTenants: vi.fn(),
  createUser: vi.fn(),
  updateUser: vi.fn(),
  deleteUser: vi.fn(),
}));

import { listTenants, listUsers, updateUser } from '../../api/client';
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
  auth.value = { user: null, isAdmin: true, isTenantAdmin: false, refreshUser: undefined };
  vi.mocked(updateUser).mockReset();
  vi.mocked(listUsers).mockImplementation(async (params?: { pageNumber?: number; pageSize?: number }) => serverPage(users, params) as never);
  vi.mocked(listTenants).mockImplementation(async (params?: { pageNumber?: number; pageSize?: number }) =>
    serverPage([{ id: 'ten_1', name: 'Tenant One', active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z' }], params) as never);
});

test('lists every user, not the ten the server returns by default', async () => {
  render(<MemoryRouter><Users /></MemoryRouter>);
  await waitFor(() => expect(screen.getByText('30 records')).toBeInTheDocument());
});

const signedIn = users[3];

function signInAs(refreshUser = vi.fn()) {
  auth.value = {
    user: { tenant: { id: 'ten_1', name: 'Tenant One' }, user: signedIn, passwordChangeRequired: false },
    isAdmin: true,
    isTenantAdmin: false,
    refreshUser,
  };
  return refreshUser;
}

async function openUser(email: string, route = '/') {
  const view = render(<MemoryRouter initialEntries={[route]}><Users /></MemoryRouter>);
  await userEvent.setup().click(await screen.findByText(email));
  return view;
}

test('changing your own password sends the current password', async () => {
  const refreshUser = signInAs();
  vi.mocked(updateUser).mockResolvedValue({} as never);
  const user = userEvent.setup();
  await openUser(signedIn.email);

  await user.type(screen.getByLabelText('Current Password'), 'old-password');
  await user.type(screen.getByLabelText('New Password'), 'new-password-1');
  await user.type(screen.getByLabelText('Confirm New Password'), 'new-password-1');
  await user.click(screen.getByRole('button', { name: 'Save' }));

  await waitFor(() => expect(updateUser).toHaveBeenCalledTimes(1));
  expect(updateUser).toHaveBeenCalledWith(signedIn.id, expect.objectContaining({
    password: 'new-password-1',
    currentPassword: 'old-password',
  }));
  await waitFor(() => expect(refreshUser).toHaveBeenCalled());
});

test('saving your own record without a new password sends no password fields', async () => {
  signInAs();
  vi.mocked(updateUser).mockResolvedValue({} as never);
  const user = userEvent.setup();
  await openUser(signedIn.email);

  await user.click(screen.getByRole('button', { name: 'Save' }));

  await waitFor(() => expect(updateUser).toHaveBeenCalledTimes(1));
  expect(vi.mocked(updateUser).mock.calls[0][1]).not.toHaveProperty('currentPassword');
  expect(vi.mocked(updateUser).mock.calls[0][1]).not.toHaveProperty('password');
});

test('an administrator resetting another user sends no current password', async () => {
  signInAs();
  vi.mocked(updateUser).mockResolvedValue({} as never);
  const user = userEvent.setup();
  await openUser(users[4].email);

  expect(screen.queryByLabelText('Current Password')).not.toBeInTheDocument();
  await user.type(screen.getByLabelText('New Password'), 'reset-pass-1');
  await user.type(screen.getByLabelText('Confirm New Password'), 'reset-pass-1');
  await user.click(screen.getByRole('button', { name: 'Save' }));

  await waitFor(() => expect(updateUser).toHaveBeenCalledTimes(1));
  expect(vi.mocked(updateUser).mock.calls[0][1]).toMatchObject({ password: 'reset-pass-1' });
  expect(vi.mocked(updateUser).mock.calls[0][1]).not.toHaveProperty('currentPassword');
});

test('shows the server message when the current password is refused', async () => {
  signInAs();
  vi.mocked(updateUser).mockRejectedValue(Object.assign(new Error('The current password is incorrect.'), { status: 403 }));
  const user = userEvent.setup();
  await openUser(signedIn.email);

  await user.type(screen.getByLabelText('Current Password'), 'wrong');
  await user.type(screen.getByLabelText('New Password'), 'new-password-1');
  await user.type(screen.getByLabelText('Confirm New Password'), 'new-password-1');
  await user.click(screen.getByRole('button', { name: 'Save' }));

  expect(await screen.findByText('The current password is incorrect.')).toBeInTheDocument();
});

test('opens your own record for a password change when the link asks for it', async () => {
  signInAs();
  render(<MemoryRouter initialEntries={['/server?tab=users&changePassword=1']}><Users /></MemoryRouter>);

  expect(await screen.findByRole('heading', { name: 'Edit User' })).toBeInTheDocument();
  expect(screen.getByLabelText('Email')).toHaveValue(signedIn.email);
  expect(screen.getByLabelText('Current Password')).toBeInTheDocument();
});
