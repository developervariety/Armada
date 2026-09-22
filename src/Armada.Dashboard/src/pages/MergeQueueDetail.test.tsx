import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import MergeQueueDetail from './MergeQueueDetail';
import { NavigateButton, deferred } from '../test/routeRace';
import { getMergeEntry, getVesselLandingPreview, listVessels } from '../api/client';

vi.mock('../api/client', () => ({
  getMergeEntry: vi.fn(),
  deleteMergeEntry: vi.fn(),
  processMergeEntry: vi.fn(),
  cancelMergeEntry: vi.fn(),
  listVessels: vi.fn(),
  getMissionDiff: vi.fn(),
  getMissionLog: vi.fn(),
  getVesselLandingPreview: vi.fn(),
}));

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      params
        ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
        : text,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => {
  const notifications = { pushToast: vi.fn() };
  return { useNotifications: () => notifications };
});

function entry(overrides: Record<string, unknown> = {}) {
  return {
    id: 'mrg_1',
    tenantId: null,
    missionId: null,
    vesselId: null,
    branchName: 'armada/msn_1',
    targetBranch: 'main',
    status: 'Queued',
    priority: 0,
    batchId: null,
    testCommand: null,
    testOutput: null,
    testExitCode: null,
    createdUtc: '2026-09-15T10:00:00Z',
    lastUpdateUtc: '2026-09-15T10:00:00Z',
    testStartedUtc: null,
    completedUtc: null,
    ...overrides,
  };
}

/**
 * Captures the page's poll interval so a test fires a tick deterministically instead of racing real time. Other
 * intervals, such as the testing library's own waitFor polling, still use the real timer.
 */
function captureIntervals(pollMs = 5000) {
  const realSetInterval = window.setInterval.bind(window);
  const realClearInterval = window.clearInterval.bind(window);
  const callbacks = new Map<number, () => void>();
  let nextId = 1_000_000;
  const setSpy = vi.spyOn(window, 'setInterval').mockImplementation(((handler: () => void, delay?: number, ...args: unknown[]) => {
    if (delay !== pollMs) return realSetInterval(handler, delay, ...args);
    const id = nextId++;
    callbacks.set(id, handler);
    return id;
  }) as never);
  const clearSpy = vi.spyOn(window, 'clearInterval').mockImplementation(((id?: number) => {
    if (id !== undefined && callbacks.has(id)) callbacks.delete(id);
    else realClearInterval(id);
  }) as never);
  return {
    active: () => callbacks.size,
    tick: async () => {
      await act(async () => {
        for (const handler of Array.from(callbacks.values())) handler();
      });
    },
    restore: () => {
      setSpy.mockRestore();
      clearSpy.mockRestore();
    },
  };
}

function renderDetail() {
  return render(
    <MemoryRouter initialEntries={['/merge-queue/mrg_1']}>
      <Routes>
        <Route path="/merge-queue/:id" element={<MergeQueueDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('MergeQueueDetail', () => {
  beforeEach(() => {
    vi.mocked(listVessels).mockResolvedValue({ objects: [] } as never);
    vi.mocked(getVesselLandingPreview).mockResolvedValue(null as never);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.clearAllMocks();
  });

  it('shows the pull request link and the merge failure the server reports', async () => {
    vi.mocked(getMergeEntry).mockResolvedValue(entry({
      status: 'Failed',
      prUrl: 'https://example.test/pr/7',
      prBaseBranch: 'main',
      mergeFailureClass: 'TextConflict',
      mergeFailureSummary: 'Automatic merge failed',
      conflictedFiles: 'src/Program.cs',
    }) as never);
    renderDetail();

    expect(await screen.findByRole('link', { name: 'https://example.test/pr/7' })).toHaveAttribute('href', 'https://example.test/pr/7');
    expect(screen.getByText('TextConflict')).toBeInTheDocument();
    expect(screen.getByText('Automatic merge failed')).toBeInTheDocument();
    expect(screen.getByText('src/Program.cs')).toBeInTheDocument();
  });

  it('polls while the entry is testing and stops once it has an outcome, without the loading spinner', async () => {
    const intervals = captureIntervals();
    vi.mocked(getMergeEntry).mockResolvedValue(entry({ status: 'Testing' }) as never);
    renderDetail();
    expect(await screen.findByText('Testing')).toBeInTheDocument();
    expect(getMergeEntry).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(intervals.active()).toBe(1));

    vi.mocked(getMergeEntry).mockResolvedValue(entry({ status: 'Landed' }) as never);
    await intervals.tick();
    await waitFor(() => expect(getMergeEntry).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Landed')).toBeInTheDocument();
    expect(screen.queryByText('Loading...')).not.toBeInTheDocument();

    await waitFor(() => expect(intervals.active()).toBe(0));
    intervals.restore();
  });

  it('keeps a loaded entry on screen when a later refresh fails', async () => {
    const intervals = captureIntervals();
    vi.mocked(getMergeEntry).mockResolvedValue(entry({ status: 'Merging' }) as never);
    renderDetail();
    expect(await screen.findByText('Merging')).toBeInTheDocument();
    await waitFor(() => expect(intervals.active()).toBe(1));

    vi.mocked(getMergeEntry).mockRejectedValue(new Error('transient') as never);
    await intervals.tick();
    await waitFor(() => expect(getMergeEntry).toHaveBeenCalledTimes(2));

    expect(screen.getByText('Merging')).toBeInTheDocument();
    expect(screen.queryByText('Failed to load merge entry.')).not.toBeInTheDocument();
    intervals.restore();
  });

  it('keeps the newest merge entry when an earlier request resolves after a later one', async () => {
    const first = deferred<unknown>();
    const second = deferred<unknown>();
    vi.mocked(getMergeEntry).mockImplementation(((id: string) => (id === 'mrg_1' ? first.promise : second.promise)) as never);
    render(
      <MemoryRouter initialEntries={['/merge-queue/mrg_1']}>
        <NavigateButton to="/merge-queue/mrg_2" />
        <Routes><Route path="/merge-queue/:id" element={<MergeQueueDetail />} /></Routes>
      </MemoryRouter>,
    );

    fireEvent.click(screen.getByText('go /merge-queue/mrg_2'));
    await act(async () => { second.resolve(entry({ id: 'mrg_2', branchName: 'armada/second-branch' })); });
    expect((await screen.findAllByText('armada/second-branch')).length).toBeGreaterThan(0);

    await act(async () => { first.resolve(entry({ branchName: 'armada/first-branch' })); });
    expect(screen.getAllByText('armada/second-branch').length).toBeGreaterThan(0);
    expect(screen.queryByText('armada/first-branch')).not.toBeInTheDocument();
  });

  it('shows the spinner instead of the previous merge entry while another id loads, and reports its failure', async () => {
    const second = deferred<unknown>();
    vi.mocked(getMergeEntry).mockImplementation(((id: string) => (id === 'mrg_1' ? Promise.resolve(entry({ branchName: 'armada/first-branch' })) : second.promise)) as never);
    render(
      <MemoryRouter initialEntries={['/merge-queue/mrg_1']}>
        <NavigateButton to="/merge-queue/mrg_2" />
        <Routes><Route path="/merge-queue/:id" element={<MergeQueueDetail />} /></Routes>
      </MemoryRouter>,
    );
    expect((await screen.findAllByText('armada/first-branch')).length).toBeGreaterThan(0);

    fireEvent.click(screen.getByText('go /merge-queue/mrg_2'));
    expect(await screen.findByText('Loading...')).toBeInTheDocument();
    expect(screen.queryByText('armada/first-branch')).not.toBeInTheDocument();

    await act(async () => { second.reject(new Error('unavailable')); });
    expect(await screen.findByText('Failed to load merge entry.')).toBeInTheDocument();
    expect(screen.queryByText('armada/first-branch')).not.toBeInTheDocument();
  });
});
