import { act, render } from '@testing-library/react';
import { StrictMode } from 'react';
import { afterEach, expect, test, vi } from 'vitest';

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ isAuthenticated: true, sessionToken: 'session-1' }),
}));

import { RESYNC_MESSAGE_TYPE, WebSocketProvider, useWebSocket } from './WebSocketContext';
import type { WebSocketMessage } from '../types/models';
import { useEffect } from 'react';

function Recorder({ received }: { received: WebSocketMessage[] }) {
  const { subscribe } = useWebSocket();
  useEffect(() => subscribe((msg) => { received.push(msg); }), [subscribe, received]);
  return null;
}

function deliver(socket: FakeSocket, frame: unknown) {
  socket.onmessage?.({ data: JSON.stringify(frame) });
}

test('broadcasts a resync message when the server reports a gap', () => {
  vi.stubGlobal('WebSocket', FakeSocket);
  const received: WebSocketMessage[] = [];
  render(
    <WebSocketProvider>
      <Recorder received={received} />
    </WebSocketProvider>,
  );
  const socket = FakeSocket.instances[0];
  act(() => {
    socket.onopen?.();
    deliver(socket, { type: 'stream.ready', data: { replayed: 0, gapDetected: false } });
  });
  expect(received.filter((msg) => msg.type === RESYNC_MESSAGE_TYPE)).toHaveLength(0);

  act(() => {
    deliver(socket, { type: 'event.gap', data: { reason: 'replay_overflow' } });
  });
  expect(received.filter((msg) => msg.type === RESYNC_MESSAGE_TYPE)).toHaveLength(1);
});

test('broadcasts a resync message after a reconnect', () => {
  vi.useFakeTimers();
  vi.stubGlobal('WebSocket', FakeSocket);
  const received: WebSocketMessage[] = [];
  render(
    <WebSocketProvider>
      <Recorder received={received} />
    </WebSocketProvider>,
  );
  const first = FakeSocket.instances[0];
  act(() => {
    first.onopen?.();
    deliver(first, { type: 'stream.ready', data: { replayed: 0, gapDetected: false } });
    first.onclose?.();
    vi.advanceTimersByTime(3500);
  });
  const second = FakeSocket.instances[1];
  expect(second).toBeDefined();
  act(() => {
    second.onopen?.();
    deliver(second, { type: 'stream.ready', data: { replayed: 0, gapDetected: false } });
  });
  expect(second.sent.map((frame) => JSON.parse(frame))).toEqual([
    { Route: 'authenticate', token: 'session-1' },
    { Route: 'subscribe' },
  ]);
  expect(received.filter((msg) => msg.type === RESYNC_MESSAGE_TYPE)).toHaveLength(1);
  vi.useRealTimers();
});

class FakeSocket {
  static OPEN = 1;
  static instances: FakeSocket[] = [];
  url: string;
  readyState = 0;
  sent: string[] = [];
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(url: string) {
    this.url = url;
    FakeSocket.instances.push(this);
  }

  send(data: string) {
    this.sent.push(data);
  }

  close() {
    this.readyState = 3;
  }
}

afterEach(() => {
  FakeSocket.instances = [];
  vi.unstubAllGlobals();
});

test('authenticates with the session token before subscribing', () => {
  vi.stubGlobal('WebSocket', FakeSocket);

  render(
    <WebSocketProvider>
      <div />
    </WebSocketProvider>,
  );

  const socket = FakeSocket.instances[0];
  expect(socket).toBeDefined();
  act(() => {
    socket.onopen?.();
  });

  expect(socket.sent.map((frame) => JSON.parse(frame))).toEqual([
    { Route: 'authenticate', token: 'session-1' },
    { Route: 'subscribe' },
  ]);
});

test('a replaced socket that closes late leaves the current socket in place and starts no reconnect', () => {
  vi.useFakeTimers();
  vi.stubGlobal('WebSocket', FakeSocket);
  const state: { current: ReturnType<typeof useWebSocket> | null } = { current: null };
  function Capture() {
    state.current = useWebSocket();
    return null;
  }
  // StrictMode runs the connect effect, its cleanup, and the effect again: the first socket is closed and
  // replaced before its close event arrives.
  render(
    <StrictMode>
      <WebSocketProvider>
        <Capture />
      </WebSocketProvider>
    </StrictMode>,
  );
  expect(FakeSocket.instances).toHaveLength(2);
  const [replaced, current] = FakeSocket.instances;
  act(() => {
    current.readyState = FakeSocket.OPEN;
    current.onopen?.();
  });
  expect(state.current?.connected).toBe(true);

  act(() => {
    replaced.onclose?.();
    vi.advanceTimersByTime(3500);
  });

  expect(FakeSocket.instances).toHaveLength(2);
  expect(state.current?.connected).toBe(true);
  act(() => {
    state.current?.send({ Route: 'ping' });
  });
  expect(current.sent.map((frame) => JSON.parse(frame))).toContainEqual({ Route: 'ping' });
  vi.useRealTimers();
});
