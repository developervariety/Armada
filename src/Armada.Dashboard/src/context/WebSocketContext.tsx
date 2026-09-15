import { createContext, useContext, useState, useEffect, useRef, useCallback, type ReactNode } from 'react';
import type { WebSocketMessage } from '../types/models';
import { useAuth } from './AuthContext';

type MessageHandler = (msg: WebSocketMessage) => void;

interface WebSocketState {
  connected: boolean;
  subscribe: (handler: MessageHandler) => () => void;
  send: (data: unknown) => void;
}

const WebSocketContext = createContext<WebSocketState | null>(null);

const RECONNECT_DELAY = 3000;

/**
 * Synthetic message broadcast to subscribers when live events may have been missed: after a
 * reconnect, or when the server reports a replay gap (`event.gap`, or `stream.ready` with
 * `gapDetected`). A page that keeps state from live events reloads from the REST API on it.
 */
export const RESYNC_MESSAGE_TYPE = 'client.resync';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

export function WebSocketProvider({ children }: { children: ReactNode }) {
  const { isAuthenticated, sessionToken } = useAuth();
  const sessionTokenRef = useRef<string | null>(sessionToken);
  sessionTokenRef.current = sessionToken;
  const [connected, setConnected] = useState(false);
  const wsRef = useRef<WebSocket | null>(null);
  const handlersRef = useRef<Set<MessageHandler>>(new Set());
  const reconnectTimerRef = useRef<number | null>(null);
  const mountedRef = useRef(true);
  // True once any connection has reached stream.ready; a later stream.ready is a reconnect.
  const hadStreamRef = useRef(false);

  const subscribe = useCallback((handler: MessageHandler) => {
    handlersRef.current.add(handler);
    return () => {
      handlersRef.current.delete(handler);
    };
  }, []);

  const send = useCallback((data: unknown) => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      wsRef.current.send(JSON.stringify(data));
    }
  }, []);

  const dispatch = useCallback((data: WebSocketMessage) => {
    handlersRef.current.forEach(handler => handler(data));
  }, []);

  const connectWs = useCallback(() => {
    if (!mountedRef.current) return;
    try {
      const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
      const url = `${protocol}//${window.location.host}/ws`;
      const ws = new WebSocket(url);

      ws.onopen = () => {
        if (!mountedRef.current) { ws.close(); return; }
        setConnected(true);
        // The server refuses every other route until the session authenticates,
        // and it handles frames in order, so subscribe follows authentication.
        ws.send(JSON.stringify({ Route: 'authenticate', token: sessionTokenRef.current }));
        ws.send(JSON.stringify({ Route: 'subscribe' }));
      };

      ws.onmessage = (evt) => {
        let data: WebSocketMessage;
        try {
          data = JSON.parse(evt.data) as WebSocketMessage;
        } catch {
          return;
        }
        dispatch(data);

        // Events sent while disconnected, or dropped by the server's replay buffer, never arrive.
        // Tell subscribers so they reload instead of showing state that silently went stale.
        let resyncReason: string | null = null;
        if (data.type === 'event.gap') {
          resyncReason = isRecord(data.data) && typeof data.data.reason === 'string' ? data.data.reason : 'gap';
        } else if (data.type === 'stream.ready') {
          const gapDetected = isRecord(data.data) && data.data.gapDetected === true;
          if (gapDetected) resyncReason = 'gap';
          else if (hadStreamRef.current) resyncReason = 'reconnect';
          hadStreamRef.current = true;
        }
        if (resyncReason) {
          dispatch({ type: RESYNC_MESSAGE_TYPE, data: { reason: resyncReason }, timestamp: new Date().toISOString() });
        }
      };

      ws.onclose = () => {
        if (!mountedRef.current) return;
        setConnected(false);
        wsRef.current = null;
        reconnectTimerRef.current = window.setTimeout(() => {
          if (mountedRef.current) connectWs();
        }, RECONNECT_DELAY);
      };

      ws.onerror = () => {
        setConnected(false);
      };

      wsRef.current = ws;
    } catch {
      setConnected(false);
      reconnectTimerRef.current = window.setTimeout(() => {
        if (mountedRef.current) connectWs();
      }, RECONNECT_DELAY);
    }
  }, [dispatch]);

  useEffect(() => {
    mountedRef.current = true;

    if (!isAuthenticated) {
      return;
    }

    connectWs();

    return () => {
      mountedRef.current = false;
      if (reconnectTimerRef.current) {
        clearTimeout(reconnectTimerRef.current);
        reconnectTimerRef.current = null;
      }
      if (wsRef.current) {
        wsRef.current.close();
        wsRef.current = null;
      }
    };
  }, [isAuthenticated, connectWs]);

  return (
    <WebSocketContext.Provider value={{ connected, subscribe, send }}>
      {children}
    </WebSocketContext.Provider>
  );
}

export function useWebSocket(): WebSocketState {
  const ctx = useContext(WebSocketContext);
  if (!ctx) throw new Error('useWebSocket must be used within WebSocketProvider');
  return ctx;
}
