import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import RoutingSettings from './RoutingSettings';
import { getSettings, updateSettings } from '../api/client';
vi.mock('../api/client', () => ({ getSettings: vi.fn(), updateSettings: vi.fn(), previewUsageRouting: vi.fn() }));
vi.mock('../context/LocaleContext', () => ({ useLocale: () => ({ t: (s: string) => s }) }));
vi.mock('../lib/useProxySessionContext', () => ({ useProxySessionContext: () => null }));

describe('Routing settings page', () => {
  it('does not expose an empty replacement after a load failure and permits retry', async () => {
    vi.mocked(getSettings).mockRejectedValueOnce(new Error('Request timed out'));
    vi.mocked(getSettings).mockResolvedValueOnce({ modelTier: { usageRouting: { enabled: true, accounts: [], personaRoutes: {} } }, providerUsage: [] });
    render(<RoutingSettings />);
    expect(await screen.findByRole('alert')).toHaveTextContent('Request timed out');
    expect(screen.queryByText('Save routing policy')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Account and persona policy (JSON)')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('Retry loading settings'));
    expect(await screen.findByLabelText('Enable usage-aware routing')).toBeChecked();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('loads the saved policy and saves only the routing replacement', async () => {
    const policy = { enabled: false, accounts: [], personaRoutes: {} };
    vi.mocked(getSettings).mockResolvedValue({ modelTier: { usageRouting: policy }, providerUsage: [] });
    vi.mocked(updateSettings).mockResolvedValue({ modelTier: { usageRouting: { ...policy, enabled: true } }, providerUsage: [] });
    render(<RoutingSettings />);
    const enable = await screen.findByLabelText('Enable usage-aware routing');
    fireEvent.click(enable);
    expect(updateSettings).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Save routing policy'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledWith({ modelTier: { usageRouting: { ...policy, enabled: true } } }));
    expect(await screen.findByRole('status')).toHaveTextContent('Routing V2 settings saved.');
  });
});
