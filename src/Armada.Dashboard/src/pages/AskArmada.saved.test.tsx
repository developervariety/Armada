import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';
import AskArmada from './AskArmada';
import { createAskThread, deleteAskThread, deleteAskThreads, getAskThread, getAskTurn, getCaptainAskTools, listAskThreads, listAskTurns, listCaptains, markAskThreadRead, markAskThreadUnread, sendAskTurn, updateAskThread } from '../api/client';
import type { AskThread, AskTurn, Captain, CaptainChatMetrics, CaptainToolAccessResult } from '../types/models';
import type { ChatTurn } from '../components/shared/CaptainChatPanel';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  createAskThread: vi.fn(), deleteAskThread: vi.fn(), deleteAskThreads: vi.fn(), getAskThread: vi.fn(), getAskTurn: vi.fn(), getCaptainAskTools: vi.fn(),
  listAskThreads: vi.fn(), listAskTurns: vi.fn(), listCaptains: vi.fn(), markAskThreadRead: vi.fn(), markAskThreadUnread: vi.fn(), sendAskTurn: vi.fn(), updateAskThread: vi.fn(),
  listAskProposals: vi.fn().mockResolvedValue([]), listAskWorkReports: vi.fn().mockResolvedValue([]), decideAskProposal: vi.fn(),
  listAskToolPermissions: vi.fn().mockResolvedValue([]), listAskToolPermissionRules: vi.fn().mockResolvedValue([]),
  decideAskToolPermission: vi.fn(), deleteAskToolPermissionRule: vi.fn(),
}));
vi.mock('../context/LocaleContext', () => {
  const context = { t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v };
  return { useLocale: () => context };
});
vi.mock('../context/WebSocketContext', () => ({ useWebSocket: () => ({ subscribe: () => () => undefined }) }));
vi.mock('../components/shared/CaptainChatPanel', () => ({ default: (props: MockPanelProps) => (
  <div data-testid="chat-panel">{props.emptyState}{props.notice}{props.turns.map((turn) => <div key={turn.id}><p>{turn.text}</p>{turn.thinking && <span>{turn.thinking}</span>}</div>)}
    <input aria-label="Message" value={props.input} onChange={(event) => props.onInputChange(event.target.value)} />
    <button disabled={!props.canSend} onClick={props.onSend}>Send</button>
  </div>
) }));
vi.mock('../components/askGreetings', () => ({ randomGreeting: () => 'Ahoy! How can Armada help?' }));
vi.mock('../components/shared/ErrorModal', () => ({ default: (props: { error: string }) => props.error ? <div role="alert">{props.error}</div> : null }));

interface MockPanelProps {
  emptyState?: ReactNode;
  notice?: ReactNode;
  turns: ChatTurn[];
  input: string;
  onInputChange: (value: string) => void;
  onSend: () => void;
  canSend: boolean;
}

const captain = { id: 'cpt_test', name: 'Test captain', runtime: 'ClaudeCode', model: 'test' } as Captain;
const thread: AskThread = { id: 'ath_test', tenantId: 'ten_test', userId: 'usr_test', title: 'Fleet status', captainId: captain.id, pinned: false, archived: false, createdUtc: '', lastActivityUtc: '', activeTurnId: null, unreadCount: 0 };
const metrics: CaptainChatMetrics = { timeToFirstTokenMs: 10, streamingMs: 20, totalMs: 30, promptTokens: 4, completionTokens: 5, totalTokens: 9, tokensPerSecond: 2 };
const page = { success: true, pageNumber: 1, pageSize: 100, totalPages: 1, totalRecords: 1, totalMs: 1, objects: [captain] };
const turn: AskTurn = { threadId: thread.id, requestId: 'ask_req_1', sequence: 1, captainId: captain.id, content: 'Which captains are stalled?', showThinking: false, state: 'Completed', response: { success: true, reply: 'No captains are stalled.', model: 'test', metrics, error: null, thinking: 'I checked.' }, createdUtc: '', completedUtc: '' };
const threadList = [thread];
const secondThread: AskThread = { ...thread, id: 'ath_second', title: 'Second conversation' };

function savedTurn(sequence: number, threadId = thread.id): AskTurn {
  return {
    ...turn,
    threadId,
    requestId: `ask_req_${sequence}`,
    sequence,
    content: `question-${sequence}`,
    response: { success: true, reply: `reply-${sequence}`, model: 'test', metrics, error: null, thinking: null },
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => { resolve = complete; });
  return { promise, resolve };
}

function renderPage(entry = '/') { return render(<MemoryRouter initialEntries={[entry]}><AskArmada /></MemoryRouter>); }
async function renderSavedPage() {
  renderPage();
  fireEvent.click(await screen.findByRole('tab', { name: 'Saved conversations' }));
}
function setup() {
  vi.mocked(listCaptains).mockResolvedValue(page as never);
  vi.mocked(listAskThreads).mockResolvedValue(threadList as never);
  vi.mocked(getAskThread).mockResolvedValue(thread as never);
  vi.mocked(listAskTurns).mockResolvedValue([turn] as never);
  vi.mocked(getAskTurn).mockResolvedValue(turn as never);
  vi.mocked(getCaptainAskTools).mockResolvedValue({ mcpConnectionPlanned: true, availabilityVerified: true, armadaToolCount: 3, summary: '' } as CaptainToolAccessResult);
}

describe('Ask Armada saved conversations', () => {
  beforeEach(() => { vi.clearAllMocks(); localStorage.clear(); setup(); });

  it('loads saved turns and keeps thread metadata edits in the service', async () => {
    await renderSavedPage();
    expect(await screen.findByText('No captains are stalled.')).toBeInTheDocument();
    expect(screen.getByText('I checked.')).toBeInTheDocument();
    const renamed = { ...thread, title: 'Renamed thread' };
    vi.spyOn(window, 'prompt').mockReturnValue('Renamed thread');
    vi.mocked(updateAskThread).mockResolvedValue(renamed as never);
    vi.mocked(listAskThreads).mockResolvedValue([renamed] as never);
    fireEvent.click(screen.getByLabelText('Rename conversation'));
    await waitFor(() => expect(updateAskThread).toHaveBeenCalledWith(thread.id, { setTitle: true, title: 'Renamed thread' }));
    fireEvent.click(screen.getByLabelText('Pin conversation'));
    await waitFor(() => expect(updateAskThread).toHaveBeenCalledWith(thread.id, { setPinned: true, pinned: true }));
    fireEvent.click(screen.getByLabelText('Archive conversation'));
    await waitFor(() => expect(updateAskThread).toHaveBeenCalledWith(thread.id, { setArchived: true, archived: true }));
  });

  it('offers delete only on archived conversations, behind a confirmation and a progress dialog', async () => {
    const archived: AskThread = { ...secondThread, archived: true };
    vi.mocked(listAskThreads).mockResolvedValue([thread, archived] as never);
    const pendingDelete = deferred<void>();
    vi.mocked(deleteAskThread).mockReturnValue(pendingDelete.promise as never);
    await renderSavedPage();
    expect(await screen.findByText('Second conversation')).toBeInTheDocument();
    expect(screen.getAllByLabelText('Delete conversation')).toHaveLength(1);

    vi.mocked(listAskThreads).mockResolvedValue([thread] as never);
    fireEvent.click(screen.getByLabelText('Delete conversation'));
    expect(deleteAskThread).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(deleteAskThread).toHaveBeenCalledWith(archived.id));
    expect(await screen.findByText('This may take some time.')).toBeInTheDocument();
    pendingDelete.resolve();
    await waitFor(() => expect(screen.queryByText('Second conversation')).not.toBeInTheDocument());
    expect(screen.queryByText('This may take some time.')).not.toBeInTheDocument();
  });

  it('deletes the ticked archived conversations in one request and names each one that was kept', async () => {
    const archivedA: AskThread = { ...secondThread, id: 'ath_a', title: 'Old A', archived: true };
    const archivedB: AskThread = { ...secondThread, id: 'ath_b', title: 'Old B', archived: true };
    vi.mocked(listAskThreads).mockResolvedValue([archivedA, archivedB] as never);
    vi.mocked(getAskThread).mockImplementation(async (id: string) => (id === archivedB.id ? archivedB : archivedA) as never);
    vi.mocked(deleteAskThreads).mockResolvedValue({ deleted: 1, skipped: [{ id: 'ath_b', reason: 'The conversation has an active turn.' }] } as never);
    await renderSavedPage();
    await screen.findByText('Old A');
    const boxes = screen.getAllByLabelText('Select conversation for deletion');
    boxes.forEach((box) => fireEvent.click(box));
    vi.mocked(listAskThreads).mockResolvedValue([archivedB] as never);
    fireEvent.click(screen.getByRole('button', { name: /Delete selected \(2\)/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(deleteAskThreads).toHaveBeenCalledWith(['ath_a', 'ath_b']));
    expect(await screen.findByRole('alert')).toHaveTextContent('Old B: The conversation has an active turn.');
  });

  it('opens the saved conversation named by an inbox link without choosing the tab first', async () => {
    vi.mocked(listAskThreads).mockResolvedValue([thread, secondThread] as never);
    vi.mocked(getAskThread).mockImplementation(async (id: string) => (id === secondThread.id ? secondThread : thread) as never);
    renderPage('/ask?thread=' + secondThread.id);
    await waitFor(() => expect(getAskThread).toHaveBeenCalledWith(secondThread.id));
    expect(getAskThread).not.toHaveBeenCalledWith(thread.id);
    expect(screen.getByRole('tab', { name: 'Saved conversations' })).toHaveAttribute('aria-selected', 'true');
  });

  it('creates a saved conversation with the selected captain', async () => {
    const created = { ...thread, id: 'ath_new', title: 'New conversation' };
    vi.mocked(createAskThread).mockResolvedValue(created as never);
    await renderSavedPage();
    fireEvent.click(await screen.findByText('New'));
    await waitFor(() => expect(createAskThread).toHaveBeenCalledWith({ captainId: captain.id }));
    expect(await screen.findByText('New conversation')).toBeInTheDocument();
  });

  it('sends no client history and renders the durable response and metrics', async () => {
    vi.mocked(listAskTurns).mockResolvedValue([] as never);
    vi.mocked(sendAskTurn).mockResolvedValue({ statusCode: 200, turn, message: null } as never);
    await renderSavedPage();
    fireEvent.change(await screen.findByLabelText('Message'), { target: { value: 'Which captains are stalled?' } });
    fireEvent.click(screen.getByText('Send'));
    await screen.findByText('No captains are stalled.');
    const request = vi.mocked(sendAskTurn).mock.calls[0][1];
    expect(request.content).toBe('Which captains are stalled?');
    expect(request.requestId).toMatch(/^[-a-z0-9]+$/i);
    expect(request).not.toHaveProperty('history');
    expect(screen.getByText('I checked.')).toBeInTheDocument();
  });

  it('shows an explicit uncertain state and does not resend it on reload', async () => {
    const uncertain = { ...turn, state: 'Uncertain' as const, response: null };
    vi.mocked(getAskThread).mockResolvedValue({ ...thread, activeTurnId: turn.requestId } as never);
    vi.mocked(listAskTurns).mockResolvedValue([uncertain] as never);
    vi.mocked(getAskTurn).mockResolvedValue(uncertain as never);
    await renderSavedPage();
    expect(await screen.findByText(/Armada will not replay it/)).toBeInTheDocument();
    expect(sendAskTurn).not.toHaveBeenCalled();
  });

  it('loads three history pages by the earliest stored sequence', async () => {
    vi.mocked(listAskTurns).mockImplementation(async (_id, beforeSequence, pageSize) => {
      const all = Array.from({ length: 205 }, (_value, index) => savedTurn(index + 1));
      const eligible = beforeSequence == null ? all : all.filter((item) => item.sequence < beforeSequence);
      return eligible.slice(-(pageSize ?? 100));
    });
    await renderSavedPage();
    await screen.findByText('reply-106');
    fireEvent.click(screen.getByText('Load earlier turns'));
    await screen.findByText('reply-6');
    fireEvent.click(screen.getByText('Load earlier turns'));
    await screen.findByText('reply-1');
    expect(listAskTurns).toHaveBeenCalledWith(thread.id, 106, 100);
    expect(listAskTurns).toHaveBeenCalledWith(thread.id, 6, 100);
    expect(screen.queryByText('Load earlier turns')).not.toBeInTheDocument();
  });

  it('ignores an earlier-page response after switching conversations', async () => {
    const earlier = deferred<AskTurn[]>();
    vi.mocked(listAskThreads).mockResolvedValue([thread, secondThread] as never);
    vi.mocked(getAskThread).mockImplementation(async (id) => (id === secondThread.id ? secondThread : thread) as never);
    vi.mocked(listAskTurns).mockImplementation(async (id, beforeSequence) => {
      if (id === thread.id && beforeSequence == null) return Array.from({ length: 100 }, (_, index) => savedTurn(101 + index)) as never;
      if (id === thread.id) return earlier.promise as never;
      return [savedTurn(201, secondThread.id)] as never;
    });
    await renderSavedPage();
    await screen.findByText('reply-101');
    fireEvent.click(screen.getByText('Load earlier turns'));
    await waitFor(() => expect(listAskTurns).toHaveBeenCalledWith(thread.id, 101, 100));
    fireEvent.click(screen.getByText(secondThread.title));
    await screen.findByText('reply-201');
    earlier.resolve([savedTurn(1)]);
    await waitFor(() => expect(screen.queryByText('reply-1')).not.toBeInTheDocument());
    expect(screen.getByText('reply-201')).toBeInTheDocument();
  });

  it('keeps a late saved-history page out of temporary chat', async () => {
    const earlier = deferred<AskTurn[]>();
    vi.mocked(listAskTurns).mockImplementation(async (_id, beforeSequence) => beforeSequence == null
      ? Array.from({ length: 100 }, (_, index) => savedTurn(101 + index)) as never
      : earlier.promise as never);
    await renderSavedPage();
    await screen.findByText('reply-101');
    fireEvent.click(screen.getByText('Load earlier turns'));
    await waitFor(() => expect(listAskTurns).toHaveBeenCalledWith(thread.id, 101, 100));
    fireEvent.click(screen.getByRole('tab', { name: 'Temporary chat' }));
    await act(async () => { earlier.resolve([savedTurn(1)]); });
    expect(screen.queryByText('reply-1')).not.toBeInTheDocument();
    expect(screen.queryByText('reply-101')).not.toBeInTheDocument();
  });

  it('restores the temporary conversation captain after visiting saved history', async () => {
    const savedCaptain = { ...captain, id: 'cpt_saved', name: 'Saved captain' };
    const savedThread = { ...thread, captainId: savedCaptain.id };
    vi.mocked(listCaptains).mockResolvedValue({ ...page, totalRecords: 2, objects: [captain, savedCaptain] } as never);
    vi.mocked(listAskThreads).mockResolvedValue([savedThread] as never);
    vi.mocked(getAskThread).mockResolvedValue(savedThread as never);
    renderPage();
    const selector = await screen.findByLabelText('Captain');
    await waitFor(() => expect(selector).toHaveValue(captain.id));
    fireEvent.click(screen.getByRole('tab', { name: 'Saved conversations' }));
    await waitFor(() => expect(selector).toHaveValue(savedCaptain.id));
    fireEvent.click(screen.getByRole('tab', { name: 'Temporary chat' }));
    expect(selector).toHaveValue(captain.id);
  });

  it('retries a lost HTTP response with the same request id', async () => {
    vi.mocked(listAskTurns).mockResolvedValue([] as never);
    vi.mocked(sendAskTurn).mockRejectedValueOnce(new Error('connection lost'));
    vi.mocked(getAskTurn).mockRejectedValueOnce(Object.assign(new Error('not found'), { status: 404 }));
    vi.mocked(sendAskTurn).mockResolvedValueOnce({ statusCode: 200, turn, message: null } as never);
    await renderSavedPage();
    fireEvent.change(await screen.findByLabelText('Message'), { target: { value: 'Question' } });
    fireEvent.click(screen.getByText('Send'));
    const retry = await screen.findByText('Retry same turn');
    const requestId = vi.mocked(sendAskTurn).mock.calls[0][1].requestId;
    fireEvent.click(retry);
    await waitFor(() => expect(sendAskTurn).toHaveBeenCalledTimes(2));
    expect(vi.mocked(sendAskTurn).mock.calls[1][1].requestId).toBe(requestId);
  });

  it('keeps one unsent draft per conversation and reopens the conversation last open', async () => {
    vi.mocked(listAskThreads).mockResolvedValue([thread, secondThread] as never);
    vi.mocked(getAskThread).mockImplementation(async (id: string) => (id === secondThread.id ? secondThread : thread) as never);
    vi.mocked(listAskTurns).mockResolvedValue([] as never);
    const first = renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: 'Saved conversations' }));
    fireEvent.change(await screen.findByLabelText('Message'), { target: { value: 'draft for the first' } });
    fireEvent.click(screen.getByText(secondThread.title));
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue(''));
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'draft for the second' } });
    fireEvent.click(screen.getByText(thread.title));
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue('draft for the first'));
    fireEvent.click(screen.getByText(secondThread.title));
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue('draft for the second'));

    first.unmount();
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: 'Saved conversations' }));
    await waitFor(() => expect(getAskThread).toHaveBeenLastCalledWith(secondThread.id));
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue('draft for the second'));
  });

  it('reads a conversation when it opens, and a conversation marked unread stays unread while it is open', async () => {
    const unread = { ...thread, unreadCount: 2 };
    vi.mocked(listAskThreads).mockResolvedValue([unread] as never);
    vi.mocked(getAskThread).mockResolvedValue(unread as never);
    vi.mocked(listAskTurns).mockResolvedValue([] as never);
    vi.mocked(markAskThreadRead).mockResolvedValue({ ...thread, unreadCount: 0 } as never);
    vi.mocked(markAskThreadUnread).mockResolvedValue({ ...thread, unreadCount: 1 } as never);
    await renderSavedPage();
    await waitFor(() => expect(markAskThreadRead).toHaveBeenCalledWith(thread.id));
    expect(await screen.findByLabelText('Mark conversation unread')).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText('Mark conversation unread'));
    await waitFor(() => expect(markAskThreadUnread).toHaveBeenCalledWith(thread.id));
    expect(await screen.findByLabelText('Unread reply')).toBeInTheDocument();

    // A reply that finishes in the open conversation leaves it unread.
    vi.mocked(getAskThread).mockResolvedValue({ ...thread, unreadCount: 1 } as never);
    vi.mocked(sendAskTurn).mockResolvedValue({ statusCode: 200, turn, message: null } as never);
    const threadReads = vi.mocked(getAskThread).mock.calls.length;
    fireEvent.change(await screen.findByLabelText('Message'), { target: { value: 'Another question' } });
    fireEvent.click(screen.getByText('Send'));
    await screen.findByText('No captains are stalled.');
    await waitFor(() => expect(vi.mocked(getAskThread).mock.calls.length).toBeGreaterThan(threadReads));
    await act(async () => { await Promise.resolve(); });
    expect(markAskThreadRead).toHaveBeenCalledTimes(1);
  });
});
