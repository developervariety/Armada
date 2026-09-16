import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import RoutingSettings from './RoutingSettings';
import { getSettings, updateSettings } from '../api/client';
vi.mock('../api/client', () => ({ getSettings: vi.fn(), updateSettings: vi.fn(), previewUsageRouting: vi.fn() }));
vi.mock('../context/LocaleContext', () => {
  const locale = { t: (s: string, params?: Record<string, string>) => params ? Object.entries(params).reduce((text, [k, v]) => text.split(`{{${k}}}`).join(v), s) : s };
  return { useLocale: () => locale };
});
vi.mock('../lib/useProxySessionContext', () => ({ useProxySessionContext: () => null }));

function saved(overrides: { midTierModels?: string[]; usageRouting?: Record<string, unknown>; highTierModels?: string[] } = {}) {
  return {
    modelTier: {
      midTierModels: overrides.midTierModels ?? ['mid-a'], highTierModels: overrides.highTierModels ?? ['high-a'], specialistPersonas: [],
      reservedHighTierSlots: 0, preferNonNativeFirst: false, withinTierStrategy: 'Random', withinTierPreferenceOrder: {},
      familyClassificationRules: [], usageRouting: overrides.usageRouting ?? { enabled: false, accounts: [], personaRoutes: {} },
    },
    voyageDispatch: { rejectStagePersonaTitlePrefixes: false, stagePersonaTitlePrefixes: [] },
    modelProviders: { providers: { vilao: { openAiBaseUrl: 'https://example.invalid/v1' } } },
    additionalPromptTemplates: [], additionalPersonas: [], additionalPipelines: [],
    providerUsage: [],
  };
}

const midTier = () => screen.getByTitle('Concrete model ids that classify as mid');
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
    expect(await screen.findByLabelText('Enable Smart Routing')).toBeChecked();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('loads the saved policy and saves only the routing replacement', async () => {
    const policy = { enabled: false, accounts: [], personaRoutes: {} };
    vi.mocked(getSettings).mockResolvedValue(saved({ usageRouting: policy }));
    vi.mocked(updateSettings).mockResolvedValue(saved({ usageRouting: { ...policy, enabled: true } }));
    render(<RoutingSettings />);
    const enable = await screen.findByLabelText('Enable Smart Routing');
    fireEvent.click(enable);
    expect(updateSettings).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Save routing policy'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledWith({ modelTier: { usageRouting: { ...policy, enabled: true } } }));
    expect(await screen.findByRole('status')).toHaveTextContent('Smart Routing settings saved.');
  });

  it('saves only the changed model routing field, never usage routing or model providers', async () => {
    vi.mocked(getSettings).mockResolvedValue(saved());
    vi.mocked(updateSettings).mockResolvedValue(saved({ midTierModels: ['mid-a', 'mid-b'] }));
    render(<RoutingSettings />);
    await screen.findByTitle('Concrete model ids that classify as mid');
    fireEvent.change(midTier(), { target: { value: 'mid-a\nmid-b' } });
    fireEvent.click(screen.getByText('Save model routing'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledTimes(1));
    expect(updateSettings).toHaveBeenCalledWith({ modelTier: { midTierModels: ['mid-a', 'mid-b'] } });
    expect(await screen.findByRole('status')).toHaveTextContent('Routing policy saved.');
  });

  it('keeps unsaved edits in both parts when the auto-refresh reloads settings', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(getSettings).mockResolvedValueOnce(saved());
    vi.mocked(getSettings).mockResolvedValue(saved({ highTierModels: ['high-b'] }));
    render(<RoutingSettings />);
    await waitFor(() => expect(midTier()).toHaveValue('mid-a'));
    fireEvent.change(midTier(), { target: { value: 'mid-a\nmid-b' } });
    fireEvent.change(usagePolicy(), { target: { value: '{"enabled": true}' } });
    await act(async () => { await vi.advanceTimersByTimeAsync(16000); });
    expect(getSettings).toHaveBeenCalledTimes(2);
    expect(midTier()).toHaveValue('mid-a\nmid-b');
    expect(usagePolicy()).toHaveValue('{"enabled": true}');
    expect(screen.getByTitle('Concrete model ids that classify as high')).toHaveValue('high-b');
  });

  it('keeps an unsaved model routing edit when Smart Routing is saved, and the reverse', async () => {
    const policy = { enabled: false, accounts: [], personaRoutes: {} };
    vi.mocked(getSettings).mockResolvedValue(saved({ usageRouting: policy }));
    vi.mocked(updateSettings).mockResolvedValueOnce(saved({ usageRouting: { ...policy, enabled: true } }));
    vi.mocked(updateSettings).mockResolvedValueOnce(saved({ midTierModels: ['mid-z'], usageRouting: { ...policy, enabled: true } }));
    render(<RoutingSettings />);
    await screen.findByTitle('Concrete model ids that classify as mid');
    fireEvent.change(midTier(), { target: { value: 'mid-z' } });
    fireEvent.click(screen.getByLabelText('Enable Smart Routing'));
    fireEvent.click(screen.getByText('Save routing policy'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledTimes(1));
    expect(midTier()).toHaveValue('mid-z');
    const enabledDraft = (usagePolicy() as HTMLTextAreaElement).value;
    fireEvent.click(screen.getByLabelText('Enable Smart Routing'));
    fireEvent.click(screen.getByText('Save model routing'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledTimes(2));
    expect(vi.mocked(updateSettings).mock.calls[1][0]).toEqual({ modelTier: { midTierModels: ['mid-z'] } });
    expect(usagePolicy()).not.toHaveValue(enabledDraft);
    expect(screen.getByLabelText('Enable Smart Routing')).not.toBeChecked();
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
});
