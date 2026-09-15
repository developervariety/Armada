import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import Server from './Server';
import { getSettings, updateSettings } from '../api/client';

vi.mock('../api/client', () => ({
  getHealth: vi.fn().mockResolvedValue({ status: 'healthy', uptime: '1m', version: 'test', ports: { admiral: 7890, mcp: 7891, webSocket: 7892 } }),
  getSettings: vi.fn(),
  updateSettings: vi.fn(),
  stopServer: vi.fn(),
  resetServer: vi.fn(),
  downloadBackup: vi.fn(),
  restoreBackup: vi.fn(),
  getProxySessionContext: vi.fn().mockResolvedValue(null),
}));
vi.mock('../context/WebSocketContext', () => ({ useWebSocket: () => ({ connected: true }) }));
vi.mock('../context/NotificationContext', () => {
  const notifications = { pushToast: vi.fn() };
  return { useNotifications: () => notifications };
});
vi.mock('../context/AuthContext', () => ({ useAuth: () => ({ isAdmin: true, isTenantAdmin: true }) }));
vi.mock('../context/LocaleContext', () => {
  const locale = { t: (text: string) => text, formatDateTime: (value: string) => value };
  return { useLocale: () => locale };
});

function saved(maxCaptains: number, heartbeatIntervalSeconds = 30) {
  return {
    admiralPort: 7890, mcpPort: 7891, maxCaptains, heartbeatIntervalSeconds, stallThresholdMinutes: 10,
    idleCaptainTimeoutSeconds: 0, planningSessionInactivityTimeoutMinutes: 30, planningSessionAbandonmentTimeoutMinutes: 60,
    planningSessionRetentionDays: 7, autoCreatePr: false, dataDirectory: '/data', databasePath: '/data/armada.db',
    logDirectory: '/data/logs', docksDirectory: '/data/docks', reposDirectory: '/data/repos',
    remoteControl: { enabled: false },
  };
}

function maxCaptainsInput(): HTMLInputElement {
  return screen.getByTitle('Maximum captains (0 = unlimited)') as HTMLInputElement;
}

function heartbeatInput(): HTMLInputElement {
  return screen.getByTitle('Health check interval, minimum 5 seconds') as HTMLInputElement;
}

describe('Server settings drafts', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.mocked(getSettings).mockReset();
    vi.mocked(updateSettings).mockReset();
  });
  afterEach(() => vi.useRealTimers());

  it('keeps an unsaved edit when the auto-refresh reloads settings', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(getSettings).mockResolvedValue(saved(4) as never);
    render(<Server />);
    await waitFor(() => expect(maxCaptainsInput()).toHaveValue(4));
    fireEvent.change(maxCaptainsInput(), { target: { value: '9' } });
    await act(async () => { await vi.advanceTimersByTimeAsync(16000); });
    expect(getSettings).toHaveBeenCalledTimes(2);
    expect(maxCaptainsInput()).toHaveValue(9);
  });

  it('adopts a server change to a field the operator did not edit', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(getSettings).mockResolvedValueOnce(saved(4, 30) as never);
    vi.mocked(getSettings).mockResolvedValue(saved(4, 45) as never);
    render(<Server />);
    await waitFor(() => expect(maxCaptainsInput()).toHaveValue(4));
    fireEvent.change(maxCaptainsInput(), { target: { value: '9' } });
    await act(async () => { await vi.advanceTimersByTimeAsync(16000); });
    expect(heartbeatInput()).toHaveValue(45);
    expect(maxCaptainsInput()).toHaveValue(9);
  });

  it('keeps another section edit when one section is saved', async () => {
    vi.mocked(getSettings).mockResolvedValue(saved(4, 30) as never);
    vi.mocked(updateSettings).mockResolvedValue(saved(9, 30) as never);
    render(<Server />);
    await waitFor(() => expect(maxCaptainsInput()).toHaveValue(4));
    fireEvent.change(maxCaptainsInput(), { target: { value: '9' } });
    fireEvent.change(heartbeatInput(), { target: { value: '60' } });
    fireEvent.click(screen.getByText('Save Server Config'));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledWith({ admiralPort: 7890, mcpPort: 7891, maxCaptains: 9 }));
    await waitFor(() => expect(maxCaptainsInput()).toHaveValue(9));
    expect(heartbeatInput()).toHaveValue(60);
  });
});
