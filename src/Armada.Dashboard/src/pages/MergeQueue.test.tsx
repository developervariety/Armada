import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number>) =>
      (params ? text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(params[key] ?? '')) : text),
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

const auth = vi.hoisted(() => ({ current: { isAdmin: true, isTenantAdmin: true, user: { user: { id: 'usr_admin', tenantId: 'default' } } } }));
vi.mock('../context/AuthContext', () => ({ useAuth: () => auth.current }));

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listMergeQueue: vi.fn(),
  enqueueMerge: vi.fn(),
  deleteMergeEntry: vi.fn(),
  processMergeEntry: vi.fn(),
  processAllMergeQueue: vi.fn(),
  cancelMergeEntry: vi.fn(),
  listVessels: vi.fn(),
  getMissionDiff: vi.fn(),
  getMissionLog: vi.fn(),
}));

import { listMergeQueue, listVessels } from '../api/client';
import MergeQueue from './MergeQueue';

beforeEach(() => {
  vi.mocked(listVessels).mockResolvedValue({ success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] });
  vi.mocked(listMergeQueue).mockResolvedValue({
    success: true, pageNumber: 1, pageSize: 25, totalPages: 3, totalRecords: 70, totalMs: 1,
    objects: [{
      id: 'mrg_1', tenantId: null, missionId: null, vesselId: null, branchName: 'feature', targetBranch: 'main',
      status: 'Queued', priority: 0, testCommand: null, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
    }] as never,
  });
});

test('sends the status filter to the server instead of filtering one page', async () => {
  render(<MemoryRouter><MergeQueue /></MemoryRouter>);
  expect(await screen.findByText('70 records')).toBeInTheDocument();
  fireEvent.change(screen.getByTitle('Filter by status'), { target: { value: 'Failed' } });
  await waitFor(() => expect(listMergeQueue).toHaveBeenLastCalledWith(
    expect.objectContaining({ pageNumber: 1, filters: expect.objectContaining({ status: 'Failed' }) }),
  ));
});

test('shows Process All only to a global administrator, because the route processes every tenant', async () => {
  auth.current = { isAdmin: false, isTenantAdmin: true, user: { user: { id: 'usr_tenant_admin', tenantId: 'ten_a' } } };
  const { unmount } = render(<MemoryRouter><MergeQueue /></MemoryRouter>);
  expect(await screen.findByText('70 records')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Process All' })).not.toBeInTheDocument();
  unmount();

  auth.current = { isAdmin: true, isTenantAdmin: true, user: { user: { id: 'usr_admin', tenantId: 'default' } } };
  render(<MemoryRouter><MergeQueue /></MemoryRouter>);
  expect(await screen.findByRole('button', { name: 'Process All' })).toBeInTheDocument();
});
