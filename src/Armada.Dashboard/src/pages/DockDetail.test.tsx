import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string>) => (params ? text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => params[key] ?? '') : text),
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', () => ({
  getDock: vi.fn(),
  deleteDock: vi.fn(),
  listAllCaptains: vi.fn(),
  listAllVessels: vi.fn(),
}));

import { getDock, listAllCaptains, listAllVessels } from '../api/client';
import DockDetail from './DockDetail';

const empty = { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };

function renderAt(id: string) {
  render(
    <MemoryRouter initialEntries={[`/docks/${id}`]}>
      <Routes>
        <Route path="/docks/:id" element={<DockDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.mocked(getDock).mockReset();
  vi.mocked(listAllCaptains).mockResolvedValue(empty as never);
  vi.mocked(listAllVessels).mockResolvedValue(empty as never);
});

test('shows not found when the dock does not exist', async () => {
  vi.mocked(getDock).mockRejectedValue(Object.assign(new Error('Not found'), { status: 404 }));
  renderAt('dck_missing');
  expect(await screen.findByText('Dock not found.')).toBeInTheDocument();
});

test('refresh reads the dock again', async () => {
  vi.mocked(getDock).mockResolvedValue({
    id: 'dck_1', tenantId: null, vesselId: null, captainId: null, branchName: 'b', worktreePath: '/w',
    active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  } as never);
  renderAt('dck_1');
  expect(await screen.findByText('Dock Details')).toBeInTheDocument();
  fireEvent.click(screen.getByTitle('Refresh dock'));
  await waitFor(() => expect(getDock).toHaveBeenCalledTimes(2));
});
