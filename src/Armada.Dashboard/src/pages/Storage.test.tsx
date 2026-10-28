import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import Storage from './Storage';
import { getStorage, refreshStorage } from '../api/client';
import type { StorageSnapshot } from '../types/models';

vi.mock('../api/client', () => ({
  getStorage: vi.fn(),
  refreshStorage: vi.fn(),
}));

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      params
        ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
        : text,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => (value ? `rel:${value}` : ''),
  };
  return { useLocale: () => locale };
});

vi.mock('../lib/useAutoRefresh', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lib/useAutoRefresh')>()),
  useAutoRefresh: () => ({ seconds: 0, setSeconds: vi.fn() }),
}));

function snapshot(overrides: Partial<StorageSnapshot> = {}): StorageSnapshot {
  return {
    measuredUtc: '2026-01-02T00:00:00Z',
    collecting: false,
    lastError: null,
    refreshIntervalSeconds: 900,
    historyCapacity: 288,
    files: {
      status: 'Partial',
      measuredUtc: '2026-01-02T00:00:00Z',
      entriesExamined: 250000,
      elapsedMilliseconds: 4200,
      partialReasons: ['EntryLimit'],
      volumes: [{ path: '/volume-example', totalBytes: 10 * 1024 ** 3, availableBytes: 4 * 1024 ** 3 }],
      locations: [
        {
          category: 'Docks',
          path: '<docks-directory>',
          bytes: 3 * 1024 * 1024,
          fileCount: 812,
          oldestWriteUtc: '2025-12-01T00:00:00Z',
          newestWriteUtc: '2026-01-01T00:00:00Z',
          linksSkipped: 2,
          vanishedEntries: 0,
          status: 'Partial',
          warnings: ['EntryLimit'],
          largestFiles: [{ path: '<docks-directory>/example/pack.bin', bytes: 2 * 1024 * 1024, lastWriteUtc: '2025-12-31T00:00:00Z' }],
        },
        {
          category: 'Logs',
          path: '<log-directory>',
          bytes: 2048,
          fileCount: 4,
          oldestWriteUtc: null,
          newestWriteUtc: null,
          linksSkipped: 0,
          vanishedEntries: 0,
          status: 'Complete',
          warnings: [],
          largestFiles: [],
        },
      ],
    },
    database: {
      provider: 'Sqlite',
      measuredUtc: '2026-01-02T00:00:00Z',
      allocatedBytes: 8 * 1024 * 1024,
      unusedBytes: 4096,
      status: 'Complete',
      warnings: ['TableSizesUnavailable'],
      tables: [{ schema: 'main', name: 'missions', rows: 120, countKind: 'Exact', dataBytes: null, indexBytes: null, warning: null }],
    },
    history: [
      { measuredUtc: '2026-01-01T00:00:00Z', observedFileBytes: 1024, databaseBytes: 1024, categories: { docks: 1024 } },
      { measuredUtc: '2026-01-02T00:00:00Z', observedFileBytes: 3 * 1024 * 1024, databaseBytes: 8 * 1024 * 1024, categories: { docks: 3 * 1024 * 1024 } },
    ],
    warnings: [],
    ...overrides,
  };
}

describe('Storage page', () => {
  beforeEach(() => {
    vi.mocked(getStorage).mockReset();
    vi.mocked(refreshStorage).mockReset();
  });

  it('shows category sizes and counts, database tables, growth and the reason a figure is partial', async () => {
    vi.mocked(getStorage).mockResolvedValue(snapshot());

    render(<Storage />);

    expect(await screen.findByText('<docks-directory>')).toBeInTheDocument();
    expect(screen.getByText('812')).toBeInTheDocument();
    expect(screen.getAllByText('3.0 MB').length).toBeGreaterThan(0);
    expect(screen.getByText('2 links not followed')).toBeInTheDocument();
    expect(screen.getByText('The scan stopped at its entry limit, so sizes are a lower bound.')).toBeInTheDocument();
    expect(screen.getByText('This database provider reports row counts only, not per-table sizes.')).toBeInTheDocument();
    expect(screen.getByText('missions')).toBeInTheDocument();
    expect(screen.getByText('120')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'File bytes over time' })).toBeInTheDocument();
    expect(screen.getByText(/across 2 measurements/)).toBeInTheDocument();
    expect(screen.getByText('<docks-directory>/example/pack.bin')).toBeInTheDocument();
  });

  it('starts a refresh and shows the new figures once the collection finishes', async () => {
    vi.mocked(getStorage).mockResolvedValueOnce(snapshot());
    vi.mocked(refreshStorage).mockResolvedValue(snapshot({ collecting: true }));
    const refreshed = snapshot();
    refreshed.files!.locations[1].bytes = 9 * 1024 * 1024;
    vi.mocked(getStorage).mockResolvedValue(refreshed);

    render(<Storage pollIntervalMs={5} />);
    await screen.findByText('<docks-directory>');

    fireEvent.click(screen.getByRole('button', { name: 'Refresh inventory' }));

    expect(refreshStorage).toHaveBeenCalledWith(false);
    await waitFor(() => expect(screen.getAllByText('9.0 MB').length).toBeGreaterThan(0));
    expect(screen.getByRole('button', { name: 'Refresh inventory' })).toBeEnabled();
  });

  it('offers no deletion or cleanup action', async () => {
    vi.mocked(getStorage).mockResolvedValue(snapshot());

    render(<Storage />);
    await screen.findByText('<docks-directory>');

    expect(screen.queryByRole('button', { name: /delete|clean|purge|remove/i })).not.toBeInTheDocument();
  });

  it('asks for the first measurement when nothing is cached yet', async () => {
    vi.mocked(getStorage).mockResolvedValue(snapshot({ measuredUtc: null, files: null, history: [] }));

    render(<Storage />);

    expect(await screen.findByText('No measurement yet. Use Refresh inventory to measure storage.')).toBeInTheDocument();
  });

  it('names a failed last collection while keeping the earlier figures', async () => {
    vi.mocked(getStorage).mockResolvedValue(snapshot({ lastError: 'CollectionFailed' }));

    render(<Storage />);

    expect(await screen.findByText(/The last collection failed/)).toBeInTheDocument();
    expect(screen.getByText('<docks-directory>')).toBeInTheDocument();
  });

  it('tells a caller who is not a global administrator why the inventory is unavailable', async () => {
    vi.mocked(getStorage).mockRejectedValue(Object.assign(new Error('403: Forbidden'), { status: 403 }));

    render(<Storage />);

    expect(await screen.findByText('The storage inventory is available to global administrators.')).toBeInTheDocument();
    expect(screen.queryByText('Failed to load the storage inventory.')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh inventory' })).toBeDisabled();
  });
});
