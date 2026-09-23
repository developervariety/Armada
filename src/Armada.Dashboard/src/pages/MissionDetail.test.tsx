import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import MissionDetail from './MissionDetail';
import { NavigateButton, deferred } from '../test/routeRace';
import {
  deleteMission,
  getMission,
  getMissionLandingPreview,
  listCaptains,
  listCheckRuns,
  listDeployments,
  listVessels,
  purgeMission,
} from '../api/client';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  getMission: vi.fn(),
  updateMission: vi.fn(),
  deleteMission: vi.fn(),
  purgeMission: vi.fn(),
  getMissionDiff: vi.fn(),
  getMissionGitHubPullRequest: vi.fn(),
  getMissionLog: vi.fn(),
  getMissionInstructions: vi.fn(),
  getMissionLandingPreview: vi.fn(),
  restartMission: vi.fn(),
  retryMissionLanding: vi.fn(),
  transitionMission: vi.fn(),
  approveMissionReview: vi.fn(),
  denyMissionReview: vi.fn(),
  listCheckRuns: vi.fn(),
  listVessels: vi.fn(),
  listCaptains: vi.fn(),
  listDeployments: vi.fn(),
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

function page<T>(objects: T[]) {
  return { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: objects.length, totalMs: 1, objects };
}

function mission(overrides: Record<string, unknown> = {}) {
  return {
    id: 'msn_1',
    title: 'Port decoder',
    description: null,
    status: 'InProgress',
    requiresReview: false,
    vesselId: null,
    voyageId: null,
    captainId: null,
    branchName: 'armada/msn_1',
    priority: 100,
    createdUtc: '2026-09-15T10:00:00Z',
    lastUpdateUtc: '2026-09-15T10:00:00Z',
    ...overrides,
  };
}

function renderDetail() {
  return render(
    <MemoryRouter initialEntries={['/missions/msn_1']}>
      <Routes>
        <Route path="/missions/:id" element={<MissionDetail />} />
        <Route path="/missions" element={<div>Missions Route</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

async function chooseAction(label: string) {
  fireEvent.click(await screen.findByTitle('Actions'));
  const items = await screen.findAllByRole('button', { name: label });
  fireEvent.click(items[items.length - 1]);
}

describe('MissionDetail', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.mocked(getMissionLandingPreview).mockResolvedValue(null as never);
    vi.mocked(listVessels).mockResolvedValue(page([]) as never);
    vi.mocked(listCaptains).mockResolvedValue(page([]) as never);
    vi.mocked(listCheckRuns).mockResolvedValue(page([]) as never);
    vi.mocked(listDeployments).mockResolvedValue(page([]) as never);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it('offers Land for a WorkProduced mission and Retry Landing for a LandingFailed mission', async () => {
    vi.mocked(getMission).mockResolvedValue(mission({ status: 'WorkProduced' }) as never);
    const { unmount } = renderDetail();
    expect(await screen.findByRole('button', { name: 'Land' })).toBeInTheDocument();
    unmount();

    vi.mocked(getMission).mockResolvedValue(mission({ status: 'LandingFailed' }) as never);
    renderDetail();
    expect(await screen.findByRole('button', { name: 'Retry Landing' })).toBeInTheDocument();
  });

  it('offers neither Land nor Mark Complete for a Review mission, because the server refuses both', async () => {
    vi.mocked(getMission).mockResolvedValue(mission({ status: 'Review', requiresReview: false }) as never);
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'Port decoder' })).toBeInTheDocument();

    fireEvent.click(screen.getByTitle('Actions'));
    expect(screen.queryByRole('button', { name: 'Land' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry Landing' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Mark Complete' })).not.toBeInTheDocument();
  });

  it('labels the DELETE route action Cancel and keeps the cancelled mission on screen', async () => {
    vi.mocked(getMission).mockResolvedValue(mission() as never);
    vi.mocked(deleteMission).mockResolvedValue(undefined as never);
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'Port decoder' })).toBeInTheDocument();

    fireEvent.click(screen.getByTitle('Actions'));
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(await screen.findByText(/remains in the database/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Yes' }));

    await waitFor(() => expect(deleteMission).toHaveBeenCalledWith('msn_1'));
    expect(screen.queryByText('Missions Route')).not.toBeInTheDocument();
  });

  it('leaves the page after a purge instead of reloading the removed mission', async () => {
    vi.mocked(getMission).mockResolvedValue(mission() as never);
    vi.mocked(purgeMission).mockResolvedValue(undefined as never);
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'Port decoder' })).toBeInTheDocument();

    await chooseAction('Purge');
    fireEvent.click(await screen.findByRole('button', { name: 'Yes' }));

    expect(await screen.findByText('Missions Route')).toBeInTheDocument();
    expect(getMission).toHaveBeenCalledTimes(1);
  });

  it('refreshes a running mission on the auto-refresh interval without the loading spinner', async () => {
    localStorage.setItem('armada_autorefresh_mission-detail', '15');
    vi.mocked(getMission).mockResolvedValue(mission() as never);
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'Port decoder' })).toBeInTheDocument();
    expect(getMission).toHaveBeenCalledTimes(1);

    vi.mocked(getMission).mockResolvedValue(mission({ status: 'WorkProduced' }) as never);
    await act(async () => {
      vi.advanceTimersByTime(15000);
    });

    await waitFor(() => expect(getMission).toHaveBeenCalledTimes(2));
    expect(await screen.findByRole('button', { name: 'Land' })).toBeInTheDocument();
    expect(screen.queryByText('Loading...')).not.toBeInTheDocument();
  });

  it('keeps the newest mission when an earlier request resolves after a later one', async () => {
    const first = deferred<unknown>();
    const second = deferred<unknown>();
    vi.mocked(getMission).mockImplementation(((id: string) => (id === 'msn_1' ? first.promise : second.promise)) as never);
    render(
      <MemoryRouter initialEntries={['/missions/msn_1']}>
        <NavigateButton to="/missions/msn_2" />
        <Routes><Route path="/missions/:id" element={<MissionDetail />} /></Routes>
      </MemoryRouter>,
    );

    fireEvent.click(screen.getByText('go /missions/msn_2'));
    await act(async () => { second.resolve(mission({ id: 'msn_2', title: 'Second mission' })); });
    expect(await screen.findByRole('heading', { name: 'Second mission' })).toBeInTheDocument();

    await act(async () => { first.resolve(mission({ title: 'First mission' })); });
    expect(screen.getByRole('heading', { name: 'Second mission' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'First mission' })).not.toBeInTheDocument();
  });

  it('shows the spinner instead of the previous mission while another id loads, and reports its failure', async () => {
    const second = deferred<unknown>();
    vi.mocked(getMission).mockImplementation(((id: string) => (id === 'msn_1' ? Promise.resolve(mission({ title: 'First mission' })) : second.promise)) as never);
    render(
      <MemoryRouter initialEntries={['/missions/msn_1']}>
        <NavigateButton to="/missions/msn_2" />
        <Routes><Route path="/missions/:id" element={<MissionDetail />} /></Routes>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'First mission' })).toBeInTheDocument();

    fireEvent.click(screen.getByText('go /missions/msn_2'));
    expect(await screen.findByText('Loading...')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'First mission' })).not.toBeInTheDocument();

    await act(async () => { second.reject(new Error('gone')); });
    expect(await screen.findByText(/Failed to load mission: gone/)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'First mission' })).not.toBeInTheDocument();
  });
});
