import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Captains from './Captains';
import { createCaptain, listCaptains, listModelEndpoints, stopAllCaptains } from '../api/client';
import { useNotifications } from '../context/NotificationContext';

vi.mock('../api/client', () => ({
  listCaptains: vi.fn(),
  createCaptain: vi.fn(),
  updateCaptain: vi.fn(),
  deleteCaptain: vi.fn(),
  stopCaptain: vi.fn(),
  stopAllCaptains: vi.fn(),
  restartCaptain: vi.fn(),
  getCaptainTools: vi.fn(),
  listModelEndpoints: vi.fn(),
  quarantineCaptain: vi.fn(),
  unquarantineCaptain: vi.fn(),
}));

const auth = vi.hoisted(() => ({ current: { isAdmin: true, isTenantAdmin: true, user: { user: { id: 'usr_admin', tenantId: 'default' } } } }));
vi.mock('../context/AuthContext', () => ({ useAuth: () => auth.current }));

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

vi.mock('../context/NotificationContext', () => {
  const notifications = { pushToast: vi.fn() };
  return { useNotifications: () => notifications };
});

function page<T>(objects: T[]) {
  return { success: true, pageNumber: 1, pageSize: 9999, totalPages: 1, totalRecords: objects.length, totalMs: 1, objects };
}

const quarantined = {
  id: 'cpt_q',
  name: 'held-captain',
  runtime: 'Codex',
  state: 'Quarantined',
  quarantineReason: 'provider billing hold',
  quarantineUntilUtc: '2026-09-15T12:00:00Z',
  tier: 'Premium',
  createdUtc: '2026-09-01T00:00:00Z',
  lastHeartbeatUtc: null,
};

function renderCaptains() {
  return render(
    <MemoryRouter>
      <Captains />
    </MemoryRouter>,
  );
}

async function openCreateForm() {
  fireEvent.click(await screen.findByRole('button', { name: '+ Captain' }));
  return screen.getByRole('heading', { name: 'Create Captain' }).closest('form') as HTMLFormElement;
}

describe('Captains', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.mocked(listCaptains).mockResolvedValue(page([quarantined]) as never);
    vi.mocked(listModelEndpoints).mockResolvedValue([
      { id: 'mep_inf', name: 'local-vllm', kind: 'Inference', provider: 'OpenAI', model: 'qwen', enabled: true },
      { id: 'mep_emb', name: 'embedder', kind: 'Embedding', provider: 'OpenAI', model: 'e5', enabled: true },
    ] as never);
    vi.mocked(createCaptain).mockResolvedValue({ id: 'cpt_new', name: 'api-captain' } as never);
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('shows a quarantined captain as a stalled tag with the release time and the reason as its tooltip', async () => {
    renderCaptains();
    const tag = await screen.findByText('until rel:2026-09-15T12:00:00Z');
    expect(tag).toHaveClass('tag', 'stalled');
    expect(tag).toHaveAttribute('title', 'provider billing hold');
    expect(screen.getByText('Premium')).toBeInTheDocument();
  });

  it('registers the list auto-refresh timer', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');
    renderCaptains();
    expect(await screen.findByLabelText('Auto-refresh interval')).toHaveValue('15');
    expect(intervalSpy.mock.calls.some(([, delay]) => delay === 15000)).toBe(true);
    intervalSpy.mockRestore();
  });

  it('lists runtimes in upstream order and requires an inference endpoint for an API Endpoint captain', async () => {
    renderCaptains();
    const form = await openCreateForm();
    const runtime = within(form).getByRole('combobox', { name: 'Runtime' }) as HTMLSelectElement;
    expect(Array.from(runtime.options).map((option) => option.value)).toEqual(
      ['', 'ClaudeCode', 'Codex', 'Gemini', 'Cursor', 'Mux', 'OpenCode', 'ApiEndpoint'],
    );

    fireEvent.change(runtime, { target: { value: 'ApiEndpoint' } });
    const endpoint = within(form).getByRole('combobox', { name: 'Inference Endpoint' }) as HTMLSelectElement;
    expect(Array.from(endpoint.options).map((option) => option.value)).toEqual(['', 'mep_inf']);
    // An API-endpoint captain draws credentials from the endpoint, so the inline
    // provider-credential fields must not appear for that runtime.
    expect(within(form).queryByText('Provider Credential')).not.toBeInTheDocument();

    fireEvent.change(within(form).getByRole('textbox', { name: 'Name' }), { target: { value: 'api-captain' } });
    fireEvent.change(endpoint, { target: { value: 'mep_inf' } });
    fireEvent.change(within(form).getByRole('combobox', { name: 'Capability tier' }), { target: { value: 'Standard' } });
    fireEvent.submit(form);

    await waitFor(() => expect(createCaptain).toHaveBeenCalledTimes(1));
    expect(createCaptain).toHaveBeenCalledWith(expect.objectContaining({
      runtime: 'ApiEndpoint',
      modelEndpointId: 'mep_inf',
      tier: 'Standard',
    }));
    // Inline provider credentials are retired: the modal never sends them for any runtime.
    const apiEndpointPayload = vi.mocked(createCaptain).mock.calls[0][0] as Record<string, unknown>;
    expect(apiEndpointPayload).not.toHaveProperty('apiKey');
    expect(apiEndpointPayload).not.toHaveProperty('apiBaseUrl');
  });

  it('sends the preference rank from the captain modal, clamped to its range', async () => {
    renderCaptains();
    const form = await openCreateForm();
    const rank = within(form).getByRole('spinbutton', { name: 'Preference rank' }) as HTMLInputElement;
    expect(rank).toHaveValue(0);
    fireEvent.change(within(form).getByRole('textbox', { name: 'Name' }), { target: { value: 'ranked-captain' } });
    fireEvent.change(within(form).getByRole('combobox', { name: 'Runtime' }), { target: { value: 'ClaudeCode' } });
    fireEvent.change(within(form).getByRole('combobox', { name: 'Capability tier' }), { target: { value: 'Premium' } });
    fireEvent.change(rank, { target: { value: '5000' } });
    fireEvent.submit(form);

    await waitFor(() => expect(createCaptain).toHaveBeenCalledTimes(1));
    expect(createCaptain).toHaveBeenCalledWith(expect.objectContaining({ tier: 'Premium', preferenceRank: 1000 }));
  });

  it('offers an optional inference-endpoint picker for a native runtime and no inline credential fields', async () => {
    renderCaptains();
    const form = await openCreateForm();
    fireEvent.change(within(form).getByRole('combobox', { name: 'Runtime' }), { target: { value: 'ClaudeCode' } });
    // The inline provider-credential fields are retired for every runtime.
    expect(within(form).queryByText('Provider Credential')).not.toBeInTheDocument();
    // A native runtime may optionally resolve its credentials from an inference endpoint; the picker
    // lists only inference endpoints and is not required.
    const endpoint = within(form).getByRole('combobox', { name: 'Inference Endpoint (optional)' }) as HTMLSelectElement;
    expect(Array.from(endpoint.options).map((option) => option.value)).toEqual(['', 'mep_inf']);
    expect(endpoint.required).toBe(false);

    fireEvent.change(within(form).getByRole('textbox', { name: 'Name' }), { target: { value: 'native-endpoint-captain' } });
    fireEvent.change(endpoint, { target: { value: 'mep_inf' } });
    fireEvent.change(within(form).getByRole('combobox', { name: 'Capability tier' }), { target: { value: 'Premium' } });
    fireEvent.submit(form);

    await waitFor(() => expect(createCaptain).toHaveBeenCalledTimes(1));
    expect(createCaptain).toHaveBeenCalledWith(expect.objectContaining({
      runtime: 'ClaudeCode',
      modelEndpointId: 'mep_inf',
    }));
  });

  it('shows Stop All only to a global administrator, because the route acts on every tenant', async () => {
    auth.current = { isAdmin: false, isTenantAdmin: true, user: { user: { id: 'usr_tenant_admin', tenantId: 'ten_a' } } };
    const { unmount } = renderCaptains();
    expect(await screen.findByRole('button', { name: '+ Captain' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Stop All' })).not.toBeInTheDocument();
    unmount();

    auth.current = { isAdmin: true, isTenantAdmin: true, user: { user: { id: 'usr_admin', tenantId: 'default' } } };
    renderCaptains();
    expect(await screen.findByRole('button', { name: 'Stop All' })).toBeInTheDocument();
  });

  it('reports every captain or session Stop All could not stop instead of announcing success', async () => {
    vi.mocked(stopAllCaptains).mockResolvedValue({
      status: 'stopped_with_failures',
      stopped: 2,
      failed: 1,
      captainsStopped: 1,
      captainsFailed: 0,
      planningSessionsStopped: 1,
      planningSessionsFailed: 0,
      refinementSessionsStopped: 0,
      refinementSessionsFailed: 1,
      failures: [{ kind: 'RefinementSession', id: 'ors_stuck', message: 'runtime did not exit' }],
    });
    renderCaptains();
    fireEvent.click(await screen.findByRole('button', { name: 'Stop All' }));
    fireEvent.click(screen.getByRole('button', { name: 'Yes' }));

    expect(await screen.findByText('Stop all stopped 2 and could not stop 1: RefinementSession ors_stuck: runtime did not exit')).toBeInTheDocument();
    expect(useNotifications().pushToast).not.toHaveBeenCalledWith('warning', 'All captains stopped.');
  });

  it('refuses to save a Mux captain without a named Mux endpoint', async () => {
    renderCaptains();
    const form = await openCreateForm();
    fireEvent.change(within(form).getByRole('textbox', { name: 'Name' }), { target: { value: 'mux-captain' } });
    fireEvent.change(within(form).getByRole('combobox', { name: 'Runtime' }), { target: { value: 'Mux' } });
    fireEvent.submit(form);

    expect(await screen.findByText('Mux captains require a named Mux endpoint.')).toBeInTheDocument();
    expect(createCaptain).not.toHaveBeenCalled();
  });
});
