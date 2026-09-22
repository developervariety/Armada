import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import VesselDetail from './VesselDetail';
import { NavigateButton, deferred } from '../test/routeRace';
import {
  getVessel,
  getVesselLandingPreview,
  getVesselReadiness,
  listFleets,
  listMissionSummaries,
  listPipelines,
} from '../api/client';

vi.mock('../api/client', () => ({
  getVessel: vi.fn(),
  listVessels: vi.fn(),
  listFleets: vi.fn(),
  listMissionSummaries: vi.fn(),
  listPipelines: vi.fn(),
  createVessel: vi.fn(),
  updateVessel: vi.fn(),
  deleteVessel: vi.fn(),
  getVesselReadiness: vi.fn(),
  getVesselLandingPreview: vi.fn(),
}));

vi.mock('../components/shared/VesselBranchPanel', () => ({ default: () => null }));
vi.mock('../components/shared/ReadinessPanel', () => ({ default: () => null }));

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

const vessel = {
  id: 'vsl_1',
  name: 'EngineLib',
  fleetId: null,
  repoUrl: 'https://example.test/engine.git',
  defaultBranch: 'main',
  landingMode: 'LocalMerge',
  requirePassingChecksToLand: false,
  requirePullRequestForProtectedBranches: false,
  requireMergeQueueForReleaseBranches: false,
  enableModelContext: true,
  createdUtc: '2026-09-01T00:00:00Z',
  lastUpdateUtc: '2026-09-01T00:00:00Z',
};

function renderDetail() {
  return render(
    <MemoryRouter initialEntries={['/vessels/vsl_1']}>
      <Routes>
        <Route path="/vessels/:id" element={<VesselDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('VesselDetail', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.mocked(getVessel).mockResolvedValue(vessel as never);
    vi.mocked(listFleets).mockResolvedValue(page([]) as never);
    vi.mocked(listMissionSummaries).mockResolvedValue(page([]) as never);
    vi.mocked(listPipelines).mockResolvedValue(page([]) as never);
    vi.mocked(getVesselReadiness).mockResolvedValue(null as never);
    vi.mocked(getVesselLandingPreview).mockResolvedValue(null as never);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it('reads the vessel by id', async () => {
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'EngineLib' })).toBeInTheDocument();
    expect(getVessel).toHaveBeenCalledWith('vsl_1');
  });

  it('says the vessel is not found for a 404', async () => {
    vi.mocked(getVessel).mockRejectedValue(Object.assign(new Error('Not found'), { status: 404 }) as never);
    renderDetail();
    expect(await screen.findByText('Vessel not found.')).toBeInTheDocument();
    expect(screen.queryByText('Failed to load vessel.')).not.toBeInTheDocument();
  });

  it('shows the upstream landing preview pill and checks label', async () => {
    vi.mocked(getVesselLandingPreview).mockResolvedValue({
      sourceBranch: null, targetBranch: 'main', branchCategory: 'Default', landingMode: 'LocalMerge', branchCleanupPolicy: null,
      expectedLandingAction: null, requirePassingChecksToLand: true, targetBranchProtected: false, protectedBranchMatch: null,
      requirePullRequestForProtectedBranches: false, requireMergeQueueForReleaseBranches: false, latestCheckSummary: null,
      isReadyToLand: true, issues: [],
    } as never);
    renderDetail();

    expect(await screen.findByText('Ready To Land')).toBeInTheDocument();
    expect(screen.getByText('Passing checks required')).toBeInTheDocument();
    expect(screen.getByText('Require Passing Checks To Land')).toBeInTheDocument();
    expect(screen.queryByText(/advisory/i)).not.toBeInTheDocument();
  });

  it('refreshes on the auto-refresh interval without the loading spinner', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'EngineLib' })).toBeInTheDocument();
    expect(getVessel).toHaveBeenCalledTimes(1);

    vi.mocked(getVessel).mockResolvedValue({ ...vessel, name: 'EngineLib Renamed' } as never);
    await act(async () => {
      vi.advanceTimersByTime(15000);
    });

    await waitFor(() => expect(getVessel).toHaveBeenCalledTimes(2));
    expect(await screen.findByRole('heading', { name: 'EngineLib Renamed' })).toBeInTheDocument();
    expect(screen.queryByText('Loading...')).not.toBeInTheDocument();
  });

  it('keeps the newest vessel when an earlier request resolves after a later one', async () => {
    const first = deferred<unknown>();
    const second = deferred<unknown>();
    vi.mocked(getVessel).mockImplementation(((id: string) => (id === 'vsl_1' ? first.promise : second.promise)) as never);
    render(
      <MemoryRouter initialEntries={['/vessels/vsl_1']}>
        <NavigateButton to="/vessels/vsl_2" />
        <Routes><Route path="/vessels/:id" element={<VesselDetail />} /></Routes>
      </MemoryRouter>,
    );

    fireEvent.click(screen.getByText('go /vessels/vsl_2'));
    await act(async () => { second.resolve({ ...vessel, id: 'vsl_2', name: 'SecondVessel' }); });
    expect(await screen.findByRole('heading', { name: 'SecondVessel' })).toBeInTheDocument();

    await act(async () => { first.resolve({ ...vessel, name: 'FirstVessel' }); });
    expect(screen.getByRole('heading', { name: 'SecondVessel' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'FirstVessel' })).not.toBeInTheDocument();
  });

  it('shows the spinner instead of the previous vessel while another id loads, and reports its failure', async () => {
    const second = deferred<unknown>();
    vi.mocked(getVessel).mockImplementation(((id: string) => (id === 'vsl_1' ? Promise.resolve({ ...vessel, name: 'FirstVessel' }) : second.promise)) as never);
    render(
      <MemoryRouter initialEntries={['/vessels/vsl_1']}>
        <NavigateButton to="/vessels/vsl_2" />
        <Routes><Route path="/vessels/:id" element={<VesselDetail />} /></Routes>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'FirstVessel' })).toBeInTheDocument();

    fireEvent.click(screen.getByText('go /vessels/vsl_2'));
    expect(await screen.findByText('Loading...')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'FirstVessel' })).not.toBeInTheDocument();

    await act(async () => { second.reject(Object.assign(new Error('unavailable'), { status: 500 })); });
    expect(await screen.findByText('Failed to load vessel.')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'FirstVessel' })).not.toBeInTheDocument();
  });
});
