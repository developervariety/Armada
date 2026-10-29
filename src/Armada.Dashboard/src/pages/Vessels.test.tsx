import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Vessels from './Vessels';
import { deleteVessel, getVesselGitStatus, listFleets, listPipelines, listVessels } from '../api/client';
import { deferred } from '../test/routeRace';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listVessels: vi.fn(),
  listFleets: vi.fn(),
  listPipelines: vi.fn(),
  createVessel: vi.fn(),
  updateVessel: vi.fn(),
  deleteVessel: vi.fn(),
  getVesselGitStatus: vi.fn(),
}));

vi.mock('../components/vessels/BuildContextModal', () => ({ default: () => null }));
vi.mock('../components/vessels/CheckoutImport', () => ({ default: () => null }));

const auth = vi.hoisted(() => ({ current: { isAdmin: true, isTenantAdmin: true, user: { user: { id: 'usr_admin', tenantId: 'default' } } } }));
vi.mock('../context/AuthContext', () => ({ useAuth: () => auth.current }));

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

const VESSEL_COUNT = 30;
const PAGE_SIZE = 25;

function vesselRecord(index: number) {
  const number = String(index).padStart(2, '0');
  return {
    id: `vsl_${number}`,
    name: `Vessel ${number}`,
    fleetId: null,
    repoUrl: `https://example.test/${number}.git`,
    defaultBranch: 'main',
    landingMode: null,
    allowConcurrentMissions: false,
    enableModelContext: false,
    createdUtc: '2026-01-01T00:00:00Z',
  };
}

function page<T>(objects: T[]) {
  return { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: objects.length, totalMs: 1, objects };
}

function readIds(): string[] {
  return vi.mocked(getVesselGitStatus).mock.calls.map((call) => call[0]);
}

describe('Vessels', () => {
  beforeEach(() => {
    vi.mocked(listVessels).mockResolvedValue(page(Array.from({ length: VESSEL_COUNT }, (_, index) => vesselRecord(index))) as never);
    vi.mocked(listFleets).mockResolvedValue(page([]) as never);
    vi.mocked(listPipelines).mockResolvedValue(page([]) as never);
    vi.mocked(getVesselGitStatus).mockImplementation(async (id: string) => ({ vesselId: id, commitsAhead: 1, commitsBehind: 0 }));
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('reads the sync status of the visible rows only, and the next page when it is shown', async () => {
    render(<MemoryRouter><Vessels /></MemoryRouter>);

    await waitFor(() => expect(getVesselGitStatus).toHaveBeenCalledTimes(PAGE_SIZE));
    const first = readIds();
    expect(new Set(first).size).toBe(PAGE_SIZE);
    expect(first).not.toContain('vsl_29');
    expect(await screen.findAllByText('1 ahead')).toHaveLength(PAGE_SIZE);

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(getVesselGitStatus).toHaveBeenCalledTimes(VESSEL_COUNT));
    expect(readIds().slice(PAGE_SIZE).sort()).toEqual(['vsl_25', 'vsl_26', 'vsl_27', 'vsl_28', 'vsl_29']);
  });

  it('keeps a progress dialog open while a bulk vessel delete runs and closes it when the deletes finish', async () => {
    const pendingDelete = deferred<void>();
    vi.mocked(deleteVessel).mockReturnValue(pendingDelete.promise as never);
    render(<MemoryRouter><Vessels /></MemoryRouter>);

    await screen.findByText('Vessel 00');
    fireEvent.click(screen.getAllByRole('checkbox')[1]);
    fireEvent.click(screen.getByRole('button', { name: /Delete Selected/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Yes' }));

    expect(await screen.findByText('This may take some time.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Dismiss' })).toBeNull();
    pendingDelete.resolve();
    await waitFor(() => expect(screen.queryByText('This may take some time.')).toBeNull());
    expect(deleteVessel).toHaveBeenCalledTimes(1);
  });
});
