import { act, render } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ isAuthenticated: true, sessionToken: 'session-1' }),
}));

import { WebSocketProvider } from './WebSocketContext';

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
