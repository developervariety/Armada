import { useEffect, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { chatWithCaptain, createAskThread, getAskThread, getAskTurn, getCaptainAskTools, listAllCaptains, listAskThreads, listAskTurns, deleteAskThread, deleteAskThreads, markAskThreadRead, markAskThreadUnread, sendAskTurn, updateAskThread } from '../api/client';
import type { AskThread, Captain, CaptainChatMessage, CaptainToolAccessResult, WebSocketMessage } from '../types/models';
import { useLocale } from '../context/LocaleContext';
import { useWebSocket } from '../context/WebSocketContext';
import { useAuthUserId } from '../context/AuthContext';
import ErrorModal from '../components/shared/ErrorModal';
import CaptainChatPanel, { type ChatTurn } from '../components/shared/CaptainChatPanel';
import { applyToolEvent } from '../components/shared/ChatToolChips';
import { randomThinkingMessage } from '../components/askThinkingMessages';
import { randomGreeting } from '../components/askGreetings';
import AskThreadShelf from '../components/ask/AskThreadShelf';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import DeleteProgressDialog from '../components/shared/DeleteProgressDialog';
import { useDeleteProgress } from '../lib/useDeleteProgress';
import AskActionCards from '../components/ask/AskActionCards';
import AskToolPermissionCards from '../components/ask/AskToolPermissionCards';
import { useEarlierAskTurns } from '../components/ask/useEarlierAskTurns';
import { replaceAskTurn, toChatTurns } from '../components/ask/askThreadUtils';
import { usePromptHistory } from '../components/ask/usePromptHistory';
import { loadAskDraft, loadLastAskThread, recordPrompt, saveAskDraft, saveLastAskThread } from '../lib/askStorage';
import { ASK_LIST_MAX_WIDTH, ASK_LIST_MIN_WIDTH, useAskListResize } from '../lib/askListResize';
import { notifyAskUnreadChanged } from '../lib/useAskUnreadCount';

// Ask Armada is available with any captain.
function isChattable(_captain: Captain): boolean {
  return true;
}

// Per-runtime setup instructions doc on GitHub for connecting a captain to Armada over MCP.
function instructionsDocUrl(runtime: string | null | undefined): string {
  const files: Record<string, string> = {
    ClaudeCode: 'INSTRUCTIONS_FOR_CLAUDE_CODE.md',
    Codex: 'INSTRUCTIONS_FOR_CODEX.md',
    Cursor: 'INSTRUCTIONS_FOR_CURSOR.md',
    Gemini: 'INSTRUCTIONS_FOR_GEMINI.md',
    Mux: 'INSTRUCTIONS_FOR_MUX.md',
    OpenCode: 'INSTRUCTIONS_FOR_OPENCODE.md',
  };
  const file = (runtime && files[runtime]) || 'MCP_API.md';
  return 'https://github.com/jchristn/Armada/blob/main/docs/' + file;
}

export default function AskArmada() {
  const { t } = useLocale();
  const navigate = useNavigate();
  // A link from the Needs You inbox opens the saved conversation that holds a proposal.
  const [searchParams] = useSearchParams();
  const linkedThreadId = searchParams.get('thread') ?? '';
  const { subscribe } = useWebSocket();
  const userId = useAuthUserId();
  const [captains, setCaptains] = useState<Captain[]>([]);
  const [mode, setMode] = useState<'temporary' | 'saved'>(linkedThreadId ? 'saved' : 'temporary');
  const [captainId, setCaptainId] = useState('');
  const [threads, setThreads] = useState<AskThread[]>([]);
  const [selectedThreadId, setSelectedThreadId] = useState(linkedThreadId);
  const [includeArchived, setIncludeArchived] = useState(false);
  const [deleteSelection, setDeleteSelection] = useState<string[]>([]);
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({ open: false, title: '', message: '', onConfirm: () => {} });
  const deleteProgress = useDeleteProgress();
  const [threadsLoading, setThreadsLoading] = useState(true);
  const [hasEarlierTurns, setHasEarlierTurns] = useState(false);
  const [turnStatusNotice, setTurnStatusNotice] = useState('');
  const [retryRequest, setRetryRequest] = useState<{ threadId: string; requestId: string; content: string; showThinking: boolean } | null>(null);
  const [turns, setTurns] = useState<ChatTurn[]>([]);
  const [temporaryTurns, setTemporaryTurns] = useState<ChatTurn[]>([]);
  const [temporaryCaptainId, setTemporaryCaptainId] = useState('');
  const [input, setInput] = useState('');
  const workspaceRef = useRef<HTMLDivElement>(null);
  const shelfRef = useRef<HTMLElement>(null);
  const listResize = useAskListResize(workspaceRef, shelfRef);
  // A conversation the operator marked unread while it is open stays unread until another is opened.
  const keepUnreadRef = useRef('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [tools, setTools] = useState<CaptainToolAccessResult | null>(null);
  const [toolsLoading, setToolsLoading] = useState(false);
  const [streamingEnabled, setStreamingEnabled] = useState(true);
  const [showThinking, setShowThinking] = useState(false);
  // A single random greeting chosen once per page load, shown large on the blank chat screen.
  const [greeting] = useState(() => randomGreeting());
  const [thinking, setThinking] = useState('');
  const abortRef = useRef<AbortController | null>(null);
  const toolsCache = useRef<Record<string, CaptainToolAccessResult>>({});
  const selectedThreadIdRef = useRef(selectedThreadId);
  const selectedThreadGeneration = useRef(0);
  const earliestLoadedSequence = useRef<number | null>(null);
  const { loadingEarlier, loadEarlierTurns } = useEarlierAskTurns({
    selectionKey: `${mode}:${selectedThreadId}`,
    enabled: mode === 'saved' && turns.length > 0,
    selectedId: selectedThreadIdRef,
    generation: selectedThreadGeneration,
    earliestSequence: earliestLoadedSequence,
    setTurns,
    setHasEarlier: setHasEarlierTurns,
    setError,
    t,
  });

  useEffect(() => {
    if (selectedThreadIdRef.current === selectedThreadId) return;
    selectedThreadIdRef.current = selectedThreadId;
    selectedThreadGeneration.current += 1;
  }, [selectedThreadId]);

  // Each saved conversation keeps its own unsent draft, and the last open conversation is reopened on the next visit.
  useEffect(() => {
    if (mode !== 'saved') return;
    setInput(selectedThreadId ? loadAskDraft(userId, selectedThreadId) : '');
    if (selectedThreadId) saveLastAskThread(userId, selectedThreadId);
    if (keepUnreadRef.current && keepUnreadRef.current !== selectedThreadId) keepUnreadRef.current = '';
  }, [mode, selectedThreadId, userId]);

  function changeInput(value: string) {
    setInput(value);
    if (mode === 'saved' && selectedThreadId) saveAskDraft(userId, selectedThreadId, value);
  }
  const promptHistory = usePromptHistory(input, changeInput);

  // Opening a conversation reads it. The server keeps the unread count so every browser agrees.
  async function readOpenThread(thread: AskThread) {
    if (thread.unreadCount <= 0 || keepUnreadRef.current === thread.id) return;
    try {
      const updated = await markAskThreadRead(thread.id);
      setThreads((current) => current.map((item) => item.id === updated.id ? { ...item, unreadCount: updated.unreadCount } : item));
      notifyAskUnreadChanged();
    } catch { /* the count stays until the next open */ }
  }

  async function toggleUnread(thread: AskThread) {
    try {
      const markRead = thread.unreadCount > 0;
      const updated = markRead ? await markAskThreadRead(thread.id) : await markAskThreadUnread(thread.id);
      keepUnreadRef.current = !markRead && thread.id === selectedThreadIdRef.current ? thread.id : '';
      setThreads((current) => current.map((item) => item.id === updated.id ? { ...item, unreadCount: updated.unreadCount } : item));
      notifyAskUnreadChanged();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to update the conversation.'));
    }
  }

  // Rotate the "waiting" message every 4 seconds while a turn is in flight.
  useEffect(() => {
    if (!busy) return;
    setThinking((prev) => randomThinkingMessage(prev));
    const id = window.setInterval(() => setThinking((prev) => randomThinkingMessage(prev)), 4000);
    return () => window.clearInterval(id);
  }, [busy]);

  useEffect(() => {
    listAllCaptains()
      .then((result) => {
        const chattable = result.filter(isChattable);
        setCaptains(chattable);
        if (chattable.length > 0) setCaptainId((current) => current || chattable[0].id);
      })
      .catch((err: unknown) => setError(err instanceof Error ? err.message : t('Failed to load captains.')));
  }, [t]);

  useEffect(() => {
    if (mode !== 'saved') { setThreadsLoading(false); return undefined; }
    let active = true;
    setThreadsLoading(true);
    listAskThreads(includeArchived)
      .then((result) => {
        if (!active) return;
        setThreads(result);
        const remembered = loadLastAskThread(userId);
        setSelectedThreadId((current) => result.some((thread) => thread.id === current)
          ? current
          : result.find((thread) => thread.id === remembered)?.id ?? result[0]?.id ?? '');
      })
      .catch((err: unknown) => { if (active) setError(err instanceof Error ? err.message : t('Failed to load conversations.')); })
      .finally(() => { if (active) setThreadsLoading(false); });
    return () => { active = false; };
  }, [mode, includeArchived, t, userId]);

  const selectedThread = threads.find((thread) => thread.id === selectedThreadId) || null;

  useEffect(() => {
    if (mode !== 'saved') return undefined;
    if (!selectedThreadId) {
      earliestLoadedSequence.current = null;
      setTurns([]);
      setHasEarlierTurns(false);
      setTurnStatusNotice('');
      return;
    }
    let active = true;
    setTurns([]);
    setTurnStatusNotice('');
    Promise.all([getAskThread(selectedThreadId), listAskTurns(selectedThreadId, undefined, 100)])
      .then(([thread, stored]) => {
        if (!active) return;
        setThreads((current) => current.map((item) => item.id === thread.id ? thread : item));
        void readOpenThread(thread);
        setCaptainId(thread.captainId ?? '');
        earliestLoadedSequence.current = stored[0]?.sequence ?? null;
        setTurns(toChatTurns(stored));
        setHasEarlierTurns(stored.length >= 100);
        if (thread.activeTurnId) {
          getAskTurn(thread.id, thread.activeTurnId).then((turn) => {
            if (!active) return;
            setTurns((current) => replaceAskTurn(current, turn));
            if (turn.state === 'Uncertain') setTurnStatusNotice(t('This turn may not have completed. Armada will not replay it. Start a new conversation to continue.'));
            setBusy(turn.state === 'Pending' || turn.state === 'Running');
          }).catch(() => { if (active) setTurnStatusNotice(t('Armada could not confirm the active turn. Do not resend it.')); });
        }
      })
      .catch((err: unknown) => { if (active) setError(err instanceof Error ? err.message : t('Failed to load this conversation.')); });
    return () => { active = false; };
  }, [mode, selectedThreadId, threadsLoading, t]);

  useEffect(() => {
    const activeTurnId = selectedThread?.activeTurnId;
    if (mode !== 'saved' || !selectedThread || !activeTurnId || !busy) return undefined;
    let active = true;
    const poll = async () => {
      try {
        const turn = await getAskTurn(selectedThread.id, activeTurnId);
        if (!active) return;
        setTurns((current) => replaceAskTurn(current, turn));
        if (turn.state === 'Completed' || turn.state === 'Failed' || turn.state === 'Uncertain') {
          const thread = await getAskThread(selectedThread.id);
          if (active) {
            setThreads((current) => current.map((item) => item.id === thread.id ? thread : item));
            void readOpenThread(thread);
            setBusy(false);
            if (turn.state === 'Uncertain') setTurnStatusNotice(t('This turn may not have completed. Armada will not replay it. Start a new conversation to continue.'));
          }
        }
      } catch {
        if (active) setTurnStatusNotice(t('Armada could not confirm the active turn. Do not resend it.'));
      }
    };
    const id = window.setInterval(() => { void poll(); }, 2000);
    return () => { active = false; window.clearInterval(id); };
  }, [mode, selectedThread, busy, t]);

  async function createThread() {
    try {
      const captain = captainId || captains[0]?.id || null;
      const created = await createAskThread({ captainId: captain });
      setThreads((current) => [created, ...current.filter((thread) => thread.id !== created.id)]);
      selectedThreadIdRef.current = created.id;
      selectedThreadGeneration.current += 1;
      setSelectedThreadId(created.id);
      setCaptainId(created.captainId ?? '');
      setError('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to create a conversation.'));
    }
  }

  async function changeThread(thread: AskThread, patch: Parameters<typeof updateAskThread>[1]) {
    try {
      const updated = await updateAskThread(thread.id, patch);
      const listing = await listAskThreads(includeArchived);
      setThreads(listing);
      if (!listing.some((item) => item.id === selectedThreadId)) setSelectedThreadId(listing[0]?.id ?? '');
      if (updated.id === selectedThreadId) setCaptainId(updated.captainId ?? '');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Failed to update the conversation.'));
    }
  }

  async function reloadAfterDelete() {
    const listing = await listAskThreads(includeArchived);
    setThreads(listing);
    setDeleteSelection((current) => current.filter((id) => listing.some((item) => item.id === id)));
    if (!listing.some((item) => item.id === selectedThreadIdRef.current)) {
      selectedThreadIdRef.current = listing[0]?.id ?? '';
      selectedThreadGeneration.current += 1;
      setSelectedThreadId(listing[0]?.id ?? '');
    }
  }

  function deleteThread(thread: AskThread) {
    setConfirm({
      open: true,
      title: t('Delete Conversation'),
      message: t('Delete conversation "{{title}}"? Its messages and proposals are removed and this cannot be undone.', { title: thread.title }),
      onConfirm: () => {
        setConfirm((current) => ({ ...current, open: false }));
        void deleteProgress.run(t('Deleting conversation'), async () => {
          await deleteAskThread(thread.id);
          await reloadAfterDelete();
        });
      },
    });
  }

  function deleteSelectedThreads() {
    const ids = [...deleteSelection];
    setConfirm({
      open: true,
      title: t('Delete Selected Conversations'),
      message: t('Delete {{count}} selected conversation(s)? Their messages and proposals are removed and this cannot be undone.', { count: ids.length }),
      onConfirm: () => {
        setConfirm((current) => ({ ...current, open: false }));
        void deleteProgress.run(t('Deleting {{count}} conversations', { count: ids.length }), async () => {
          const result = await deleteAskThreads(ids);
          await reloadAfterDelete();
          if (result.skipped.length > 0) {
            const lines = result.skipped.map((item) => `${threads.find((thread) => thread.id === item.id)?.title ?? item.id}: ${item.reason}`);
            setError(t('Deleted {{deleted}} conversations. {{kept}} kept:', { deleted: result.deleted, kept: result.skipped.length }) + ' ' + lines.join('; '));
          }
        });
      },
    });
  }

  async function renameThread(thread: AskThread) {
    const title = window.prompt(t('Conversation name'), thread.title);
    if (title == null || title.trim() === thread.title) return;
    await changeThread(thread, { setTitle: true, title: title.trim() });
  }

  // Detect whether the selected captain is connected to Armada over MCP so we can warn when it is not.
  useEffect(() => {
    if (!captainId) { setTools(null); setToolsLoading(false); return; }
    // Serve a previously-fetched result instantly; the probe is slow (it live-checks each configured
    // MCP server), so only pay for it once per captain per session.
    const cached = toolsCache.current[captainId];
    if (cached) { setTools(cached); setToolsLoading(false); return; }
    let active = true;
    setTools(null);
    setToolsLoading(true);
    getCaptainAskTools(captainId)
      .then((result) => { toolsCache.current[captainId] = result; if (active) setTools(result); })
      .catch(() => { if (active) setTools(null); })
      .finally(() => { if (active) setToolsLoading(false); });
    return () => { active = false; };
  }, [captainId]);

  const selectedCaptain = captains.find((c) => c.id === captainId) || null;
  const armadaMcpMissing = tools != null && tools.armadaToolCount <= 0 && (tools.availabilityVerified || tools.mcpConnectionPlanned);
  const armadaMcpUnverified = tools != null && !tools.availabilityVerified && !tools.mcpConnectionPlanned;

  function changeMode(next: 'temporary' | 'saved') {
    if (next === mode || busy) return;
    selectedThreadGeneration.current += 1;
    if (mode === 'temporary') { setTemporaryTurns(turns); setTemporaryCaptainId(captainId); }
    if (next === 'temporary') { setTurns(temporaryTurns); setCaptainId(temporaryCaptainId); setTurnStatusNotice(''); setInput(''); }
    else setThreadsLoading(true);
    setMode(next);
  }

  function updateStreamingTurn(mutate: (turn: ChatTurn) => ChatTurn) {
    setTurns((current) => {
      const copy = [...current];
      for (let index = copy.length - 1; index >= 0; index -= 1) {
        if (copy[index].role === 'assistant' && copy[index].streaming) { copy[index] = mutate(copy[index]); break; }
      }
      return copy;
    });
  }

  async function sendTemporary(message: string) {
    const text = message.trim();
    if (!text || busy || !captainId) return;
    recordPrompt(text);
    setInput('');
    const streaming = streamingEnabled;
    const turnId = streaming ? ((typeof crypto !== 'undefined' && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now()) + Math.random().toString(36).slice(2)) : undefined;
    const history: CaptainChatMessage[] = turns.filter((turn) => turn.role !== 'system')
      .map((turn) => ({ role: turn.role as 'user' | 'assistant', content: turn.text }));
    setTurns((current) => streaming
      ? [...current, { role: 'user', text }, { role: 'assistant', text: '', streaming: true }]
      : [...current, { role: 'user', text }]);
    setBusy(true);
    let unsubscribe: () => void = () => undefined;
    if (streaming) unsubscribe = subscribe((msg: WebSocketMessage) => {
      const data = msg.data as { turnId?: string; delta?: string } | undefined;
      if (data && data.turnId === turnId && data.delta && msg.type === 'ask.chunk') {
        const delta = data.delta;
        updateStreamingTurn((turn) => ({ ...turn, text: turn.text + delta }));
      } else if (data && data.turnId === turnId && data.delta && msg.type === 'ask.thinking') {
        const delta = data.delta;
        updateStreamingTurn((turn) => ({ ...turn, thinking: (turn.thinking ?? '') + delta }));
      } else if (msg.type === 'ask.tool') {
        const tool = msg.data as ({ turnId?: string } & Parameters<typeof applyToolEvent>[1]) | undefined;
        if (tool && tool.turnId === turnId && tool.id) {
          const toolEvent = tool;
          updateStreamingTurn((turn) => ({ ...turn, tools: applyToolEvent(turn.tools, toolEvent) }));
        }
      }
    });
    const controller = new AbortController();
    abortRef.current = controller;
    try {
      const response = await chatWithCaptain(captainId, { message: text, history, turnId, showThinking }, { signal: controller.signal });
      if (!response.success) {
        setError(response.error || t('The captain could not respond.'));
        setTurns((current) => current.filter((turn) => !(turn.role === 'assistant' && turn.streaming)).slice(0, -1));
      } else if (streaming) {
        updateStreamingTurn((turn) => ({ ...turn, streaming: false, text: response.reply, metrics: response.metrics, model: response.model, thinking: response.thinking ?? turn.thinking }));
      } else {
        setTurns((current) => [...current, { role: 'assistant', text: response.reply, metrics: response.metrics, model: response.model, thinking: response.thinking ?? undefined }]);
      }
    } catch (err: unknown) {
      if (err instanceof Error && err.name === 'AbortError') {
        if (streaming) updateStreamingTurn((turn) => ({ ...turn, streaming: false }));
      } else if (err instanceof Error && (err.name === 'NetworkError' || err.message === 'Request timed out')) {
        // A lost response does not prove the captain stopped. Keep received work and let the
        // operator choose the next question; repeating the request could run its tools twice.
        if (streaming) updateStreamingTurn((turn) => ({ ...turn, streaming: false }));
        setTurns((current) => [...current, {
          role: 'system',
          text: t('The connection ended before the reply was confirmed. The captain may have completed the request. Any partial reply is kept above. Check the result before sending the request again.'),
        }]);
        setError(err.message);
      } else {
        setError(err instanceof Error ? err.message : t('Chat failed.'));
        setTurns((current) => current.filter((turn) => !(turn.role === 'assistant' && turn.streaming)).slice(0, -1));
      }
    } finally {
      abortRef.current = null;
      unsubscribe();
      setBusy(false);
    }
  }

  async function send(message: string, retry?: { threadId: string; requestId: string; content: string; showThinking: boolean }) {
    if (mode === 'temporary') { await sendTemporary(message); return; }
    const text = message.trim();
    const threadId = retry?.threadId ?? selectedThreadId;
    if (!text || text.length > 32000 || busy || !captainId || !threadId) return;
    recordPrompt(text);
    saveAskDraft(userId, threadId, '');
    setInput('');
    setRetryRequest(null);
    setTurnStatusNotice('');

    const streaming = streamingEnabled;
    const requestId = retry?.requestId ?? (streaming
      ? ((typeof crypto !== 'undefined' && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now()) + Math.random().toString(36).slice(2))
      : ((typeof crypto !== 'undefined' && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now()) + Math.random().toString(36).slice(2)));
    const showTurnThinking = retry?.showThinking ?? showThinking;
    if (retry) {
      setTurns((current) => current.map((turn) => turn.id === `${requestId}:assistant` ? { ...turn, streaming: true } : turn));
    } else {
      setTurns((current) => [...current, { id: `${requestId}:user`, role: 'user', text }, { id: `${requestId}:assistant`, role: 'assistant', text: '', streaming: true }]);
    }
    setBusy(true);

    let unsubscribe: () => void = () => undefined;
    if (streaming) {
      unsubscribe = subscribe((msg: WebSocketMessage) => {
        if (msg.type === 'ask.chunk') {
          const data = msg.data as { turnId?: string; delta?: string } | undefined;
          if (!data || data.turnId !== requestId || !data.delta) return;
          setTurns((current) => current.map((turn) => turn.id === `${requestId}:assistant` ? { ...turn, text: turn.text + data.delta! } : turn));
        } else if (msg.type === 'ask.tool') {
          const d = msg.data as ({ turnId?: string } & Parameters<typeof applyToolEvent>[1]) | undefined;
          if (!d || d.turnId !== requestId || !d.id) return;
          setTurns((current) => current.map((turn) => turn.id === `${requestId}:assistant` ? { ...turn, tools: applyToolEvent(turn.tools, d) } : turn));
        } else if (msg.type === 'ask.thinking') {
          const data = msg.data as { turnId?: string; delta?: string } | undefined;
          if (!data || data.turnId !== requestId || !data.delta) return;
          setTurns((current) => current.map((turn) => turn.id === `${requestId}:assistant` ? { ...turn, thinking: (turn.thinking ?? '') + data.delta! } : turn));
        }
      });
    }

    // The stable request id makes a manual retry safe after a lost HTTP response.
    const controller = new AbortController();
    abortRef.current = controller;
    let remainsActive = false;

    try {
      const result = await sendAskTurn(threadId, { requestId, content: text, showThinking: showTurnThinking }, { signal: controller.signal });
      if (result.turn) {
        setTurns((current) => replaceAskTurn(current, result.turn!));
        setThreads((current) => current.map((thread) => thread.id === threadId ? { ...thread, activeTurnId: result.turn!.state === 'Running' || result.turn!.state === 'Pending' ? requestId : null } : thread));
        remainsActive = result.turn.state === 'Running' || result.turn.state === 'Pending';
        if (result.turn.state === 'Uncertain') setTurnStatusNotice(t('This turn may not have completed. Armada will not replay it. Start a new conversation to continue.'));
      } else {
        const message = result.message || t('The captain could not respond.');
        setError(message);
        setTurns((current) => current.map((turn) => turn.id === `${requestId}:assistant` ? { ...turn, text: message, streaming: false } : turn));
      }
    } catch (err: unknown) {
      try {
        const stored = await getAskTurn(threadId, requestId);
        setTurns((current) => replaceAskTurn(current, stored));
        remainsActive = stored.state === 'Running' || stored.state === 'Pending';
        if (stored.state === 'Uncertain') setTurnStatusNotice(t('This turn may not have completed. Armada will not replay it. Start a new conversation to continue.'));
      } catch {
        const message = err instanceof Error ? err.message : t('Chat status is unknown.');
        setRetryRequest({ threadId, requestId, content: text, showThinking: showTurnThinking });
        setTurnStatusNotice(t('The request status could not be read. Retry with the same request id to avoid starting a duplicate turn.'));
        if (!(err instanceof Error && err.name === 'AbortError')) setError(message);
      }
    } finally {
      abortRef.current = null;
      unsubscribe();
      setBusy(remainsActive);
      try {
        const thread = await getAskThread(threadId);
        setThreads((current) => current.map((item) => item.id === threadId ? thread : item));
        if (threadId === selectedThreadIdRef.current) void readOpenThread(thread);
      } catch { /* the durable turn view remains available on the next reload */ }
    }
  }

  // Abort the in-flight turn. The dropped request cancels the captain runtime server-side.
  function stop() {
    abortRef.current?.abort();
  }

  return (
    <div className="ask-page">
      <div className="view-header">
        <div>
          <h2>{t('Ask Armada')}</h2>
          <p className="text-dim view-subtitle">{mode === 'saved' ? t('Resume a saved conversation with its stored history.') : t('Chat directly with a captain. This temporary conversation stays in this browser session.')}</p>
        </div>
        <div className="ask-header-controls">
          <div className="ask-mode-toggle" role="tablist" aria-label={t('Ask conversation mode')}>
            <button role="tab" aria-selected={mode === 'temporary'} className={mode === 'temporary' ? 'active' : ''} onClick={() => changeMode('temporary')} disabled={busy}>{t('Temporary chat')}</button>
            <button role="tab" aria-selected={mode === 'saved'} className={mode === 'saved' ? 'active' : ''} onClick={() => changeMode('saved')} disabled={busy}>{t('Saved conversations')}</button>
          </div>
          <label className="ask-stream-toggle" title={t('Stream the reply token-by-token as it is produced')}>
            <input type="checkbox" checked={streamingEnabled} onChange={(e) => setStreamingEnabled(e.target.checked)} disabled={busy} />
            {t('Stream responses')}
          </label>
          <label className="ask-stream-toggle" title={t('Surface the model reasoning above the answer. Mux streams it natively; other runtimes are asked to include it.')}>
            <input type="checkbox" checked={showThinking} onChange={(e) => setShowThinking(e.target.checked)} disabled={busy} />
            {t('Show thinking')}
          </label>
          <label className="text-dim" style={{ display: 'flex', flexDirection: 'column', gap: '0.25rem', fontSize: '0.8rem' }}>
            {t('Captain')}
            <select value={captainId} onChange={(e) => {
              setCaptainId(e.target.value);
              if (mode === 'saved' && selectedThread) void changeThread(selectedThread, { setCaptainId: true, captainId: e.target.value || null });
            }} disabled={busy || (mode === 'saved' && !selectedThread)}>
              {captains.length === 0 && <option value="">{t('No captains available')}</option>}
              {captains.map((c) => (
                <option key={c.id} value={c.id}>{c.name} ({c.model || c.runtime})</option>
              ))}
            </select>
          </label>
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />
      <DeleteProgressDialog state={deleteProgress.state} onRetry={deleteProgress.retry} onDismiss={deleteProgress.dismiss} />
      <ConfirmDialog open={confirm.open} title={confirm.title} message={confirm.message} danger confirmLabel="Delete"
        onConfirm={confirm.onConfirm} onCancel={() => setConfirm((current) => ({ ...current, open: false }))} />

      <div
        ref={workspaceRef}
        className={listResize.dragging ? 'ask-workspace is-resizing' : 'ask-workspace'}
        style={listResize.width !== null ? { ['--ask-list-width' as string]: `${listResize.width}px` } : undefined}
      >
        {mode === 'saved' && <AskThreadShelf
          shelfRef={shelfRef}
          threads={threads}
          selectedId={selectedThreadId}
          includeArchived={includeArchived}
          loading={threadsLoading || busy}
          t={t}
          onSelect={(thread) => { selectedThreadIdRef.current = thread.id; selectedThreadGeneration.current += 1; setSelectedThreadId(thread.id); setCaptainId(thread.captainId ?? ''); }}
          onCreate={() => { void createThread(); }}
          onRename={(thread) => { void renameThread(thread); }}
          onPin={(thread) => { void changeThread(thread, { setPinned: true, pinned: !thread.pinned }); }}
          onArchive={(thread) => { void changeThread(thread, { setArchived: true, archived: !thread.archived }); }}
          onToggleUnread={(thread) => { void toggleUnread(thread); }}
          deleteSelection={deleteSelection}
          onToggleDeleteSelection={(thread) => setDeleteSelection((current) => current.includes(thread.id) ? current.filter((id) => id !== thread.id) : [...current, thread.id])}
          onDelete={deleteThread}
          onDeleteSelected={deleteSelectedThreads}
          onToggleArchived={() => setIncludeArchived((value) => !value)}
        />}
        {mode === 'saved' && (
          <div
            className="ask-list-splitter"
            role="separator"
            aria-orientation="vertical"
            aria-label={t('Resize conversation list')}
            aria-valuemin={ASK_LIST_MIN_WIDTH}
            aria-valuemax={ASK_LIST_MAX_WIDTH}
            aria-valuenow={listResize.width ?? undefined}
            tabIndex={0}
            {...listResize.splitterProps}
          />
        )}
        <div className="ask-body">
          {mode === 'saved' && selectedThread?.archived && <div className="text-dim" role="status">{t('This conversation is archived. Restore it to send another turn.')}</div>}
          {mode === 'saved' && turnStatusNotice && <div className="ask-turn-status" role="status">{turnStatusNotice}</div>}
          {mode === 'saved' && selectedThread && <AskActionCards key={selectedThread.id} threadId={selectedThread.id} busy={busy} t={t} />}
          {mode === 'saved' && selectedThread && <AskToolPermissionCards key={selectedThread.id} threadId={selectedThread.id} busy={busy} t={t} />}
          {mode === 'saved' && retryRequest && (
            <div className="ask-turn-status" role="alert">
              <span>{turnStatusNotice}</span>
              <button className="btn btn-sm" disabled={busy} onClick={() => { void send(retryRequest.content, retryRequest); }}>{t('Retry same turn')}</button>
            </div>
          )}
          {toolsLoading && (
            <div className="text-dim" style={{ fontSize: '0.78rem', marginBottom: '0.6rem' }}>
              {t('Checking whether this captain is connected to Armada over MCP...')}
            </div>
          )}
          {armadaMcpUnverified && (
            <div className="text-dim" style={{ fontSize: '0.78rem', marginBottom: '0.6rem' }}>
              {tools.summary || t('Armada MCP connection status is not verified for this captain.')}
            </div>
          )}
          {armadaMcpMissing && (
            <div className="mcp-warning-banner">
              <span className="mcp-warning-icon" aria-hidden="true">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
                  <path d="M12 9v4" />
                  <path d="M12 17h.01" />
                </svg>
              </span>
              <div className="mcp-warning-body">
                <strong>{t(tools?.mcpConnectionPlanned ? 'Ask MCP preflight found no Armada tools.' : 'This captain is not connected to Armada over MCP.')}</strong>
                <span className="text-dim">
                  {tools?.mcpConnectionPlanned
                    ? tools.summary
                    : t('It cannot call Armada tools (fleet, missions, voyages, and more). Add the Armada MCP server to this captain’s runtime config to connect it.')}
                </span>
              </div>
              {!tools?.mcpConnectionPlanned && (
                <div className="mcp-warning-actions">
                  <button className="btn btn-sm" onClick={() => navigate('/captains/' + captainId)}>{t('View captain')}</button>
                  <a className="btn btn-sm" href={instructionsDocUrl(selectedCaptain?.runtime)} target="_blank" rel="noopener noreferrer">{t('How to connect')}</a>
                </div>
              )}
            </div>
          )}

          {mode === 'saved' && hasEarlierTurns && (
            <button className="btn btn-sm ask-load-earlier" onClick={() => { void loadEarlierTurns(); }} disabled={loadingEarlier || busy}>
              {loadingEarlier ? t('Loading…') : t('Load earlier turns')}
            </button>
          )}
          {captains.length === 0 ? (
            <div className="card" style={{ padding: '1.5rem', textAlign: 'center' }}>
              <p className="text-dim">{t('No captains are configured yet. Create a captain to Ask Armada.')}</p>
            </div>
          ) : mode === 'saved' && !selectedThread ? (
            <div className="card" style={{ padding: '1.5rem', textAlign: 'center' }}>
              <p className="text-dim">{t('Create a conversation to begin.')}</p>
              <button className="btn btn-primary" onClick={() => { void createThread(); }} disabled={threadsLoading}>{t('New conversation')}</button>
            </div>
          ) : <CaptainChatPanel
            t={t}
            turns={turns}
            notice={selectedCaptain?.runtime === 'Codex' ? t('Codex responses cannot be streamed and will arrive upon completion.') : undefined}
            assistantName={selectedCaptain?.name}
            emptyState={(
              <div className="ask-empty">
                <p className="ask-empty-greeting">{greeting}</p>
                <p className="ask-empty-sub">{selectedCaptain ? t('Chatting with {{name}}', { name: selectedCaptain.name }) : t('Select a captain to begin.')}</p>
              </div>
            )}
            input={input}
            onInputChange={changeInput}
            onInputKeyDown={promptHistory.onKeyDown}
            onSend={() => send(input)}
            onStop={stop}
            busy={busy}
            canSend={!busy && !!captainId && (mode === 'temporary' || (!!selectedThread && !selectedThread.archived)) && input.trim().length > 0}
            canStop={busy}
            thinking={thinking}
            inputPlaceholder={t('Message the captain...')}
            inputMaxLength={32000}
            inputDisabled={!captainId || (mode === 'saved' && (!selectedThread || selectedThread.archived))}
            onClear={mode === 'temporary' ? () => { setTemporaryTurns([]); setTurns([]); setThinking(''); setError(''); } : undefined}
            clearDisabled={busy || turns.length === 0}
            toolRuntimeLabel={selectedCaptain?.runtime || tools?.runtime || undefined}
          />}
        </div>
      </div>
    </div>
  );
}
