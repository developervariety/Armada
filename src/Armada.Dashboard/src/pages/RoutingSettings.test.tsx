import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import RoutingSettings from './RoutingSettings';
import { getSettings, listCaptains, listPersonas, updateSettings } from '../api/client';
vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  getSettings: vi.fn(), updateSettings: vi.fn(), previewUsageRouting: vi.fn(),
  listCaptains: vi.fn().mockResolvedValue({ objects: [] }), listPersonas: vi.fn().mockResolvedValue({ objects: [] }), createCaptain: vi.fn(), createAccountHome: vi.fn(),
  startAccountLogin: vi.fn(), submitAccountLoginCode: vi.fn(), submitAccountLoginKey: vi.fn(),
  getAccountLoginStatus: vi.fn(), cancelAccountLogin: vi.fn(),
}));
vi.mock('../context/LocaleContext', () => {
  const locale = { t: (s: string, params?: Record<string, string>) => params ? Object.entries(params).reduce((text, [k, v]) => text.split(`{{${k}}}`).join(v), s) : s };
  return { useLocale: () => locale };
});
vi.mock('../lib/useProxySessionContext', () => ({ useProxySessionContext: () => null }));

function saved(overrides: { reservedHighTierSlots?: number; usageRouting?: Record<string, unknown>; preferNonNativeFirst?: boolean } = {}) {
  return {
    modelTier: {
      reservedHighTierSlots: overrides.reservedHighTierSlots ?? 0, preferNonNativeFirst: overrides.preferNonNativeFirst ?? false,
      usageRouting: overrides.usageRouting ?? { enabled: false, accounts: [], personaRoutes: {} },
    },
    voyageDispatch: { rejectStagePersonaTitlePrefixes: false, stagePersonaTitlePrefixes: [] },
    modelProviders: { providers: { vilao: { openAiBaseUrl: 'https://example.invalid/v1' } } },
    additionalPromptTemplates: [], additionalPersonas: [], additionalPipelines: [],
    providerUsage: [],
  };
}

const reservedSlots = () => screen.getByTitle('Idle Premium slots held for specialist work (0 disables)');
const usagePolicy = () => screen.getByLabelText('Account and persona policy (JSON)');

describe('Routing settings page', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.mocked(getSettings).mockReset();
    vi.mocked(updateSettings).mockReset();
  });
  afterEach(() => vi.useRealTimers());

  it('does not expose an empty replacement after a load failure and permits retry', async () => {
    vi.mocked(getSettings).mockRejectedValueOnce(new Error('Request timed out'));
    vi.mocked(getSettings).mockResolvedValueOnce(saved({ usageRouting: { enabled: true, accounts: [], personaRoutes: {} } }));
    render(<RoutingSettings />);
    expect(await screen.findByRole('alert')).toHaveTextContent('Request timed out');
    expect(screen.queryByText('Save routing policy')).not.toBeInTheDocument();
    expect(screen.queryByText('Save model routing')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Account and persona policy (JSON)')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('Retry loading settings'));
    expect(await screen.findByLabelText('Smart Routing')).toBeChecked();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('loads the saved policy and saves only the routing replacement', async () => {
    const policy = { enabled: false, accounts: [], personaRoutes: {} };
    vi.mocked(getSettings).mockResolvedValue(saved({ usageRouting: policy }));
    vi.mocked(updateSettings).mockResolvedValue(saved({ usageRouting: { ...policy, enabled: true } }));
    render(<RoutingSettings />);
    const enable = await screen.findByLabelText('Smart Routing');
    fireEvent.click(enable);
    expect(updateSettings).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Save routing policy'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledWith({ modelTier: { usageRouting: { ...policy, enabled: true } } }));
    expect(await screen.findByRole('status')).toHaveTextContent('Smart Routing settings saved.');
  });

  it('saves only the changed model routing field, never usage routing or model providers', async () => {
    vi.mocked(getSettings).mockResolvedValue(saved());
    vi.mocked(updateSettings).mockResolvedValue(saved({ reservedHighTierSlots: 2 }));
    render(<RoutingSettings />);
    await screen.findByTitle('Idle Premium slots held for specialist work (0 disables)');
    fireEvent.change(reservedSlots(), { target: { value: '2' } });
    fireEvent.click(screen.getByText('Save model routing'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledTimes(1));
    expect(updateSettings).toHaveBeenCalledWith({ modelTier: { reservedHighTierSlots: 2 } });
    expect(await screen.findByRole('status')).toHaveTextContent('Routing policy saved.');
  });

  it('shows no retired tier list, strategy, preference order, family rule, or specialist persona field', async () => {
    vi.mocked(getSettings).mockResolvedValue(saved());
    render(<RoutingSettings />);
    await screen.findByTitle('Idle Premium slots held for specialist work (0 disables)');
    expect(screen.queryByText('Mid-tier models (one per line)')).not.toBeInTheDocument();
    expect(screen.queryByText('High-tier models (one per line)')).not.toBeInTheDocument();
    expect(screen.queryByText('Specialist personas (one per line)')).not.toBeInTheDocument();
    expect(screen.queryByText('Within-tier strategy')).not.toBeInTheDocument();
    expect(screen.queryByText('Within-tier preference order (JSON object)')).not.toBeInTheDocument();
    expect(screen.queryByText('Family classification rules (JSON array of {pattern, tier})')).not.toBeInTheDocument();
  });

  it('keeps unsaved edits in both parts when the auto-refresh reloads settings', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(getSettings).mockResolvedValueOnce(saved());
    vi.mocked(getSettings).mockResolvedValue(saved({ preferNonNativeFirst: true }));
    render(<RoutingSettings />);
    await waitFor(() => expect(reservedSlots()).toHaveValue(0));
    fireEvent.change(reservedSlots(), { target: { value: '3' } });
    fireEvent.change(usagePolicy(), { target: { value: '{"enabled": true}' } });
    await act(async () => { await vi.advanceTimersByTimeAsync(16000); });
    expect(getSettings).toHaveBeenCalledTimes(2);
    expect(reservedSlots()).toHaveValue(3);
    expect(usagePolicy()).toHaveValue('{"enabled": true}');
    expect(screen.getByLabelText('Prefer non-native captains first')).toBeChecked();
  });

  it('keeps an unsaved model routing edit when Smart Routing is saved, and the reverse', async () => {
    const policy = { enabled: false, accounts: [], personaRoutes: {} };
    vi.mocked(getSettings).mockResolvedValue(saved({ usageRouting: policy }));
    vi.mocked(updateSettings).mockResolvedValueOnce(saved({ usageRouting: { ...policy, enabled: true } }));
    vi.mocked(updateSettings).mockResolvedValueOnce(saved({ reservedHighTierSlots: 5, usageRouting: { ...policy, enabled: true } }));
    render(<RoutingSettings />);
    await screen.findByTitle('Idle Premium slots held for specialist work (0 disables)');
    fireEvent.change(reservedSlots(), { target: { value: '5' } });
    fireEvent.click(screen.getByLabelText('Smart Routing'));
    fireEvent.click(screen.getByText('Save routing policy'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledTimes(1));
    expect(reservedSlots()).toHaveValue(5);
    const enabledDraft = (usagePolicy() as HTMLTextAreaElement).value;
    fireEvent.click(screen.getByLabelText('Legacy Routing'));
    fireEvent.click(screen.getByText('Save model routing'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledTimes(2));
    expect(vi.mocked(updateSettings).mock.calls[1][0]).toEqual({ modelTier: { reservedHighTierSlots: 5 } });
    expect(usagePolicy()).not.toHaveValue(enabledDraft);
    expect(screen.getByLabelText('Smart Routing')).not.toBeChecked();
  });

  it('reports invalid JSON by field and sends nothing', async () => {
    vi.mocked(getSettings).mockResolvedValue(saved());
    render(<RoutingSettings />);
    const providers = await screen.findByTitle('Model provider definitions');
    fireEvent.change(providers, { target: { value: '{ not json' } });
    fireEvent.click(screen.getByText('Save model routing'));
    expect(await screen.findByRole('alert')).toHaveTextContent('modelProviders is not valid JSON.');
    expect(updateSettings).not.toHaveBeenCalled();
  });
  it('edits persona model lists in a table and saves the personaModels policy shape', async () => {
    const policy = { enabled: true, accounts: [], personaRoutes: {} };
    vi.mocked(listPersonas).mockResolvedValue({ objects: [{ name: 'Worker', active: true }, { name: 'Judge', active: true }] } as never);
    vi.mocked(listCaptains).mockResolvedValue({ objects: [
      { id: 'cpt_a', name: 'alpha', model: 'model-x' }, { id: 'cpt_b', name: 'beta', model: 'model-x' }, { id: 'cpt_c', name: 'gamma', model: 'model-y' },
    ] } as never);
    vi.mocked(getSettings).mockResolvedValue(saved({ usageRouting: policy }));
    vi.mocked(updateSettings).mockImplementation(async (body) => saved({ usageRouting: (body as { modelTier: { usageRouting: Record<string, unknown> } }).modelTier.usageRouting }) as never);
    render(<RoutingSettings />);
    const addDefault = await screen.findByLabelText('Add model to Worker Default');
    expect(screen.getByTestId('persona-models-Judge')).toBeInTheDocument();
    expect(within(addDefault).getByRole('option', { name: 'model-x (2)' })).toBeInTheDocument();
    expect(within(addDefault).getByRole('option', { name: 'model-y (1)' })).toBeInTheDocument();
    expect(within(addDefault).getAllByRole('option')).toHaveLength(3);
    fireEvent.change(addDefault, { target: { value: 'model-x' } });
    fireEvent.change(screen.getByLabelText('Add model to Worker Stronger'), { target: { value: 'model-y' } });
    expect(within(screen.getByTestId('persona-models-Worker')).getByText('2 captains')).toBeInTheDocument();
    expect(JSON.parse((usagePolicy() as HTMLTextAreaElement).value).personaModels).toEqual({ Worker: { default: ['model-x'], lighter: [], stronger: ['model-y'] } });
    fireEvent.click(screen.getByText('Save routing policy'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledWith({ modelTier: { usageRouting: {
      ...policy, personaModels: { Worker: { default: ['model-x'], lighter: [], stronger: ['model-y'] } },
    } } }));
    fireEvent.click(screen.getByLabelText('Remove model lists for Worker'));
    expect(JSON.parse((usagePolicy() as HTMLTextAreaElement).value).personaModels).toEqual({});
  });
});
