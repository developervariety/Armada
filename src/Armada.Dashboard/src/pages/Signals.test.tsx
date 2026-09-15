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
  listSignals: vi.fn(),
  sendSignal: vi.fn(),
  markSignalRead: vi.fn(),
  deleteSignalsBatch: vi.fn(),
  listCaptains: vi.fn(),
}));

import { listCaptains, listSignals } from '../api/client';
import Signals from './Signals';

const empty = { success: true, pageNumber: 1, pageSize: 25, totalPages: 0, totalRecords: 0, totalMs: 1, objects: [] };

beforeEach(() => {
  vi.mocked(listCaptains).mockResolvedValue(empty as never);
  vi.mocked(listSignals).mockResolvedValue(empty as never);
});

test('filters by signal type with the parameter the server reads for signals', async () => {
  render(<MemoryRouter><Signals /></MemoryRouter>);
  const typeSelect = await screen.findByDisplayValue('All Types');
  fireEvent.change(typeSelect, { target: { value: 'Wake' } });
  await waitFor(() => expect(listSignals).toHaveBeenLastCalledWith(
    expect.objectContaining({ filters: expect.objectContaining({ signalType: 'Wake' }) }),
  ));
  const calls = vi.mocked(listSignals).mock.calls;
  const lastFilters = calls[calls.length - 1]?.[0]?.filters ?? {};
  expect(lastFilters).not.toHaveProperty('type');
});

test('offers every signal type the server defines', async () => {
  render(<MemoryRouter><Signals /></MemoryRouter>);
  const typeSelect = await screen.findByDisplayValue('All Types');
  const values = Array.from((typeSelect as HTMLSelectElement).options).map((option) => option.value);
  expect(values).toEqual(expect.arrayContaining(['Assignment', 'Progress', 'Completion', 'Error', 'Heartbeat', 'Nudge', 'Mail', 'Wake']));
});
