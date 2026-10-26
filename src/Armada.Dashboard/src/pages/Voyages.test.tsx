import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
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

import { cancelVoyage, listVoyages } from '../api/client';
import Voyages from './Voyages';

beforeEach(() => {
  vi.resetAllMocks();
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
  fireEvent.click(screen.getByText('Next'));
  await waitFor(() => expect(listVoyages).toHaveBeenLastCalledWith(
    expect.objectContaining({ pageNumber: 2, filters: expect.objectContaining({ status: 'Failed' }) }),
  ));
});

test('cancels exactly the selected voyages', async () => {
  vi.mocked(listVoyages).mockResolvedValue({
    success: true, pageNumber: 1, pageSize: 25, totalPages: 1, totalRecords: 2, totalMs: 1,
    objects: [
      { id: 'vyg_1', title: 'Voyage One', status: 'InProgress' },
      { id: 'vyg_2', title: 'Voyage Two', status: 'InProgress' },
    ] as never,
  });
  render(<MemoryRouter><Voyages /></MemoryRouter>);
  await screen.findByText('Voyage Two');
  const selectors = screen.getAllByLabelText('Select this voyage');
  fireEvent.click(selectors[0]);
  fireEvent.click(selectors[1]);
  fireEvent.click(screen.getByText(/Cancel Selected/));
  fireEvent.click(screen.getByText('Yes'));
  await waitFor(() => expect(cancelVoyage).toHaveBeenNthCalledWith(2, 'vyg_2'));
  expect(cancelVoyage).toHaveBeenNthCalledWith(1, 'vyg_1');
});

test('does not let a slower old request replace the filtered page', async () => {
  type Result = Awaited<ReturnType<typeof listVoyages>>;
  let resolveOld!: (result: Result) => void;
  let resolveCurrent!: (result: Result) => void;
  vi.mocked(listVoyages)
    .mockImplementationOnce(() => new Promise<Result>(resolve => { resolveOld = resolve; }))
    .mockImplementationOnce(() => new Promise<Result>(resolve => { resolveCurrent = resolve; }));
  render(<MemoryRouter><Voyages /></MemoryRouter>);
  fireEvent.change(screen.getByTitle('Filter by status'), { target: { value: 'Failed' } });
  await waitFor(() => expect(listVoyages).toHaveBeenCalledTimes(2));
  expect(resolveOld).toBeTypeOf('function');
  expect(resolveCurrent).toBeTypeOf('function');

  const page = (title: string) => ({
    success: true, pageNumber: 1, pageSize: 25, totalPages: 1, totalRecords: 1, totalMs: 1,
    objects: [{ id: `vyg_${title}`, title, status: 'Failed' }],
  }) as Result;
  await act(async () => resolveCurrent(page('Current voyage')));
  expect(await screen.findByText('Current voyage')).toBeInTheDocument();
  await act(async () => resolveOld(page('Stale voyage')));
  expect(screen.queryByText('Stale voyage')).not.toBeInTheDocument();
  expect(screen.getByText('Current voyage')).toBeInTheDocument();
});
