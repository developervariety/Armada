import { act, render } from '@testing-library/react';
import { beforeEach, expect, test, vi } from 'vitest';
import type { WebSocketMessage } from '../types/models';

const handlers = new Set<(msg: WebSocketMessage) => void>();

vi.mock('./WebSocketContext', () => ({
  useWebSocket: () => ({
    connected: true,
    send: vi.fn(),
    subscribe: (handler: (msg: WebSocketMessage) => void) => {
      handlers.add(handler);
      return () => handlers.delete(handler);
    },
  }),
}));

vi.mock('./LocaleContext', () => ({
  useLocale: () => ({ t: (text: string) => text }),
}));

import { NotificationProvider, notificationRoute, useNotifications, type Notification } from './NotificationContext';

function Capture({ onValue }: { onValue: (notifications: Notification[]) => void }) {
  const { notifications } = useNotifications();
  onValue(notifications);
  return null;
}

function renderProvider() {
  let latest: Notification[] = [];
  render(
    <NotificationProvider>
      <Capture onValue={(value) => { latest = value; }} />
    </NotificationProvider>,
  );
  return () => latest;
}

beforeEach(() => {
  handlers.clear();
  localStorage.clear();
});

test('uses the server message timestamp instead of the browser clock', () => {
  const read = renderProvider();
  act(() => {
    handlers.forEach((handler) => handler({
      type: 'mission.changed',
      timestamp: '2026-01-02T03:04:05Z',
      data: { id: 'msn_1', title: 'Mission one', status: 'Failed' },
    }));
  });
  expect(read()[0].timestampUtc).toBe('2026-01-02T03:04:05Z');
});

test.each([
  ['deployment.changed', 'dpl_1', '/deployments/dpl_1'],
  ['objective.changed', 'obj_1', '/objectives/obj_1'],
  ['incident.changed', 'inc_1', '/incidents/inc_1'],
])('stores the entity id for %s so the notification can be opened', (type, id, route) => {
  const read = renderProvider();
  act(() => {
    handlers.forEach((handler) => handler({ type, data: { id, title: 'Record', status: 'Failed' } }));
  });
  const notification = read()[0];
  expect(notification.entityId).toBe(id);
  expect(notificationRoute(notification)).toBe(route);
});
