import { act, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';
import type { PlanningSession, PlanningSessionDetail, PlanningSessionMessage, WebSocketMessage } from '../types/models';

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

vi.mock('../context/NotificationContext', () => {
  const notifications = { pushToast: vi.fn() };
  return { useNotifications: () => notifications };
});

vi.mock('../api/client', () => ({
  createPlanningSession: vi.fn(),
  deletePlanningSession: vi.fn(),
  dispatchPlanningSession: vi.fn(),
  getVesselReadiness: vi.fn(),
  getPlanningSession: vi.fn(),
  listPlanningSessions: vi.fn(),
  sendPlanningSessionMessage: vi.fn(),
  stopPlanningSession: vi.fn(),
  stopPlanningTurn: vi.fn(),
  summarizePlanningSession: vi.fn(),
  listAllCaptains: vi.fn(),
  listAllFleets: vi.fn(),
  listAllPipelines: vi.fn(),
  listAllVessels: vi.fn(),
  listAllPlaybooks: vi.fn(),
}));

import {
  getPlanningSession,
  getVesselReadiness,
  listAllCaptains,
  listAllFleets,
  listAllPipelines,
  listAllPlaybooks,
  listAllVessels,
  listPlanningSessions,
} from '../api/client';
import Planning from './Planning';
import { deferred } from '../test/routeRace';

const session: PlanningSession = {
  id: 'psn_1', tenantId: null, userId: null, captainId: 'cpt_1', vesselId: 'vsl_1', fleetId: null, dockId: null,
  branchName: null, title: 'Plan the importer', status: 'Active', pipelineId: null, processId: null, failureReason: null,
  createdUtc: '2026-01-01T00:00:00Z', startedUtc: '2026-01-01T00:00:00Z', completedUtc: null,
  lastUpdateUtc: '2026-01-01T00:00:00Z', selectedPlaybooks: [],
};

function message(id: string, sequence: number, content: string): PlanningSessionMessage {
  return {
    id, planningSessionId: 'psn_1', tenantId: null, userId: null, role: 'Assistant', sequence, content,
    isSelectedForDispatch: false, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  };
}

function detail(messages: PlanningSessionMessage[]): PlanningSessionDetail {
  return { session, messages, captain: null, vessel: null };
}

function emit(msg: WebSocketMessage) {
  handlers.forEach((handler) => handler(msg));
}

function renderSession() {
  render(
    <MemoryRouter initialEntries={['/planning/psn_1']}>
      <Routes><Route path="/planning/:id" element={<Planning />} /></Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  handlers.clear();
  vi.mocked(getPlanningSession).mockReset();
  vi.mocked(listPlanningSessions).mockReset();
  vi.mocked(listPlanningSessions).mockResolvedValue([session]);
  vi.mocked(listAllCaptains).mockResolvedValue([]);
  vi.mocked(listAllFleets).mockResolvedValue([]);
  vi.mocked(listAllVessels).mockResolvedValue([]);
  vi.mocked(listAllPipelines).mockResolvedValue([]);
  vi.mocked(listAllPlaybooks).mockResolvedValue([]);
  vi.mocked(getVesselReadiness).mockResolvedValue(null as never);
});

test('a resync after missed events reads the session list and the open session again', async () => {
  vi.mocked(getPlanningSession).mockResolvedValue(detail([message('msg_1', 1, 'First reply')]));
  renderSession();
  expect((await screen.findAllByText('First reply')).length).toBeGreaterThan(0);
  const catalogReads = vi.mocked(listPlanningSessions).mock.calls.length;

  // A reply created while the connection was down never arrives as an event; only a reread shows it.
  vi.mocked(getPlanningSession).mockResolvedValue(detail([message('msg_1', 1, 'First reply'), message('msg_2', 2, 'Reply sent during the outage')]));
  await act(async () => { emit({ type: 'client.resync', data: { reason: 'reconnect' }, timestamp: '2026-01-01T00:00:00Z' }); });

  expect((await screen.findAllByText('Reply sent during the outage')).length).toBeGreaterThan(0);
  expect(getPlanningSession).toHaveBeenCalledTimes(2);
  await waitFor(() => expect(vi.mocked(listPlanningSessions).mock.calls.length).toBeGreaterThan(catalogReads));
});

test('a message event that arrives while the session is loading is shown once the load completes', async () => {
  const load = deferred<PlanningSessionDetail>();
  vi.mocked(getPlanningSession).mockReturnValue(load.promise);
  renderSession();
  await waitFor(() => expect(getPlanningSession).toHaveBeenCalledWith('psn_1'));

  await act(async () => {
    emit({
      type: 'planning-session.message.created',
      data: { sessionId: 'psn_1', message: message('msg_2', 2, 'Reply created during the load') },
      timestamp: '2026-01-01T00:00:00Z',
    });
  });
  // The read's snapshot was taken before the reply existed.
  await act(async () => { load.resolve(detail([message('msg_1', 1, 'First reply')])); });

  expect((await screen.findAllByText('First reply')).length).toBeGreaterThan(0);
  expect(screen.getAllByText('Reply created during the load').length).toBeGreaterThan(0);
});
