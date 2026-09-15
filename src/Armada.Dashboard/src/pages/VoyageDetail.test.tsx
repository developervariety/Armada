import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
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

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', () => ({
  getVoyage: vi.fn(),
  purgeVoyage: vi.fn(),
  cancelVoyage: vi.fn(),
  listMissions: vi.fn(),
  getMissionDiff: vi.fn(),
  getMissionLog: vi.fn(),
  createMission: vi.fn(),
  listVessels: vi.fn(),
  listCaptains: vi.fn(),
}));

import { getVoyage, listCaptains, listVessels } from '../api/client';
import VoyageDetail from './VoyageDetail';

const empty = { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };

function mission(id: string, status: string) {
  return { id, title: `Mission ${id}`, status, vesselId: null, captainId: null, branchName: null, voyageId: 'vyg_1', priority: 100 };
}

beforeEach(() => {
  vi.mocked(listVessels).mockResolvedValue(empty as never);
  vi.mocked(listCaptains).mockResolvedValue(empty as never);
  // Calls must not carry over from an earlier test in this file.
  vi.mocked(getVoyage).mockReset();
  vi.mocked(getVoyage).mockResolvedValue({
    voyage: { id: 'vyg_1', title: 'Voyage One', status: 'InProgress', createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z' },
    missions: [mission('msn_1', 'Complete'), mission('msn_2', 'LandingFailed'), mission('msn_3', 'Cancelled'), mission('msn_4', 'InProgress')],
  } as never);
});

function renderPage() {
  render(
    <MemoryRouter initialEntries={['/voyages/vyg_1']}>
      <Routes>
        <Route path="/voyages/:id" element={<VoyageDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

test('counts landing failures and cancellations in the progress line', async () => {
  renderPage();
  expect(await screen.findByText(/3\/4 finished/)).toBeInTheDocument();
  expect(screen.getByText(/1 failed/)).toBeInTheDocument();
  expect(screen.getByText(/1 cancelled/)).toBeInTheDocument();
});

test('a refresh reloads the voyage without replacing the page with a loading state', async () => {
  renderPage();
  expect(await screen.findByRole('heading', { name: 'Voyage One' })).toBeInTheDocument();
  fireEvent.click(screen.getByTitle('Refresh voyage'));
  expect(screen.queryByText('Loading...')).not.toBeInTheDocument();
  await waitFor(() => expect(getVoyage).toHaveBeenCalledTimes(2));
  expect(screen.getByRole('heading', { name: 'Voyage One' })).toBeInTheDocument();
});
