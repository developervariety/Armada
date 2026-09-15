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

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', () => ({
  listVoyages: vi.fn(),
  cancelVoyage: vi.fn(),
  purgeVoyage: vi.fn(),
  getVoyageMissionSummary: vi.fn(),
}));

import { listVoyages } from '../api/client';
import Voyages from './Voyages';

beforeEach(() => {
  vi.mocked(listVoyages).mockResolvedValue({
    success: true, pageNumber: 1, pageSize: 25, totalPages: 5, totalRecords: 120, totalMs: 1,
    objects: [{ id: 'vyg_1', title: 'Voyage One', status: 'InProgress', createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z' }] as never,
  });
});

test('sends the status filter to the server and keeps server pagination totals', async () => {
  render(<MemoryRouter><Voyages /></MemoryRouter>);
  expect(await screen.findByText('120 records')).toBeInTheDocument();
  fireEvent.change(screen.getByTitle('Filter by status'), { target: { value: 'Failed' } });
  await waitFor(() => expect(listVoyages).toHaveBeenLastCalledWith(
    expect.objectContaining({ pageNumber: 1, filters: expect.objectContaining({ status: 'Failed' }) }),
  ));
});
