import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Memories from './Memories';
import { listMemories, listVessels } from '../api/client';
import { deferred } from '../test/routeRace';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listMemories: vi.fn(),
  deleteMemory: vi.fn(),
  listVessels: vi.fn(),
}));
vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ isAdmin: false, isTenantAdmin: false, user: { user: { id: 'usr_1', tenantId: 'ten_a' } } }),
}));
vi.mock('../context/LocaleContext', () => {
  // One locale object for every render, as the provider gives, so the page's load is not rebuilt each render.
  const locale = { t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v };
  return { useLocale: () => locale };
});
vi.mock('../context/NotificationContext', () => {
  const notifications = { pushToast: vi.fn() };
  return { useNotifications: () => notifications };
});

function memory(overrides: Record<string, unknown>) {
  return {
    id: 'mem_1', tenantId: 'ten_a', userId: 'usr_1', scope: 'UserSpecific', type: 'Semantic', topic: 'build',
    key: null, summary: 'Own memory', content: 'content', salience: 0.5, version: 1, sourceKind: 'Manual',
    tags: [], createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

const page = (objects: unknown[]) => ({ success: true, pageNumber: 1, pageSize: 25, totalPages: 1, totalRecords: objects.length, totalMs: 1, objects });

function renderMemories() {
  return render(
    <MemoryRouter>
      <Memories />
    </MemoryRouter>,
  );
}

describe('Memories page', () => {
  beforeEach(() => {
    vi.mocked(listVessels).mockResolvedValue(page([{ id: 'vsl_named', name: 'North Wind' }]) as never);
    vi.mocked(listMemories).mockResolvedValue(page([
      memory({}),
      memory({ id: 'mem_2', scope: 'TenantWide', userId: 'usr_9', summary: 'Shared memory' }),
    ]) as never);
  });

  it('offers delete only on memories the viewer may change', async () => {
    renderMemories();
    await screen.findByText('Own memory');
    expect(screen.getAllByText('Delete')).toHaveLength(1);
    expect(screen.getAllByText('Personal')).toHaveLength(1);
    expect(screen.getAllByText('Tenant-wide')).toHaveLength(1);
  });

  it('sends the type filter as the server query parameter', async () => {
    renderMemories();
    await screen.findByText('Own memory');
    fireEvent.change(screen.getByLabelText('Filter by type'), { target: { value: 'Procedural' } });
    await waitFor(() => expect(listMemories).toHaveBeenLastCalledWith({ pageNumber: 1, pageSize: 25, filters: { type: 'Procedural' } }));
  });

  it('sends one search request with the final text after the user stops typing', async () => {
    renderMemories();
    await screen.findByText('Own memory');
    vi.mocked(listMemories).mockClear();
    vi.useFakeTimers();
    try {
      const input = screen.getByPlaceholderText('Search content, topic, tags...');
      for (const text of ['b', 'bu', 'bui', 'buil', 'build']) {
        fireEvent.change(input, { target: { value: text } });
        await act(async () => { vi.advanceTimersByTime(50); });
      }
      expect(listMemories).not.toHaveBeenCalled();

      await act(async () => { vi.advanceTimersByTime(1000); });
      expect(listMemories).toHaveBeenCalledTimes(1);
      expect(listMemories).toHaveBeenCalledWith({ pageNumber: 1, pageSize: 25, filters: { search: 'build' } });
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows the vessel name and falls back to the id', async () => {
    vi.mocked(listMemories).mockResolvedValue(page([
      memory({ id: 'mem_named', summary: 'Named vessel', vesselId: 'vsl_named' }),
      memory({ id: 'mem_bare', summary: 'Bare vessel', vesselId: 'vsl_missing' }),
    ]) as never);
    renderMemories();
    expect(await screen.findByRole('link', { name: 'North Wind' })).toHaveAttribute('href', '/vessels/vsl_named');
    expect(screen.getByRole('link', { name: 'vsl_missing' })).toHaveAttribute('href', '/vessels/vsl_missing');
  });

  it('shows the newest search result when an earlier search responds last', async () => {
    const earlier = deferred<unknown>();
    const later = deferred<unknown>();
    vi.mocked(listMemories).mockImplementation((async (params?: { filters?: Record<string, string> }) => {
      const term = params?.filters?.search;
      if (term === 'bu') return earlier.promise;
      if (term === 'build') return later.promise;
      return page([memory({})]);
    }) as never);
    renderMemories();
    await screen.findByText('Own memory');

    const input = screen.getByPlaceholderText('Search content, topic, tags...');
    // Each term waits out the search debounce so both requests are in flight together.
    fireEvent.change(input, { target: { value: 'bu' } });
    await waitFor(() => expect(listMemories).toHaveBeenLastCalledWith(expect.objectContaining({ filters: { search: 'bu' } })));
    fireEvent.change(input, { target: { value: 'build' } });
    await waitFor(() => expect(listMemories).toHaveBeenLastCalledWith(expect.objectContaining({ filters: { search: 'build' } })));
    await act(async () => { later.resolve(page([memory({ id: 'mem_new', summary: 'Newest match' })])); });
    expect(await screen.findByText('Newest match')).toBeInTheDocument();

    await act(async () => { earlier.resolve(page([memory({ id: 'mem_old', summary: 'Stale match' })])); });
    expect(screen.getByText('Newest match')).toBeInTheDocument();
    expect(screen.queryByText('Stale match')).not.toBeInTheDocument();
  });
});
