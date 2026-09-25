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

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ isAdmin: true, isTenantAdmin: false }),
}));

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  createIncident: vi.fn(),
  deleteIncident: vi.fn(),
  listDeployments: vi.fn(),
  listEnvironments: vi.fn(),
  listIncidents: vi.fn(),
  listReleases: vi.fn(),
  listVessels: vi.fn(),
}));

import { listDeployments, listEnvironments, listIncidents, listReleases, listVessels } from '../api/client';
import Incidents from './Incidents';
import { deferred } from '../test/routeRace';
import { DEFAULT_AUTO_REFRESH_SECONDS } from '../lib/useAutoRefresh';
import { SEARCH_DEBOUNCE_MS } from '../lib/useDebouncedValue';

const empty = { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };
const statusTotals: Record<string, number> = { Open: 7, Monitoring: 2, Mitigated: 1, RolledBack: 3, Closed: 40 };

beforeEach(() => {
  vi.mocked(listVessels).mockResolvedValue(empty as never);
  vi.mocked(listEnvironments).mockResolvedValue(empty as never);
  vi.mocked(listDeployments).mockResolvedValue(empty as never);
  vi.mocked(listReleases).mockResolvedValue(empty as never);
  vi.mocked(listIncidents).mockReset();
  vi.mocked(listIncidents).mockImplementation(async (query) => {
    if (query?.pageSize === 1 && query.status) {
      return { ...empty, pageSize: 1, totalRecords: statusTotals[query.status] ?? 0 } as never;
    }
    return {
      success: true, pageNumber: query?.pageNumber ?? 1, pageSize: query?.pageSize ?? 25, totalPages: 3, totalRecords: 53, totalMs: 1,
      objects: [{
        id: 'inc_1', title: 'Incident A', summary: null, impact: null, status: 'Open', severity: 'High',
        environmentId: null, environmentName: null, deploymentId: null, releaseId: null,
        createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
      }],
    } as never;
  });
});

// Card labels such as "Open" also appear as filter options, so read the overview cards only.
function cardValue(label: string) {
  const card = Array.from(document.querySelectorAll('.playbook-overview-card'))
    .find((element) => element.querySelector('span')?.textContent === label);
  return card?.querySelector('strong')?.textContent;
}

test('pages incidents on the server and takes totals from the server', async () => {
  render(<MemoryRouter><Incidents /></MemoryRouter>);
  expect(await screen.findByText('Incident A')).toBeInTheDocument();
  for (const call of vi.mocked(listIncidents).mock.calls) {
    expect(call[0]?.pageSize ?? 0).toBeLessThanOrEqual(500);
  }
  await waitFor(() => expect(cardValue('Total Incidents')).toBe('53'));
  expect(cardValue('Open')).toBe('7');
  expect(cardValue('Closed / Rolled Back')).toBe('43');
});

test('sends the status filter to the server', async () => {
  render(<MemoryRouter><Incidents /></MemoryRouter>);
  expect(await screen.findByText('Incident A')).toBeInTheDocument();
  fireEvent.change(screen.getByDisplayValue('All statuses'), { target: { value: 'Mitigated' } });
  await waitFor(() => expect(listIncidents).toHaveBeenLastCalledWith(
    expect.objectContaining({ pageNumber: 1, status: 'Mitigated' }),
  ));
});

test('an auto-refresh of the previous page that responds last does not replace the page the user moved to', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  try {
    localStorage.clear();
    const staleRefresh = deferred<unknown>();
    let pageOneReads = 0;
    const incident = (id: string, title: string) => ({
      id, title, summary: null, impact: null, status: 'Open', severity: 'High',
      environmentId: null, environmentName: null, deploymentId: null, releaseId: null,
      createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
    });
    vi.mocked(listIncidents).mockImplementation((async (query?: { pageNumber?: number; pageSize?: number; status?: string }) => {
      if (query?.pageSize === 1) return { ...empty, pageSize: 1, totalRecords: 0 };
      const pageNumber = query?.pageNumber ?? 1;
      const result = (title: string) => ({
        success: true, pageNumber, pageSize: 25, totalPages: 3, totalRecords: 53, totalMs: 1, objects: [incident(`inc_${pageNumber}`, title)],
      });
      if (pageNumber === 2) return result('Incident on page two');
      pageOneReads += 1;
      return pageOneReads === 1 ? result('Incident on page one') : staleRefresh.promise;
    }) as never);
    render(<MemoryRouter><Incidents /></MemoryRouter>);
    expect(await screen.findByText('Incident on page one')).toBeInTheDocument();

    await act(async () => { vi.advanceTimersByTime(DEFAULT_AUTO_REFRESH_SECONDS * 1000); });
    await waitFor(() => expect(pageOneReads).toBe(2));

    fireEvent.click(screen.getAllByText('Next')[0]);
    expect(await screen.findByText('Incident on page two')).toBeInTheDocument();

    await act(async () => {
      staleRefresh.resolve({
        success: true, pageNumber: 1, pageSize: 25, totalPages: 3, totalRecords: 53, totalMs: 1, objects: [incident('inc_1', 'Refreshed page one')],
      });
    });
    expect(screen.getByText('Incident on page two')).toBeInTheDocument();
    expect(screen.queryByText('Refreshed page one')).not.toBeInTheDocument();
  } finally {
    vi.useRealTimers();
  }
});

test('a refresh reloads the incidents but not the vessel, environment, deployment and release names', async () => {
  render(<MemoryRouter><Incidents /></MemoryRouter>);
  expect(await screen.findByText('Incident A')).toBeInTheDocument();
  await waitFor(() => expect(listVessels).toHaveBeenCalled());
  const lookupsBefore = [listVessels, listEnvironments, listDeployments, listReleases].map(fn => vi.mocked(fn).mock.calls.length);
  const pagesBefore = vi.mocked(listIncidents).mock.calls.length;

  fireEvent.click(screen.getByTitle('Refresh incidents'));

  await waitFor(() => expect(vi.mocked(listIncidents).mock.calls.length).toBeGreaterThan(pagesBefore));
  expect([listVessels, listEnvironments, listDeployments, listReleases].map(fn => vi.mocked(fn).mock.calls.length)).toEqual(lookupsBefore);
});

test('typing a search sends one request for the finished word, from the first page', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  try {
    render(<MemoryRouter><Incidents /></MemoryRouter>);
    expect(await screen.findByText('Incident A')).toBeInTheDocument();
    fireEvent.click(screen.getAllByText('Next')[0]);
    await waitFor(() => expect(listIncidents).toHaveBeenLastCalledWith(expect.objectContaining({ pageNumber: 2 })));

    const box = screen.getByPlaceholderText('Search by title, summary, impact, environment, or ID...');
    for (const value of ['d', 'di', 'disk']) fireEvent.change(box, { target: { value } });
    await act(async () => { await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS); });

    await waitFor(() => expect(listIncidents).toHaveBeenLastCalledWith(
      expect.objectContaining({ pageNumber: 1, search: 'disk' }),
    ));
    const searches = vi.mocked(listIncidents).mock.calls.map((call) => call[0]?.search).filter(Boolean);
    expect(searches).toEqual(['disk']);
  } finally {
    vi.useRealTimers();
  }
});

test('a load that keeps failing on the refresh timer opens the error once, and again only after a success', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  try {
    localStorage.clear();
    const working = vi.mocked(listIncidents).getMockImplementation()!;
    const tick = async () => {
      const before = vi.mocked(listIncidents).mock.calls.length;
      await act(async () => { await vi.advanceTimersByTimeAsync(DEFAULT_AUTO_REFRESH_SECONDS * 1000); });
      await waitFor(() => expect(vi.mocked(listIncidents).mock.calls.length).toBeGreaterThan(before));
      await act(async () => { await Promise.resolve(); });
    };
    vi.mocked(listIncidents).mockRejectedValue(new Error('offline'));
    render(<MemoryRouter><Incidents /></MemoryRouter>);
    expect(await screen.findByText('offline')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));

    await tick();
    expect(screen.queryByText('offline')).not.toBeInTheDocument();

    vi.mocked(listIncidents).mockImplementation(working);
    await tick();
    await screen.findByText('Incident A');

    vi.mocked(listIncidents).mockRejectedValue(new Error('offline'));
    await tick();
    expect(await screen.findByText('offline')).toBeInTheDocument();
  } finally {
    vi.useRealTimers();
  }
});
