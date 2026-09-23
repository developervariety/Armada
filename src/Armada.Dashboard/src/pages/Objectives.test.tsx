import { render, screen, waitFor } from '@testing-library/react';
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
  createBacklogItem: vi.fn(),
  deleteBacklogItem: vi.fn(),
  importObjectiveFromGitHub: vi.fn(),
  listAllBacklog: vi.fn(),
  listBacklog: vi.fn(),
  listFleets: vi.fn(),
  listVessels: vi.fn(),
  reorderBacklog: vi.fn(),
}));

import { listAllBacklog, listBacklog, listFleets, listVessels } from '../api/client';
import Objectives from './Objectives';

const empty = { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };

function objective(index: number) {
  return {
    id: `obj_${index}`, title: `Objective ${index}`, description: null, owner: null, category: null, targetVersion: null,
    refinementSummary: null, status: 'Draft', kind: 'Feature', priority: 'P2', backlogState: 'Inbox', effort: 'M',
    rank: index, dueUtc: null, tags: [], acceptanceCriteria: [], fleetIds: [], vesselIds: [], blockedByObjectiveIds: [],
    createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  };
}

beforeEach(() => {
  const all = Array.from({ length: 600 }, (_, index) => objective(index));
  vi.mocked(listFleets).mockResolvedValue(empty as never);
  vi.mocked(listVessels).mockResolvedValue(empty as never);
  // One server page holds at most 500 items.
  vi.mocked(listBacklog).mockResolvedValue({ ...empty, pageSize: 500, totalPages: 2, totalRecords: 600, objects: all.slice(0, 500) } as never);
  vi.mocked(listAllBacklog).mockResolvedValue(all as never);
});

function cardValue(label: string) {
  const card = Array.from(document.querySelectorAll('.playbook-overview-card'))
    .find((element) => element.querySelector('span')?.textContent === label);
  return card?.querySelector('strong')?.textContent;
}

test('counts every backlog item, not only the first server page', async () => {
  render(<MemoryRouter><Objectives /></MemoryRouter>);
  await waitFor(() => expect(cardValue('Backlog Items')).toBe('600'));
  expect(screen.getByText('Showing 600 of 600 backlog items.')).toBeInTheDocument();
});
