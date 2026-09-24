import { fireEvent, render, screen } from '@testing-library/react';
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
  listEvents: vi.fn(),
  deleteEventsBatch: vi.fn(),
  listCaptains: vi.fn(),
  listVessels: vi.fn(),
}));

import { listCaptains, listEvents, listVessels } from '../api/client';
import Events from './Events';

const empty = { success: true, pageNumber: 1, pageSize: 50, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };

function event(id: string, message: string, createdUtc: string) {
  return {
    id, tenantId: null, eventType: 'mission.changed', entityType: 'mission', entityId: null, captainId: null,
    missionId: null, vesselId: null, voyageId: null, message, payload: null, createdUtc,
  };
}

beforeEach(() => {
  vi.mocked(listCaptains).mockResolvedValue(empty as never);
  vi.mocked(listVessels).mockResolvedValue(empty as never);
  vi.mocked(listEvents).mockImplementation((async (params?: { pageNumber?: number; pageSize?: number }) => {
    const all = [event('evt_1', 'first page message', '2026-01-02T00:00:00Z'), event('evt_2', 'second page message', '2026-01-01T00:00:00Z')];
    const size = params?.pageSize ?? 50;
    const pageNumber = params?.pageNumber ?? 1;
    // The server returns one event per page at the default size, so the list spans two pages.
    const perPage = size >= 1000 ? all.length : 1;
    const objects = all.slice((pageNumber - 1) * perPage, pageNumber * perPage);
    return { ...empty, pageNumber, pageSize: size, totalPages: Math.ceil(all.length / perPage), totalRecords: all.length, objects };
  }) as never);
});

test('a message column filter finds events on every server page, not only the loaded one', async () => {
  render(<MemoryRouter><Events /></MemoryRouter>);
  await screen.findByText('first page message');

  const messageFilter = document.querySelectorAll('.column-filter-row input.col-filter')[2] as HTMLInputElement;
  fireEvent.change(messageFilter, { target: { value: 'second' } });

  expect(await screen.findByText('second page message')).toBeInTheDocument();
  expect(screen.queryByText('first page message')).not.toBeInTheDocument();
});

test('a column sort orders every event, not only the loaded page', async () => {
  render(<MemoryRouter><Events /></MemoryRouter>);
  await screen.findByText('first page message');

  // The default order is newest first; ascending creation time puts the older event from page two first.
  fireEvent.click(screen.getByTitle('Created -- click to sort'));

  expect(await screen.findByText('second page message')).toBeInTheDocument();
  const rows = Array.from(document.querySelectorAll('tbody tr')).map(row => row.textContent ?? '');
  expect(rows[0]).toContain('second page message');
});
