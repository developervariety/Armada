import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import { deferred } from '../test/routeRace';

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

test('an earlier filter that responds last neither replaces the rows nor prunes the selection', async () => {
  const wake = deferred<unknown>();
  vi.mocked(listSignals).mockImplementation((async (params?: { filters?: Record<string, string> }) => {
    const type = params?.filters?.signalType;
    if (type === 'Wake') return wake.promise;
    if (type === 'Mail') return page(['sig_mail']);
    return page(['sig_all']);
  }) as never);
  render(<MemoryRouter><Signals /></MemoryRouter>);
  await screen.findByText('sig_all', { selector: '.id-value' });

  const typeSelect = screen.getByDisplayValue('All Types');
  fireEvent.change(typeSelect, { target: { value: 'Wake' } });
  await waitFor(() => expect(listSignals).toHaveBeenLastCalledWith(
    expect.objectContaining({ filters: expect.objectContaining({ signalType: 'Wake' }) }),
  ));
  fireEvent.change(typeSelect, { target: { value: 'Mail' } });
  await screen.findByText('sig_mail', { selector: '.id-value' });
  fireEvent.click(rowCheckbox('sig_mail'));
  expect(screen.getByText(/Delete Selected/)).toHaveTextContent('(1)');

  await act(async () => { wake.resolve(page(['sig_wake'])); });
  expect(screen.getByText('sig_mail', { selector: '.id-value' })).toBeInTheDocument();
  expect(screen.queryByText('sig_wake', { selector: '.id-value' })).not.toBeInTheDocument();
  expect(rowCheckbox('sig_mail').checked).toBe(true);
  expect(screen.getByText(/Delete Selected/)).toHaveTextContent('(1)');
});

test('a reload that finds the page past the new end moves to the last page instead of showing an empty table', async () => {
  let totalPages = 2;
  vi.mocked(listSignals).mockImplementation((async (params?: { pageNumber?: number }) => {
    const pageNumber = params?.pageNumber ?? 1;
    if (pageNumber > totalPages) return { ...empty, pageNumber, totalPages, totalRecords: totalPages, objects: [] };
    return { ...empty, pageNumber, totalPages, totalRecords: totalPages, objects: [signal(`sig_p${pageNumber}`)] };
  }) as never);
  render(<MemoryRouter><Signals /></MemoryRouter>);
  await screen.findByText('sig_p1', { selector: '.id-value' });

  fireEvent.click(screen.getByText('Next'));
  await screen.findByText('sig_p2', { selector: '.id-value' });

  // The only row on page 2 is removed elsewhere; the list now ends at page 1.
  totalPages = 1;
  fireEvent.click(screen.getByTitle('Refresh signals'));

  await screen.findByText('sig_p1', { selector: '.id-value' });
  expect(listSignals).toHaveBeenLastCalledWith(expect.objectContaining({ pageNumber: 1 }));
  expect(screen.getByRole('spinbutton')).toHaveValue(1);
});

test('a payload column filter finds signals on every server page, not only the loaded one', async () => {
  vi.mocked(listSignals).mockImplementation((async (params?: { pageNumber?: number; pageSize?: number }) => {
    const all = [signal('sig_first'), signal('sig_second')];
    const perPage = (params?.pageSize ?? 25) >= 1000 ? all.length : 1;
    const pageNumber = params?.pageNumber ?? 1;
    const objects = all.slice((pageNumber - 1) * perPage, pageNumber * perPage);
    return { ...empty, pageNumber, totalPages: Math.ceil(all.length / perPage), totalRecords: all.length, objects };
  }) as never);
  render(<MemoryRouter><Signals /></MemoryRouter>);
  await screen.findByText('sig_first', { selector: '.id-value' });

  const payloadFilter = document.querySelectorAll('thead input[type="text"]')[3] as HTMLInputElement;
  fireEvent.change(payloadFilter, { target: { value: 'second' } });

  expect(await screen.findByText('sig_second', { selector: '.id-value' })).toBeInTheDocument();
  expect(screen.queryByText('sig_first', { selector: '.id-value' })).not.toBeInTheDocument();
});
