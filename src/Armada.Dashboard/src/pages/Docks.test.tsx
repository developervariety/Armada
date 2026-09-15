import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';

vi.mock('../context/LocaleContext', () => {
  const translate = (text: string, params?: Record<string, string | number | null | undefined>) => {
    if (!params) return text;
    return Object.entries(params).reduce(
      (current, [key, value]) => current.split(`{{${key}}}`).join(value == null ? '' : String(value)),
      text,
    );
  };
  const locale = {
    t: translate,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', () => ({
  listDocks: vi.fn(),
  deleteDock: vi.fn(),
  listCaptains: vi.fn(),
  listVessels: vi.fn(),
}));

import { listCaptains, listDocks, listVessels } from '../api/client';
import Docks from './Docks';

function dock(index: number) {
  return {
    id: `dck_${String(index).padStart(3, '0')}`,
    tenantId: null,
    vesselId: 'vsl_1',
    captainId: null,
    branchName: `branch-${String(index).padStart(3, '0')}`,
    worktreePath: `/docks/${index}`,
    active: true,
    createdUtc: new Date(Date.UTC(2026, 0, 1, 0, index)).toISOString(),
    lastUpdateUtc: new Date(Date.UTC(2026, 0, 1, 0, index)).toISOString(),
  };
}

beforeEach(() => {
  vi.mocked(listCaptains).mockResolvedValue({ success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] });
  vi.mocked(listVessels).mockResolvedValue({ success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] });
});

test('loads every dock and paginates, sorts and filters over the whole set', async () => {
  const all = Array.from({ length: 30 }, (_, index) => dock(index));
  vi.mocked(listDocks).mockResolvedValue({ success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 30, totalMs: 1, objects: all as never });

  render(<MemoryRouter><Docks /></MemoryRouter>);

  expect(await screen.findByText('30 records')).toBeInTheDocument();
  expect(listDocks).toHaveBeenCalledWith({ pageSize: 1000 });
  // Newest first across all 30 docks; one page of 25 rows.
  expect(screen.getByText('branch-029')).toBeInTheDocument();
  expect(screen.queryByText('branch-000')).not.toBeInTheDocument();
});
