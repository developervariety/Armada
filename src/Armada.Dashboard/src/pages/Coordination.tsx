import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useLocale } from '../context/LocaleContext';
import { useWebSocket } from '../context/WebSocketContext';
import CoordinationChatCard from '../components/coordination/CoordinationChatCard';
import {
  listCoordinationMessages,
  listCoordinationParticipants,
  listCoordinationRooms,
  postCoordinationMessage,
  sendCoordinationPresence,
} from '../api/client';
import type { CoordinationMessage, CoordinationParticipant, CoordinationRoom } from '../types/models';
import { getDashboardParticipantKey, sortMessages, upsertMessage } from './coordination/coordinationUtils';

const HEARTBEAT_INTERVAL_MS = 30_000;
const POLL_FALLBACK_INTERVAL_MS = 20_000;

/**
 * Shared coordination board: one chatroom where every operator session and the
 * dashboard see who is doing what. Sessions claim work here before dispatching,
 * so nobody is startled by a voyage they did not start.
 */
export default function Coordination() {
  const { t, formatDateTime, formatRelativeTime } = useLocale();
  const { subscribe } = useWebSocket();

  const [rooms, setRooms] = useState<CoordinationRoom[]>([]);
  const [selectedRoomKey, setSelectedRoomKey] = useState<string>('');
  const [messages, setMessages] = useState<CoordinationMessage[]>([]);
  const [participants, setParticipants] = useState<CoordinationParticipant[]>([]);
  const [composer, setComposer] = useState('');
  const [sending, setSending] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const transcriptRef = useRef<HTMLDivElement | null>(null);
  const participantKeyRef = useRef<string>(getDashboardParticipantKey());

  const roomKey = selectedRoomKey || 'fleet';
  const currentRoom = useMemo(
    () => rooms.find((r) => r.key === roomKey) || null,
    [rooms, roomKey],
  );

  const loadRooms = useCallback(async () => {
    try {
      const result = await listCoordinationRooms();
      setRooms(result);
      if (result.length > 0) {
        setSelectedRoomKey((current) => (current ? current : result[0].key));
      }
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load coordination rooms.'));
    }
  }, [t]);

  const loadMessages = useCallback(async () => {
    try {
      setLoading(true);
      const result = await listCoordinationMessages(roomKey, { limit: 200 });
      setMessages(sortMessages(result));
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to load board notes.'));
    } finally {
      setLoading(false);
    }
  }, [roomKey, t]);

  const loadParticipants = useCallback(async () => {
    try {
      const result = await listCoordinationParticipants(roomKey, 15);
      setParticipants(result);
    } catch {
      // presence is best-effort
    }
  }, [roomKey]);

  useEffect(() => {
    loadRooms();
  }, [loadRooms]);

  useEffect(() => {
    loadMessages();
    loadParticipants();
  }, [loadMessages, loadParticipants]);

  // Heartbeat while the page is open so other sessions see the dashboard as present.
  useEffect(() => {
    const beat = () => {
      sendCoordinationPresence(roomKey, {
        participantKey: participantKeyRef.current,
        displayName: 'Dashboard',
      })
        .then(() => loadParticipants())
        .catch(() => undefined);
    };
    beat();
    const timer = window.setInterval(beat, HEARTBEAT_INTERVAL_MS);
    return () => window.clearInterval(timer);
  }, [roomKey, loadParticipants]);

  // Live updates over WebSocket; polling is only a fallback.
  useEffect(() => {
    const unsubscribe = subscribe((msg) => {
      if (msg.type === 'coordination.message.created') {
        const payload = msg.data as { roomKey?: string; message?: CoordinationMessage } | undefined;
        if (!payload?.message) return;
        if (payload.roomKey && payload.roomKey !== roomKey) return;
        setMessages((current) => upsertMessage(current, payload.message!));
      }
    });
    return unsubscribe;
  }, [roomKey, subscribe]);

  useEffect(() => {
    const timer = window.setInterval(loadMessages, POLL_FALLBACK_INTERVAL_MS);
    return () => window.clearInterval(timer);
  }, [loadMessages]);

  useEffect(() => {
    if (!transcriptRef.current) return;
    transcriptRef.current.scrollTop = transcriptRef.current.scrollHeight;
  }, [messages]);

  async function handleSend() {
    if (!composer.trim() || sending) return;

    try {
      setSending(true);
      setError('');
      const message = await postCoordinationMessage(roomKey, {
        content: composer.trim(),
        authorName: 'Dashboard',
        authorId: participantKeyRef.current,
      });
      setComposer('');
      setMessages((current) => upsertMessage(current, message));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to post note.'));
    } finally {
      setSending(false);
    }
  }

  const canSend = composer.trim().length > 0 && !sending;

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Chatroom</h1>
          <p className="text-muted">
            One shared board for operator sessions and captains. Read before you act; write when you act.
          </p>
        </div>
        <button type="button" className="btn" onClick={() => { loadMessages(); loadParticipants(); }}>
          Refresh
        </button>
      </div>

      {error && <div className="alert alert-error">{error}</div>}

      <div className="coordination-layout">
        <div className="card coordination-rooms">
          <div className="card-label">Rooms</div>
          {rooms.length === 0 && !loading && (
            <div className="text-muted">No rooms yet.</div>
          )}
          {rooms.map((r) => (
            <button
              key={r.id}
              type="button"
              className={`coordination-room-item${r.key === roomKey ? ' is-active' : ''}`}
              onClick={() => setSelectedRoomKey(r.key)}
            >
              <span>{r.name}</span>
              <span className="text-dim">{formatRelativeTime(r.lastUpdateUtc)}</span>
            </button>
          ))}
        </div>

        <div className="coordination-chat">
          <CoordinationChatCard
            transcriptRef={transcriptRef}
            roomName={currentRoom?.name || 'Fleet'}
            messages={messages}
            participants={participants}
            composer={composer}
            sending={sending}
            canSend={canSend}
            formatDateTime={formatDateTime}
            formatRelativeTime={formatRelativeTime}
            onComposerChange={setComposer}
            onSend={handleSend}
          />
        </div>
      </div>
    </div>
  );
}
