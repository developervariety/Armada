import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';

const auth = vi.hoisted(() => ({
  current: { isAdmin: false, isTenantAdmin: true, user: { user: { id: 'usr_me' }, tenant: { id: 'ten_a', name: 'Tenant A' } } },
}));
vi.mock('../../context/AuthContext', () => ({ useAuth: () => auth.current }));
vi.mock('../../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number>) =>
      (params ? text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(params[key] ?? '')) : text),
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});
vi.mock('../../context/NotificationContext', () => ({ useNotifications: () => ({ pushToast: vi.fn() }) }));
vi.mock('../../lib/useProxySessionContext', () => ({ useProxySessionContext: () => null }));
vi.mock('../../api/client', async () => (await import('../../test/clientMock')).withAllPages({
  listCredentials: vi.fn(),
  listUsers: vi.fn(),
  listTenants: vi.fn(),
  createCredential: vi.fn(),
  updateCredential: vi.fn(),
  deleteCredential: vi.fn(),
}));

import { listCredentials, listUsers, updateCredential } from '../../api/client';
import Credentials from './Credentials';

function page<T>(objects: T[]) {
  return { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: objects.length, totalMs: 1, objects };
}

// Built at run time so no token-shaped literal sits in the source.
const ownToken = ['own', 'token', 'value'].join('-');
const MASK = '*'.repeat(8);

function credential(id: string, name: string, bearerToken: string) {
  return { id, tenantId: 'ten_a', userId: 'usr_me', name, bearerToken, active: true, isStatic: false, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z' };
}

beforeEach(() => {
  vi.mocked(listUsers).mockResolvedValue(page([{ id: 'usr_me', email: 'me@example.com' }]) as never);
  vi.mocked(listCredentials).mockResolvedValue(page([
    credential('crd_own', 'mine', ownToken),
    credential('crd_other', 'theirs', MASK),
  ]) as never);
});

test('offers to copy a readable token but not a masked one', async () => {
  render(<MemoryRouter><Credentials /></MemoryRouter>);
  const ownRow = (await screen.findByText('mine')).closest('tr') as HTMLElement;
  const otherRow = screen.getByText('theirs').closest('tr') as HTMLElement;

  expect(within(ownRow).getByTitle('Copy token')).toBeInTheDocument();
  expect(within(otherRow).queryByTitle('Copy token')).not.toBeInTheDocument();
});

test('saving a credential whose token is masked does not send the mask as the token', async () => {
  vi.mocked(updateCredential).mockResolvedValue({} as never);
  render(<MemoryRouter><Credentials /></MemoryRouter>);
  fireEvent.click(await screen.findByText('theirs'));
  fireEvent.click(screen.getByRole('button', { name: 'Save' }));

  await waitFor(() => expect(updateCredential).toHaveBeenCalled());
  expect(vi.mocked(updateCredential).mock.calls[0][1]).not.toHaveProperty('bearerToken');
});
