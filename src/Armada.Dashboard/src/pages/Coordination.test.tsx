import { act, fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, expect, test, vi } from 'vitest';
import type { CoordinationMessage } from '../types/models';

// One stable locale object, like the real provider: a new `t` per render would re-create the
// page's load callbacks every render and loop its effects.
vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string) => text,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/WebSocketContext', () => ({
  useWebSocket: () => ({ connected: true, send: vi.fn(), subscribe: () => () => undefined }),
}));

vi.mock('../api/client', () => ({
  listCoordinationRooms: vi.fn(),
  listCoordinationMessages: vi.fn(),
  listCoordinationParticipants: vi.fn(),
  listCoordinationClaims: vi.fn(),
  postCoordinationMessage: vi.fn(),
  sendCoordinationPresence: vi.fn(),
}));

import {
  listCoordinationClaims,
  listCoordinationMessages,
  listCoordinationParticipants,
  listCoordinationRooms,
  sendCoordinationPresence,
} from '../api/client';
import Coordination from './Coordination';

function note(id: string, content: string, extra: Partial<CoordinationMessage> = {}): CoordinationMessage {
  return {
    id,
    coordinationRoomId: 'crm_1',
    tenantId: null,
    authorType: 'Operator',
    authorId: null,
    authorName: 'Operator',
    content,
    voyageId: null,
    missionId: null,
    vesselId: null,
    incidentId: null,
    toParticipantKey: null,
    createdUtc: `2026-01-01T00:00:${id.padStart(2, '0').slice(-2)}Z`,
    lastUpdateUtc: '2026-01-01T00:00:00Z',
    ...extra,
  };
}

beforeEach(() => {
  vi.mocked(listCoordinationRooms).mockResolvedValue([
    { id: 'crm_1', key: 'fleet', name: 'Fleet', lastUpdateUtc: '2026-01-01T00:00:00Z' },
    { id: 'crm_2', key: 'ops', name: 'Ops', lastUpdateUtc: '2026-01-01T00:00:00Z' },
  ] as never);
  vi.mocked(listCoordinationParticipants).mockResolvedValue([]);
  vi.mocked(listCoordinationClaims).mockResolvedValue([]);
  vi.mocked(sendCoordinationPresence).mockResolvedValue({} as never);
  vi.mocked(listCoordinationMessages).mockReset();
});

test('a slow read for the previous room does not replace the selected room notes', async () => {
  let resolveFleet: (value: CoordinationMessage[]) => void = () => {};
  vi.mocked(listCoordinationMessages).mockImplementation((roomKey: string) => {
    if (roomKey === 'fleet') return new Promise((resolve) => { resolveFleet = resolve; });
    return Promise.resolve([note('2', 'ops note')]);
  });

  render(<Coordination />);
  fireEvent.click(await screen.findByRole('button', { name: /Ops/ }));
  expect(await screen.findByText('ops note')).toBeInTheDocument();

  await act(async () => { resolveFleet([note('1', 'fleet note')]); });
  expect(screen.queryByText('fleet note')).not.toBeInTheDocument();
  expect(screen.getByText('ops note')).toBeInTheDocument();
});

test('shows the recipient of a directed note', async () => {
  vi.mocked(listCoordinationMessages).mockResolvedValue([note('1', 'for you', { toParticipantKey: 'session-b' })]);
  render(<Coordination />);
  expect(await screen.findByText('for you')).toBeInTheDocument();
  expect(screen.getByText(/to session-b/)).toBeInTheDocument();
});

test('says when only the newest notes are shown', async () => {
  const many = Array.from({ length: 200 }, (_, index) => note(String(index), `note ${index}`, { createdUtc: new Date(Date.UTC(2026, 0, 1, 0, 0, index)).toISOString() }));
  vi.mocked(listCoordinationMessages).mockResolvedValue(many);
  render(<Coordination />);
  expect(await screen.findByText('note 199')).toBeInTheDocument();
  expect(screen.getByText(/Showing the newest 200 notes/)).toBeInTheDocument();
});
