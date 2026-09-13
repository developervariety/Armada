import { act, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Dashboard from './Dashboard';
import {
  getStatus,
  getVoyageMissionSummary,
  listCaptains,
  listFleets,
  listMissionSummaries,
  listMissions,
  listVessels,
} from '../api/client';

type WebSocketHandler = (message: unknown) => void;
const socketHandlers: WebSocketHandler[] = [];

vi.mock('../api/client', () => ({
  getStatus: vi.fn(),
  listMissions: vi.fn(),
  listMissionSummaries: vi.fn(),
  getVoyageMissionSummary: vi.fn(),
  getMission: vi.fn(),
  listVessels: vi.fn(),
  listCaptains: vi.fn(),
  listSignals: vi.fn(),
  listFleets: vi.fn(),
  deleteMission: vi.fn(),
  restartMission: vi.fn(),
}));

// The real providers return stable functions (useCallback), so the mocks return the same objects on every render.
vi.mock('../context/WebSocketContext', () => {
  const socket = {
    connected: true,
    send: () => undefined,
    subscribe: (handler: WebSocketHandler) => {
      socketHandlers.push(handler);
      return () => {
        const index = socketHandlers.indexOf(handler);
        if (index >= 0) socketHandlers.splice(index, 1);
      };
    },
  };
  return { useWebSocket: () => socket };
});

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

vi.mock('../components/MissionHistoryChart', () => ({
  default: () => null,
}));

function page<T>(objects: T[]) {
  return { success: true, pageNumber: 1, pageSize: 10, totalPages: 1, totalRecords: objects.length, totalMs: 1, objects };
}

const baseStatus = {
  totalCaptains: 1,
  idleCaptains: 1,
  workingCaptains: 0,
  stalledCaptains: 0,
  activeVoyages: 1,
  missionsByStatus: { Complete: 3 },
  voyages: [
    { voyage: { id: 'vyg_older', title: 'Older voyage', status: 'InProgress' }, totalMissions: 2, completedMissions: 1, failedMissions: 0 },
  ],
  recentSignals: [],
};

function renderDashboard() {
  return render(
    <MemoryRouter>
      <Dashboard />
    </MemoryRouter>,
  );
}

describe('Dashboard home', () => {
  beforeEach(() => {
    localStorage.clear();
    socketHandlers.length = 0;
    vi.mocked(getStatus).mockResolvedValue(baseStatus as never);
    vi.mocked(listMissions).mockResolvedValue(page([]) as never);
    vi.mocked(listMissionSummaries).mockResolvedValue(page([
      { id: 'msn_recent', title: 'Recent mission', status: 'Complete', voyageId: 'vyg_recent', vesselId: 'vsl_alpha', captainId: null, createdUtc: '2026-09-13T10:00:00Z' },
    ]) as never);
    vi.mocked(getVoyageMissionSummary).mockResolvedValue({
      statusCounts: { Complete: 1, InProgress: 1 },
      vessels: page(['vsl_beta']),
    } as never);
    vi.mocked(listVessels).mockResolvedValue(page([
      { id: 'vsl_alpha', name: 'Alpha vessel' },
      { id: 'vsl_beta', name: 'Beta vessel' },
    ]) as never);
    vi.mocked(listCaptains).mockResolvedValue(page([]) as never);
    vi.mocked(listFleets).mockResolvedValue(page([]) as never);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it('loads a bounded page of mission summaries instead of full missions', async () => {
    renderDashboard();
    expect(await screen.findByText('Recent mission')).toBeInTheDocument();

    expect(listMissions).not.toHaveBeenCalled();
    expect(listMissionSummaries).toHaveBeenCalledWith(expect.objectContaining({ pageSize: 10 }));
  });

  it('names voyage vessels from the voyage mission summary, not from the recent mission slice', async () => {
    renderDashboard();

    expect(await screen.findByText('Older voyage')).toBeInTheDocument();
    // The vessel filter also lists every vessel name, so match only the voyage table cell.
    await waitFor(() => {
      const cells = screen.getAllByText('Beta vessel').filter((element) => element.tagName === 'TD');
      expect(cells).toHaveLength(1);
    });
    expect(getVoyageMissionSummary).toHaveBeenCalledWith('vyg_older', expect.objectContaining({ pageSize: 100 }));
  });

  it('coalesces WebSocket bursts during an in-flight load into one follow-up load', async () => {
    let releaseFirst: (value: unknown) => void = () => undefined;
    vi.mocked(getStatus)
      .mockImplementationOnce(() => new Promise((resolve) => { releaseFirst = resolve; }) as never)
      .mockResolvedValue(baseStatus as never);

    renderDashboard();
    await waitFor(() => expect(getStatus).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(socketHandlers.length).toBeGreaterThan(0));

    act(() => {
      for (let i = 0; i < 5; i++) socketHandlers.forEach((handler) => handler({ type: 'mission.changed' }));
    });
    await act(async () => {
      releaseFirst(baseStatus);
    });

    await waitFor(() => expect(getStatus).toHaveBeenCalledTimes(2));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(getStatus).toHaveBeenCalledTimes(2);
  });

  it('registers no refresh timer when auto-refresh is set to None', async () => {
    localStorage.setItem('armada_autorefresh_dashboard', '0');
    const intervalSpy = vi.spyOn(window, 'setInterval');

    renderDashboard();
    expect(await screen.findByLabelText('Auto-refresh interval')).toHaveValue('0');

    const refreshTimers = intervalSpy.mock.calls.filter(([, delay]) => typeof delay === 'number' && delay >= 1000);
    expect(refreshTimers).toHaveLength(0);
    intervalSpy.mockRestore();
  });

  it('keeps a 30-second home refresh by default', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');

    renderDashboard();
    expect(await screen.findByLabelText('Auto-refresh interval')).toHaveValue('30');

    const refreshTimers = intervalSpy.mock.calls.filter(([, delay]) => typeof delay === 'number' && delay >= 1000);
    expect(refreshTimers.map(([, delay]) => delay)).toEqual([30000]);
    intervalSpy.mockRestore();
  });
});
