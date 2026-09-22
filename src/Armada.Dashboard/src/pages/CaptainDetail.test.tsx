import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import CaptainDetail from './CaptainDetail';
import { NavigateButton, deferred } from '../test/routeRace';
import { getCaptain, listMissionSummaries, listModelEndpoints, updateCaptain } from '../api/client';

vi.mock('../api/client', () => ({
  createCaptain: vi.fn(),
  getCaptain: vi.fn(),
  getCaptainTools: vi.fn(),
  getCaptainLog: vi.fn(),
  listModelEndpoints: vi.fn(),
  stopCaptain: vi.fn(),
  quarantineCaptain: vi.fn(),
  unquarantineCaptain: vi.fn(),
  getMission: vi.fn(),
  listMissionSummaries: vi.fn(),
  updateCaptain: vi.fn(),
  deleteCaptain: vi.fn(),
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

const captain = {
  id: 'cpt_1',
  name: 'judge-one',
  runtime: 'ClaudeCode',
  state: 'Idle',
  tier: 'Premium',
  preferenceRank: 2,
  model: null,
  modelEndpointId: null,
  currentMissionId: null,
  createdUtc: '2026-09-01T00:00:00Z',
  lastUpdateUtc: '2026-09-01T00:00:00Z',
  lastHeartbeatUtc: null,
};

function renderDetail() {
  return render(
    <MemoryRouter initialEntries={['/captains/cpt_1']}>
      <Routes>
        <Route path="/captains/:id" element={<CaptainDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('CaptainDetail', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.mocked(getCaptain).mockResolvedValue(captain as never);
    vi.mocked(listMissionSummaries).mockResolvedValue({ success: true, pageNumber: 1, pageSize: 100, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] } as never);
    vi.mocked(listModelEndpoints).mockResolvedValue([
      { id: 'mep_inf', name: 'local-vllm', kind: 'Inference', provider: 'OpenAI', model: null, enabled: true },
    ] as never);
    vi.mocked(updateCaptain).mockResolvedValue(captain as never);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it('says the captain is not found for a 404 instead of a load failure', async () => {
    vi.mocked(getCaptain).mockRejectedValue(Object.assign(new Error('Not found'), { status: 404 }) as never);
    renderDetail();
    expect(await screen.findByText('Captain not found.')).toBeInTheDocument();
    expect(screen.queryByText('Failed to load captain.')).not.toBeInTheDocument();
  });

  it('shows the capability tier badge', async () => {
    renderDetail();
    expect(await screen.findByText('Premium')).toBeInTheDocument();
  });

  it('shows the preference rank and saves an edited rank', async () => {
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'judge-one' })).toBeInTheDocument();
    expect(screen.getByText('Preference rank').nextElementSibling).toHaveTextContent('2');

    fireEvent.click(screen.getByTitle('Actions'));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const form = screen.getByRole('heading', { name: 'Edit Captain' }).closest('form') as HTMLFormElement;
    const rank = screen.getByRole('spinbutton', { name: /^Preference rank/ });
    expect(rank).toHaveValue(2);
    fireEvent.change(rank, { target: { value: '4' } });
    fireEvent.submit(form);

    await waitFor(() => expect(updateCaptain).toHaveBeenCalledTimes(1));
    expect(updateCaptain).toHaveBeenCalledWith('cpt_1', expect.objectContaining({ tier: 'Premium', preferenceRank: 4 }));
  });

  it('refreshes the captain on the auto-refresh interval without the loading spinner', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'judge-one' })).toBeInTheDocument();
    expect(getCaptain).toHaveBeenCalledTimes(1);

    vi.mocked(getCaptain).mockResolvedValue({ ...captain, state: 'Working' } as never);
    await act(async () => {
      vi.advanceTimersByTime(15000);
    });

    await waitFor(() => expect(getCaptain).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Working')).toBeInTheDocument();
    expect(screen.queryByText('Loading...')).not.toBeInTheDocument();
  });

  it('saves the tier and the inference endpoint of an API Endpoint captain', async () => {
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'judge-one' })).toBeInTheDocument();

    fireEvent.click(screen.getByTitle('Actions'));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const form = screen.getByRole('heading', { name: 'Edit Captain' }).closest('form') as HTMLFormElement;
    fireEvent.change(screen.getByRole('combobox', { name: 'Runtime' }), { target: { value: 'ApiEndpoint' } });
    fireEvent.change(await screen.findByRole('combobox', { name: 'Inference Endpoint' }), { target: { value: 'mep_inf' } });
    fireEvent.change(screen.getByRole('combobox', { name: /^Capability tier/ }), { target: { value: 'Economy' } });
    fireEvent.submit(form);

    await waitFor(() => expect(updateCaptain).toHaveBeenCalledTimes(1));
    expect(updateCaptain).toHaveBeenCalledWith('cpt_1', expect.objectContaining({
      runtime: 'ApiEndpoint',
      modelEndpointId: 'mep_inf',
      tier: 'Economy',
    }));
  });

  it('refuses to save a Mux captain without a named Mux endpoint', async () => {
    renderDetail();
    expect(await screen.findByRole('heading', { name: 'judge-one' })).toBeInTheDocument();

    fireEvent.click(screen.getByTitle('Actions'));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const form = screen.getByRole('heading', { name: 'Edit Captain' }).closest('form') as HTMLFormElement;
    fireEvent.change(screen.getByRole('combobox', { name: 'Runtime' }), { target: { value: 'Mux' } });
    fireEvent.submit(form);

    expect(await screen.findByText('Mux captains require a named Mux endpoint.')).toBeInTheDocument();
    expect(updateCaptain).not.toHaveBeenCalled();
  });

  it('keeps the newest captain when an earlier request resolves after a later one', async () => {
    const first = deferred<unknown>();
    const second = deferred<unknown>();
    vi.mocked(getCaptain).mockImplementation(((id: string) => (id === 'cpt_1' ? first.promise : second.promise)) as never);
    render(
      <MemoryRouter initialEntries={['/captains/cpt_1']}>
        <NavigateButton to="/captains/cpt_2" />
        <Routes><Route path="/captains/:id" element={<CaptainDetail />} /></Routes>
      </MemoryRouter>,
    );

    fireEvent.click(screen.getByText('go /captains/cpt_2'));
    await act(async () => { second.resolve({ ...captain, id: 'cpt_2', name: 'second-captain' }); });
    expect(await screen.findByRole('heading', { name: 'second-captain' })).toBeInTheDocument();

    await act(async () => { first.resolve({ ...captain, name: 'first-captain' }); });
    expect(screen.getByRole('heading', { name: 'second-captain' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'first-captain' })).not.toBeInTheDocument();
  });

  it('shows the spinner instead of the previous captain while another id loads, and reports its failure', async () => {
    const second = deferred<unknown>();
    vi.mocked(getCaptain).mockImplementation(((id: string) => (id === 'cpt_1' ? Promise.resolve({ ...captain, name: 'first-captain' }) : second.promise)) as never);
    render(
      <MemoryRouter initialEntries={['/captains/cpt_1']}>
        <NavigateButton to="/captains/cpt_2" />
        <Routes><Route path="/captains/:id" element={<CaptainDetail />} /></Routes>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'first-captain' })).toBeInTheDocument();

    fireEvent.click(screen.getByText('go /captains/cpt_2'));
    expect(await screen.findByText('Loading...')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'first-captain' })).not.toBeInTheDocument();

    await act(async () => { second.reject(Object.assign(new Error('unavailable'), { status: 500 })); });
    expect(await screen.findByText('Failed to load captain.')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'first-captain' })).not.toBeInTheDocument();
  });
});
