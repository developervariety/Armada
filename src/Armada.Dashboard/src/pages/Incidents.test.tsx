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

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ isAdmin: true, isTenantAdmin: false }),
}));

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', () => ({
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
