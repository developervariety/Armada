import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Missions from './Missions';
import { getMission, createMission, listCaptains, listMissionSummaries, listVessels, listVoyages } from '../api/client';
import type { MissionSummary } from '../types/models';

const translate = (text: string, params?: Record<string, string | number | null | undefined>) => {
  if (!params) return text;
  return Object.entries(params).reduce(
    (current, [key, value]) => current.split(`{{${key}}}`).join(value == null ? '' : String(value)),
    text,
  );
};
const localeValue = { t: translate, formatDateTime: (v: string | null | undefined) => v ?? '', formatRelativeTime: (v: string | null | undefined) => v ?? '' };
const notifications = { pushToast: vi.fn() };

vi.mock('../api/client', () => ({
  listMissionSummaries: vi.fn(),
  getMission: vi.fn(),
  listVessels: vi.fn(),
  listCaptains: vi.fn(),
  listVoyages: vi.fn(),
  createMission: vi.fn(),
  updateMission: vi.fn(),
  deleteMission: vi.fn(),
  purgeMission: vi.fn(),
  restartMission: vi.fn(),
  retryMissionLanding: vi.fn(),
  transitionMission: vi.fn(),
  getMissionDiff: vi.fn(),
  getMissionLog: vi.fn(),
}));
vi.mock('../context/LocaleContext', () => ({ useLocale: () => localeValue }));
vi.mock('../context/NotificationContext', () => ({ useNotifications: () => notifications }));
vi.mock('../lib/useAutoRefresh', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lib/useAutoRefresh')>()),
  useAutoRefresh: () => ({ seconds: 30, setSeconds: vi.fn() }),
}));
vi.mock('../components/shared/UserScopeFilter', () => ({ default: () => null }));

const BRANCH = 'armada/captain-1/msn_1-a-very-long-branch-name';
const mission = {
  id: 'msn_1', tenantId: null, userId: null, voyageId: 'vyg_1', vesselId: null, captainId: null, title: 'Fix the login flow',
  status: 'InProgress', priority: 100, branchName: BRANCH, createdUtc: '2026-10-01T00:00:00Z',
} as unknown as MissionSummary;

function page<T>(objects: T[]) {
  return { success: true, pageNumber: 1, pageSize: 25, totalPages: 1, totalRecords: objects.length, totalMs: 1, objects } as never;
}

describe('Missions table', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.mocked(listMissionSummaries).mockResolvedValue(page([mission]));
    vi.mocked(listVessels).mockResolvedValue(page([]));
    vi.mocked(listCaptains).mockResolvedValue(page([]));
    vi.mocked(listVoyages).mockResolvedValue(page([]));
  });

  it('puts refresh controls in the table toolbar and locks Title and ID in the column chooser', async () => {
    const { container } = render(<MemoryRouter><Missions /></MemoryRouter>);
    await screen.findByText('Fix the login flow');
    const bar = container.querySelector('.data-table .pagination-bar') as HTMLElement;
    expect(within(bar).getByText('1 record')).toBeInTheDocument();
    expect(within(bar).getByLabelText('Auto-refresh interval')).toBeInTheDocument();
    expect(within(bar).getByTitle('Reload the mission list from the server now.')).toBeInTheDocument();
    expect(container.querySelector('.page-header .auto-refresh-select')).toBeNull();

    await act(async () => { fireEvent.click(within(bar).getByRole('button', { name: /^Columns/ })); });
    const menu = screen.getByRole('menu', { name: 'Choose visible columns' });
    expect(within(menu).getByRole('menuitemcheckbox', { name: /^Title/ })).toHaveAttribute('aria-disabled', 'true');
    expect(within(menu).getByRole('menuitemcheckbox', { name: /^ID/ })).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(within(menu).getByRole('menuitemcheckbox', { name: /^Branch/ }));
    expect(screen.queryByText(BRANCH)).not.toBeInTheDocument();
  });

  it('keeps the branch on one line with the full name in the tooltip', async () => {
    render(<MemoryRouter><Missions /></MemoryRouter>);
    const branch = await screen.findByText(BRANCH);
    expect(branch).toHaveClass('url-value');
    expect(branch.closest('td')).toHaveClass('table-url-cell');
    expect(branch.closest('td')).toHaveAttribute('title', BRANCH);
  });
});


describe('Mission duplication', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(listMissionSummaries).mockResolvedValue(page([mission]));
    vi.mocked(listVessels).mockResolvedValue(page([{ id: 'vsl_1', name: 'Vessel' }]));
    vi.mocked(listCaptains).mockResolvedValue(page([]));
    vi.mocked(listVoyages).mockResolvedValue(page([]));
    vi.mocked(getMission).mockResolvedValue({ ...mission, vesselId: 'vsl_1', mode: 'Audit', description: 'Complete untruncated instructions', persona: 'Judge', requiresReview: true, reviewDenyAction: 'FailPipeline' } as never);
    vi.mocked(createMission).mockResolvedValue({ ...mission, id: 'msn_copy' } as never);
  });

  it('loads full instructions and creates an edited copy without modifying the original execution', async () => {
    render(<MemoryRouter><Missions /></MemoryRouter>);
    await screen.findByText(mission.title);
    fireEvent.click(screen.getByRole('button', { name: 'Actions' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Duplicate' }));
    await screen.findByRole('heading', { name: 'Duplicate Mission' });
    expect(screen.getByLabelText('Description')).toHaveValue('Complete untruncated instructions');
    expect(createMission).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Independent audit' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Copy' }));
    await vi.waitFor(() => expect(createMission).toHaveBeenCalledWith(expect.objectContaining({ title: 'Independent audit', description: 'Complete untruncated instructions', mode: 'Audit', persona: 'Judge' })));
    const payload = vi.mocked(createMission).mock.calls[0][0];
    expect(payload).not.toHaveProperty('id'); expect(payload).not.toHaveProperty('branchName'); expect(payload).not.toHaveProperty('voyageId');
  });

  it('related-table links open a cancellable draft without creating anything', async () => {
    render(<MemoryRouter initialEntries={['/missions?duplicate=msn_1']}><Missions /></MemoryRouter>);
    await screen.findByRole('heading', { name: 'Duplicate Mission' });
    expect(getMission).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(createMission).not.toHaveBeenCalled();
  });
});
