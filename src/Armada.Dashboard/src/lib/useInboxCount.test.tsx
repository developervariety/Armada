import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import type { WebSocketMessage } from '../types/models';

const handlers = new Set<(msg: WebSocketMessage) => void>();

vi.mock('../context/WebSocketContext', () => ({
  useWebSocket: () => ({
    connected: true,
    send: vi.fn(),
    subscribe: (handler: (msg: WebSocketMessage) => void) => {
      handlers.add(handler);
      return () => handlers.delete(handler);
    },
  }),
}));

vi.mock('../api/client', () => ({
  getInbox: vi.fn(),
}));

import { getInbox } from '../api/client';
import { useInboxCount } from './useInboxCount';

function emit() {
  handlers.forEach((handler) => handler({ type: 'mission.changed', data: {} }));
}

beforeEach(() => {
  handlers.clear();
  vi.mocked(getInbox).mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

test('does not request the admin-only inbox when the caller is not an administrator', async () => {
  vi.mocked(getInbox).mockResolvedValue([]);
  renderHook(() => useInboxCount(false));
  await act(async () => { emit(); emit(); });
  expect(getInbox).not.toHaveBeenCalled();
});

test('a refused request does not re-fire on every WebSocket message', async () => {
  vi.mocked(getInbox).mockRejectedValue(Object.assign(new Error('Forbidden'), { status: 403 }));
  renderHook(() => useInboxCount(true));
  await act(async () => { await Promise.resolve(); });
  expect(getInbox).toHaveBeenCalledTimes(1);
  await act(async () => { emit(); emit(); emit(); await Promise.resolve(); });
  expect(getInbox).toHaveBeenCalledTimes(1);
});

test('does not start an overlapping request while one is in flight', async () => {
  vi.useFakeTimers();
  let resolveFirst: (value: never[]) => void = () => {};
  vi.mocked(getInbox).mockImplementationOnce(() => new Promise((resolve) => { resolveFirst = resolve; }));
  vi.mocked(getInbox).mockResolvedValue([]);
  renderHook(() => useInboxCount(true));
  expect(getInbox).toHaveBeenCalledTimes(1);
  // Past the WebSocket throttle, but the first request is still pending.
  await act(async () => { vi.advanceTimersByTime(5000); emit(); });
  expect(getInbox).toHaveBeenCalledTimes(1);
  await act(async () => { resolveFirst([]); await Promise.resolve(); });
  await act(async () => { vi.advanceTimersByTime(5000); emit(); await Promise.resolve(); });
  expect(getInbox).toHaveBeenCalledTimes(2);
});
