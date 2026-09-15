import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Memories from './Memories';
import { listMemories } from '../api/client';

vi.mock('../api/client', () => ({ listMemories: vi.fn(), deleteMemory: vi.fn() }));
vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ isAdmin: false, isTenantAdmin: false, user: { user: { id: 'usr_1', tenantId: 'ten_a' } } }),
}));
vi.mock('../context/LocaleContext', () => ({
  useLocale: () => ({ t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v }),
}));
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

describe('Memories page', () => {
  beforeEach(() => {
    vi.mocked(listMemories).mockResolvedValue(page([
      memory({}),
      memory({ id: 'mem_2', scope: 'TenantWide', userId: 'usr_9', summary: 'Shared memory' }),
    ]) as never);
  });

  it('offers delete only on memories the viewer may change', async () => {
    render(<Memories />);
    await screen.findByText('Own memory');
    expect(screen.getAllByText('Delete')).toHaveLength(1);
    expect(screen.getAllByText('Personal')).toHaveLength(1);
    expect(screen.getAllByText('Tenant-wide')).toHaveLength(1);
  });

  it('sends the type filter as the server query parameter', async () => {
    render(<Memories />);
    await screen.findByText('Own memory');
    fireEvent.change(screen.getByLabelText('Filter by type'), { target: { value: 'Procedural' } });
    await waitFor(() => expect(listMemories).toHaveBeenLastCalledWith({ pageNumber: 1, pageSize: 25, filters: { type: 'Procedural' } }));
  });
});
