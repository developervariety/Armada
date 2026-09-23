import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
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

function signal(id: string) {
  return { id, tenantId: null, fromCaptainId: null, toCaptainId: null, type: 'Nudge', payload: id, read: false, createdUtc: '2026-01-01T00:00:00Z' };
}

function page(ids: string[]) {
  return { ...empty, totalPages: 1, totalRecords: ids.length, objects: ids.map(signal) };
}

function rowCheckbox(id: string): HTMLInputElement {
  const row = screen.getByText(id, { selector: '.id-value' }).closest('tr') as HTMLElement;
  return within(row).getByRole('checkbox') as HTMLInputElement;
}

test('a refresh keeps the selected signals that still exist and drops the ones that vanished', async () => {
  vi.mocked(listSignals).mockResolvedValue(page(['sig_a', 'sig_b', 'sig_c']) as never);
  render(<MemoryRouter><Signals /></MemoryRouter>);
  await screen.findByText('sig_a', { selector: '.id-value' });

  fireEvent.click(rowCheckbox('sig_a'));
  fireEvent.click(rowCheckbox('sig_b'));
  expect(screen.getByText(/Delete Selected/)).toHaveTextContent('(2)');

  vi.mocked(listSignals).mockResolvedValue(page(['sig_a', 'sig_c']) as never);
  const callsBefore = vi.mocked(listSignals).mock.calls.length;
  fireEvent.click(screen.getByTitle('Refresh signals'));
  await waitFor(() => expect(vi.mocked(listSignals).mock.calls.length).toBeGreaterThan(callsBefore));
  await waitFor(() => expect(screen.queryByText('sig_b', { selector: '.id-value' })).not.toBeInTheDocument());

  expect(rowCheckbox('sig_a').checked).toBe(true);
  expect(rowCheckbox('sig_c').checked).toBe(false);
  expect(screen.getByText(/Delete Selected/)).toHaveTextContent('(1)');
});
