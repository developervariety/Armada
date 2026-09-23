import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
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

vi.mock('../components/backlog/BacklogRefinementSessionList', () => ({ default: () => null }));

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  applyObjectiveRefinementSummary: vi.fn(),
  createBacklogItem: vi.fn(),
  createBacklogRefinementSession: vi.fn(),
  deleteBacklogItem: vi.fn(),
  deleteObjectiveRefinementSession: vi.fn(),
  getBacklogItem: vi.fn(),
  getObjectiveRefinementSession: vi.fn(),
  importObjectiveFromGitHub: vi.fn(),
  listAllBacklog: vi.fn(),
  listBacklog: vi.fn(),
  listBacklogRefinementSessions: vi.fn(),
  listCaptains: vi.fn(),
  listFleets: vi.fn(),
  listPipelines: vi.fn(),
  listVessels: vi.fn(),
  sendObjectiveRefinementMessage: vi.fn(),
  stopObjectiveRefinementSession: vi.fn(),
  summarizeObjectiveRefinementSession: vi.fn(),
  updateBacklogItem: vi.fn(),
}));

import {
  getBacklogItem, getObjectiveRefinementSession, listAllBacklog, listBacklog, listBacklogRefinementSessions,
  listCaptains, listFleets, listPipelines, listVessels,
} from '../api/client';
import ObjectiveDetail from './ObjectiveDetail';

const empty = { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };

function objective(title: string) {
  return {
    id: 'obj_1', title, description: null, status: 'Draft', kind: 'Feature', category: null, priority: 'P2', rank: 1,
    backlogState: 'Inbox', effort: 'M', owner: null, targetVersion: null, dueUtc: null, parentObjectiveId: null,
    blockedByObjectiveIds: [], refinementSummary: null, preparation: null, suggestedPipelineId: null, suggestedPlaybooks: [],
    tags: [], acceptanceCriteria: [], nonGoals: [], rolloutConstraints: [], evidenceLinks: [], fleetIds: [], vesselIds: [],
    planningSessionIds: [], refinementSessionIds: [], voyageIds: [], missionIds: [], checkRunIds: [], releaseIds: [],
    deploymentIds: [], incidentIds: [], sourceId: null, sourceType: null,
    createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  };
}

const session = {
  id: 'ors_1', objectiveId: 'obj_1', tenantId: null, userId: null, captainId: 'cpt_1', fleetId: null, vesselId: null,
  title: 'Session', status: 'Active', processId: null, failureReason: null,
  createdUtc: '2026-01-01T00:00:00Z', startedUtc: null, completedUtc: null, lastUpdateUtc: '2026-01-01T00:00:00Z',
};

const summary = {
  summary: 'Tighter scope from the captain',
  acceptanceCriteria: ['Criterion one'],
  nonGoals: [],
  rolloutConstraints: [],
  suggestedPipelineId: null,
  preparation: null,
  method: 'runtime-json',
};

beforeEach(() => {
  handlers.clear();
  for (const fn of [listFleets, listVessels, listCaptains, listPipelines, listBacklog]) {
    vi.mocked(fn).mockResolvedValue(empty as never);
  }
  vi.mocked(listAllBacklog).mockResolvedValue([] as never);
  vi.mocked(getBacklogItem).mockReset();
  vi.mocked(getBacklogItem).mockResolvedValue(objective('Objective One') as never);
  vi.mocked(listBacklogRefinementSessions).mockResolvedValue([session] as never);
  vi.mocked(getObjectiveRefinementSession).mockResolvedValue({ session, messages: [], captain: null } as never);
});

function renderPage() {
  render(
    <MemoryRouter initialEntries={['/backlog/obj_1']}>
      <Routes>
        <Route path="/backlog/:id" element={<ObjectiveDetail />} />
        <Route path="/backlog" element={<div>Backlog List</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

function emit(type: string, data: unknown) {
  act(() => {
    handlers.forEach((handler) => handler({ type, data }));
  });
}

test('applies a live change when the form has no unsaved edits', async () => {
  renderPage();
  expect(await screen.findByDisplayValue('Objective One')).toBeInTheDocument();
  emit('objective.changed', objective('Server Title'));
  expect(await screen.findByDisplayValue('Server Title')).toBeInTheDocument();
});

test('keeps unsaved edits when a live change arrives and says the record changed', async () => {
  renderPage();
  const titleInput = await screen.findByDisplayValue('Objective One');
  fireEvent.change(titleInput, { target: { value: 'My Title' } });
  emit('objective.changed', objective('Server Title'));
  expect(screen.getByDisplayValue('My Title')).toBeInTheDocument();
  expect(screen.getByText(/changed on the server/)).toBeInTheDocument();
});

test('shows that the backlog item was deleted when it no longer exists', async () => {
  vi.mocked(getBacklogItem).mockRejectedValue(Object.assign(new Error('Not found'), { status: 404 }));
  renderPage();
  expect(await screen.findByText(/was deleted or does not exist/)).toBeInTheDocument();
});

test('shows that the backlog item was deleted when a delete event arrives', async () => {
  renderPage();
  expect(await screen.findByDisplayValue('Objective One')).toBeInTheDocument();
  emit('objective.deleted', { id: 'obj_1' });
  expect(await screen.findByText(/was deleted or does not exist/)).toBeInTheDocument();
});

test('renders the summary from a summary.created event', async () => {
  renderPage();
  expect(await screen.findByDisplayValue('Objective One')).toBeInTheDocument();
  await waitFor(() => expect(getObjectiveRefinementSession).toHaveBeenCalled());
  emit('objective-refinement-session.summary.created', { sessionId: 'ors_1', messageId: null, summary });
  expect(await screen.findByText('Tighter scope from the captain')).toBeInTheDocument();
});

test('reloads the backlog item when an applied event names it', async () => {
  renderPage();
  expect(await screen.findByDisplayValue('Objective One')).toBeInTheDocument();
  vi.mocked(getBacklogItem).mockResolvedValue(objective('Applied Title') as never);
  emit('objective-refinement-session.applied', { sessionId: 'ors_1', objectiveId: 'obj_1', summary });
  expect(await screen.findByDisplayValue('Applied Title')).toBeInTheDocument();
});
