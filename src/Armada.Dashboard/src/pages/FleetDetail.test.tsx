import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string) => text,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', () => ({
  getFleet: vi.fn(),
  listFleets: vi.fn(),
  listVessels: vi.fn(),
  listPipelines: vi.fn(),
  createFleet: vi.fn(),
  updateFleet: vi.fn(),
  deleteFleet: vi.fn(),
}));

import { getFleet, listFleets, listPipelines, listVessels } from '../api/client';
import FleetDetail from './FleetDetail';

const empty = { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };

function renderAt(id: string) {
  render(
    <MemoryRouter initialEntries={[`/fleets/${id}`]}>
      <Routes>
        <Route path="/fleets/:id" element={<FleetDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.mocked(getFleet).mockReset();
  vi.mocked(listFleets).mockReset();
  vi.mocked(listFleets).mockResolvedValue(empty as never);
  vi.mocked(listVessels).mockResolvedValue(empty as never);
  vi.mocked(listPipelines).mockResolvedValue(empty as never);
});

test('reads the fleet by id instead of searching a fleet list', async () => {
  vi.mocked(getFleet).mockResolvedValue({
    id: 'flt_1', name: 'Fleet One', tenantId: null, description: null, defaultPipelineId: null,
    defaultPlaybooks: null, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  });
  renderAt('flt_1');
  expect((await screen.findAllByText('Fleet One')).length).toBeGreaterThan(0);
  expect(getFleet).toHaveBeenCalledWith('flt_1');
  expect(listFleets).not.toHaveBeenCalled();
  expect(listVessels).toHaveBeenCalledWith(expect.objectContaining({ filters: { fleetId: 'flt_1' } }));
});

test('shows not found when the fleet does not exist', async () => {
  vi.mocked(getFleet).mockRejectedValue(Object.assign(new Error('Not found'), { status: 404 }));
  renderAt('flt_missing');
  expect(await screen.findByText('Fleet not found.')).toBeInTheDocument();
});
