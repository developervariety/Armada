import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Missions from './Missions';
import { listCaptains, listMissionSummaries, listVessels } from '../api/client';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listMissionSummaries: vi.fn(),
  createMission: vi.fn(),
  updateMission: vi.fn(),
  deleteMission: vi.fn(),
  purgeMission: vi.fn(),
  restartMission: vi.fn(),
  retryMissionLanding: vi.fn(),
  transitionMission: vi.fn(),
  getMissionDiff: vi.fn(),
  getMissionLog: vi.fn(),
  listVessels: vi.fn(),
  listCaptains: vi.fn(),
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

function summary(id: string, title: string, createdUtc: string) {
  return { id, title, status: 'Pending', priority: 100, vesselId: null, captainId: null, voyageId: null, branchName: null, createdUtc };
}

// Two server pages at the page cap: the newest page holds no match, the older page holds the only one.
const serverPages = [
  [summary('msn_new', 'Refresh docs', '2026-09-15T10:00:00Z')],
  [summary('msn_old', 'Port Alpha decoder', '2026-09-01T10:00:00Z')],
];

function renderMissions() {
  return render(
    <MemoryRouter>
      <Missions />
    </MemoryRouter>,
  );
}

describe('Missions list', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.mocked(listVessels).mockResolvedValue({ objects: [] } as never);
    vi.mocked(listCaptains).mockResolvedValue({ objects: [] } as never);
    vi.mocked(listMissionSummaries).mockImplementation(async (params) => {
      const pageNumber = params?.pageNumber ?? 1;
      if ((params?.pageSize ?? 0) < 1000) {
        return { success: true, pageNumber: 1, pageSize: params?.pageSize ?? 25, totalPages: 2, totalRecords: 2, totalMs: 1, objects: serverPages[0] } as never;
      }
      return { success: true, pageNumber, pageSize: 1000, totalPages: 2, totalRecords: 2, totalMs: 1, objects: serverPages[pageNumber - 1] } as never;
    });
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('offers the LandingFailed and PullRequestOpen statuses in the status filter', async () => {
    renderMissions();
    const filter = await screen.findByTitle('Filter by status') as HTMLSelectElement;
    const values = Array.from(filter.options).map((option) => option.value);
    expect(values).toContain('LandingFailed');
    expect(values).toContain('PullRequestOpen');
  });

  it('pages on the server in creation order and does not fetch voyages', async () => {
    renderMissions();
    expect(await screen.findByText('Refresh docs')).toBeInTheDocument();
    expect(listMissionSummaries).toHaveBeenCalledWith(expect.objectContaining({
      pageNumber: 1,
      pageSize: 25,
      filters: { order: 'CreatedDescending' },
    }));
  });

  it('finds a title match outside the current server page', async () => {
    renderMissions();
    expect(await screen.findByText('Refresh docs')).toBeInTheDocument();

    fireEvent.change(screen.getAllByPlaceholderText('Search...')[0], { target: { value: 'alpha' } });

    expect(await screen.findByText('Port Alpha decoder')).toBeInTheDocument();
    expect(screen.queryByText('Refresh docs')).not.toBeInTheDocument();
    await waitFor(() => expect(listMissionSummaries).toHaveBeenCalledWith(expect.objectContaining({ pageNumber: 2, pageSize: 1000 })));
  });

  it('sorts by title across every mission, not only the current server page', async () => {
    renderMissions();
    expect(await screen.findByText('Refresh docs')).toBeInTheDocument();

    fireEvent.click(screen.getByTitle('Mission title -- click to sort'));

    expect(await screen.findByText('Port Alpha decoder')).toBeInTheDocument();
    const titles = screen.getAllByRole('row').slice(2).map((row) => row.querySelector('strong')?.textContent).filter(Boolean);
    expect(titles).toEqual(['Port Alpha decoder', 'Refresh docs']);
  });
});
