import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';
import type { WebSocketMessage } from '../types/models';

const handlers = new Set<(msg: WebSocketMessage) => void>();

vi.mock('../context/WebSocketContext', () => ({
  RESYNC_MESSAGE_TYPE: 'client.resync',
  useWebSocket: () => ({
    connected: true,
    send: vi.fn(),
    subscribe: (handler: (msg: WebSocketMessage) => void) => {
      handlers.add(handler);
      return () => handlers.delete(handler);
    },
  }),
}));

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number>) =>
      (params ? text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(params[key] ?? '')) : text),
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ isAdmin: true, isTenantAdmin: false }),
}));

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../components/shared/MissionRecoveryPanel', () => ({ default: () => null }));

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  createIncident: vi.fn(),
  deleteIncident: vi.fn(),
  getIncident: vi.fn(),
  getMissionRecovery: vi.fn(),
  listDeployments: vi.fn(),
  listEnvironments: vi.fn(),
  listReleases: vi.fn(),
  listRunbookExecutions: vi.fn(),
  listVessels: vi.fn(),
  rollbackDeployment: vi.fn(),
  updateIncident: vi.fn(),
}));

import {
  getIncident, listDeployments, listEnvironments, listReleases, listRunbookExecutions, listVessels, updateIncident,
} from '../api/client';
import IncidentDetail from './IncidentDetail';

const empty = { success: true, pageNumber: 1, pageSize: 500, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };

function incident(title: string, status = 'Open') {
  return {
    id: 'inc_1', title, summary: null, status, severity: 'High', vesselId: null, environmentId: null, environmentName: null,
    deploymentId: null, releaseId: null, missionId: null, voyageId: null, rollbackDeploymentId: null, impact: null,
    rootCause: null, recoveryNotes: null, postmortem: null, detectedUtc: null, mitigatedUtc: null, closedUtc: null,
    createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  };
}

beforeEach(() => {
  handlers.clear();
  for (const fn of [listVessels, listEnvironments, listDeployments, listReleases, listRunbookExecutions]) {
    vi.mocked(fn).mockResolvedValue(empty as never);
  }
  vi.mocked(getIncident).mockResolvedValue(incident('Old title') as never);
});

function renderPage() {
  render(
    <MemoryRouter initialEntries={['/incidents/inc_1']}>
      <Routes>
        <Route path="/incidents/:id" element={<IncidentDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

function emitChange(title: string, status = 'Mitigated') {
  act(() => {
    handlers.forEach((handler) => handler({ type: 'incident.changed', data: incident(title, status) }));
  });
}

test('applies a live change to this incident when the form has no unsaved edits', async () => {
  renderPage();
  expect(await screen.findByDisplayValue('Old title')).toBeInTheDocument();
  emitChange('Server title');
  expect(screen.getByDisplayValue('Server title')).toBeInTheDocument();
});

test('does not overwrite unsaved edits with a live change, and says the record changed', async () => {
  renderPage();
  const titleInput = await screen.findByDisplayValue('Old title');
  fireEvent.change(titleInput, { target: { value: 'My edit' } });
  emitChange('Server title');
  expect(screen.getByDisplayValue('My edit')).toBeInTheDocument();
  expect(screen.getByText(/changed on the server/)).toBeInTheDocument();
});

test('the close dialog asks for the root cause and shows the server refusal', async () => {
  vi.mocked(getIncident).mockResolvedValue({
    ...incident('Mission failed: build'),
    rootCause: 'DoD gate failed: classification=TestFail',
    openedReason: 'DoD gate failed: classification=TestFail',
    rootCauseWrittenBy: null,
  } as never);
  vi.mocked(updateIncident).mockRejectedValue(
    new Error('incident_root_cause_unchanged: the root cause is the text the incident was opened with.'),
  );
  renderPage();
  await screen.findByDisplayValue('Mission failed: build');
  expect(screen.getByText('Automatic reading, not confirmed by a person.')).toBeInTheDocument();

  fireEvent.click(screen.getByRole('button', { name: 'Close Incident' }));
  const dialog = screen.getByRole('dialog', { name: 'Close Incident' });
  expect(within(dialog).getByText('Opened with: DoD gate failed: classification=TestFail')).toBeInTheDocument();
  const cause = within(dialog).getByLabelText('Root Cause') as HTMLTextAreaElement;
  expect(cause.value).toBe('');

  fireEvent.change(cause, { target: { value: 'DoD gate failed: classification=TestFail' } });
  await act(async () => {
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close Incident' }));
  });

  expect(updateIncident).toHaveBeenCalledWith('inc_1', { status: 'Closed', rootCause: 'DoD gate failed: classification=TestFail' });
  expect(await within(dialog).findByRole('alert')).toHaveTextContent('incident_root_cause_unchanged');
  expect(screen.getByRole('dialog', { name: 'Close Incident' })).toBeInTheDocument();
});
