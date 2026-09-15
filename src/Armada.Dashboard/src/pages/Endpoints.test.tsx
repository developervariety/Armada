import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Endpoints from './Endpoints';
import { createModelEndpoint, listModelEndpoints, updateModelEndpoint } from '../api/client';

const auth = vi.hoisted(() => ({ current: { isAdmin: false, isTenantAdmin: false, user: { user: { id: 'usr_1', tenantId: 'ten_a' } } } }));

vi.mock('../api/client', () => ({
  listModelEndpoints: vi.fn(),
  createModelEndpoint: vi.fn(),
  updateModelEndpoint: vi.fn(),
  deleteModelEndpoint: vi.fn(),
  validateModelEndpoint: vi.fn(),
  healthCheckModelEndpoints: vi.fn(),
}));
vi.mock('../context/AuthContext', () => ({ useAuth: () => auth.current }));
vi.mock('../context/LocaleContext', () => ({
  useLocale: () => ({ t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v }),
}));
vi.mock('../context/NotificationContext', () => ({ useNotifications: () => ({ pushToast: vi.fn() }) }));

function endpoint(overrides: Record<string, unknown>) {
  return {
    id: 'mep_1', tenantId: 'ten_a', userId: 'usr_1', scope: 'UserSpecific', name: 'Mine', kind: 'Inference',
    provider: 'OpenAI', baseUrl: 'https://api.openai.com', model: 'gpt-4o-mini', dimensionality: 0, timeoutMs: 120000,
    enabled: true, hasApiKey: true, healthStatus: 'Unknown', lastHealthCheckUtc: null, lastHealthError: null,
    lastLatencyMs: null, healthHistory: [], uptimePercentage: 0, consecutiveSuccesses: 0, consecutiveFailures: 0,
    firstHealthCheckUtc: null, lastHealthyUtc: null, lastUnhealthyUtc: null,
    createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('Endpoints page', () => {
  beforeEach(() => {
    auth.current = { isAdmin: false, isTenantAdmin: false, user: { user: { id: 'usr_1', tenantId: 'ten_a' } } };
    vi.mocked(createModelEndpoint).mockReset();
    vi.mocked(updateModelEndpoint).mockReset();
    vi.mocked(listModelEndpoints).mockResolvedValue([
      endpoint({}),
      endpoint({ id: 'mep_2', name: 'Shared', scope: 'TenantWide', userId: 'usr_admin' }),
    ] as never);
  });

  it('hides the health sweep from anyone but a global administrator', async () => {
    auth.current = { isAdmin: false, isTenantAdmin: true, user: { user: { id: 'usr_ta', tenantId: 'ten_a' } } };
    render(<Endpoints />);
    await screen.findByText('Mine');
    expect(screen.queryByText('Run Health Sweep')).not.toBeInTheDocument();
  });

  it('creates a personal endpoint for a regular user with only fields the server accepts', async () => {
    vi.mocked(createModelEndpoint).mockResolvedValue(endpoint({ id: 'mep_3', name: 'Local' }) as never);
    render(<Endpoints />);
    await screen.findByText('Mine');
    fireEvent.click(screen.getByText('+ Endpoint'));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Local' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'http://localhost:11434' } });
    fireEvent.change(screen.getByLabelText('Provider'), { target: { value: 'Ollama' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create Endpoint' }));
    await waitFor(() => expect(createModelEndpoint).toHaveBeenCalledWith({
      name: 'Local', kind: 'Inference', provider: 'Ollama', baseUrl: 'http://localhost:11434', model: null,
      dimensionality: 0, timeoutMs: 120000, enabled: true, scope: 'UserSpecific',
    }));
  });

  it('updates an owned endpoint without resending its scope or stored key', async () => {
    vi.mocked(updateModelEndpoint).mockResolvedValue(endpoint({ name: 'Mine 2' }) as never);
    render(<Endpoints />);
    fireEvent.click(await screen.findByText('Mine'));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Mine 2' } });
    fireEvent.click(screen.getByText('Save Changes'));
    await waitFor(() => expect(updateModelEndpoint).toHaveBeenCalled());
    const [id, body] = vi.mocked(updateModelEndpoint).mock.calls[0];
    expect(id).toBe('mep_1');
    expect(body).not.toHaveProperty('scope');
    expect(body).not.toHaveProperty('apiKey');
    expect(body).toMatchObject({ name: 'Mine 2' });
  });

  it('opens health, not the editor, for a tenant-wide endpoint a regular user cannot change', async () => {
    render(<Endpoints />);
    fireEvent.click(await screen.findByText('Shared'));
    expect(await screen.findByText('Health History')).toBeInTheDocument();
    expect(screen.queryByText('Edit Endpoint')).not.toBeInTheDocument();
    expect(screen.queryByText('Validate Now')).not.toBeInTheDocument();
  });
});
