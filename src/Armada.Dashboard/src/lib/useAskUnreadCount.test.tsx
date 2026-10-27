import { act, renderHook, waitFor } from '@testing-library/react';
import type { AskThread, EnumerationResult, WebSocketMessage } from '../types/models';

const enumerateAskThreads = vi.fn();
let socketHandler: ((msg: WebSocketMessage) => void) | null = null;

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  enumerateAskThreads: (...args: unknown[]) => enumerateAskThreads(...args),
}));

vi.mock('../context/WebSocketContext', () => ({
  useWebSocket: () => ({
    subscribe: (handler: (msg: WebSocketMessage) => void) => {
      socketHandler = handler;
      return () => { socketHandler = null; };
    },
  }),
}));

import { countUnreadAskThreads, useAskUnreadCount } from './useAskUnreadCount';

function thread(id: string, unreadCount?: number, archived = false): AskThread {
  return { id, archived, ...(unreadCount === undefined ? {} : { unreadCount }) } as AskThread;
}

function page(objects: AskThread[], pageNumber = 1, totalPages = 1): EnumerationResult<AskThread> {
  return { success: true, pageNumber, pageSize: 100, totalPages, totalRecords: objects.length, objects, totalMs: 1 };
}

describe('countUnreadAskThreads', () => {
  it('counts conversations, not messages, and ignores 0, undefined and archived threads', () => {
    expect(countUnreadAskThreads([
      thread('a', 5),
      thread('b', 1),
      thread('c', 0),
      thread('d'),
      thread('e', 3, true),
    ])).toBe(2);
  });

  it('returns 0 for missing input', () => {
    expect(countUnreadAskThreads(undefined)).toBe(0);
    expect(countUnreadAskThreads(null)).toBe(0);
    expect(countUnreadAskThreads([])).toBe(0);
  });
});

describe('useAskUnreadCount', () => {
  beforeEach(() => {
    enumerateAskThreads.mockReset();
    socketHandler = null;
  });

  it('counts threads with unreadCount > 0 from the non-archived thread list', async () => {
    enumerateAskThreads.mockResolvedValue(page([thread('a', 2), thread('b', 0), thread('c'), thread('d', 7)]));
    const { result } = renderHook(() => useAskUnreadCount());
    await waitFor(() => expect(result.current).toBe(2));
    expect(enumerateAskThreads).toHaveBeenCalledWith({ pageNumber: 1, pageSize: 100, includeArchived: false });
  });

  it('reads every page of the thread list', async () => {
    enumerateAskThreads
      .mockResolvedValueOnce(page([thread('a', 1)], 1, 2))
      .mockResolvedValueOnce(page([thread('b', 4), thread('c', 0)], 2, 2));
    const { result } = renderHook(() => useAskUnreadCount());
    await waitFor(() => expect(result.current).toBe(2));
    expect(enumerateAskThreads).toHaveBeenCalledTimes(2);
    expect(enumerateAskThreads).toHaveBeenLastCalledWith({ pageNumber: 2, pageSize: 100, includeArchived: false });
  });

  it('returns 0 when the API fails', async () => {
    enumerateAskThreads.mockResolvedValueOnce(page([thread('a', 1)]));
    const { result } = renderHook(() => useAskUnreadCount());
    await waitFor(() => expect(result.current).toBe(1));

    enumerateAskThreads.mockRejectedValue(new Error('boom'));
    await act(async () => {
      socketHandler?.({ type: 'ask.thread', data: { threadId: 'a', thread: { id: 'a', unreadCount: 0 } } } as WebSocketMessage);
      await new Promise((resolve) => setTimeout(resolve, 600));
    });
    expect(result.current).toBe(0);
  });

  it('returns 0 when the first load fails', async () => {
    enumerateAskThreads.mockRejectedValue(new Error('offline'));
    const { result } = renderHook(() => useAskUnreadCount());
    await waitFor(() => expect(enumerateAskThreads).toHaveBeenCalled());
    expect(result.current).toBe(0);
  });

  it('treats an empty or malformed result as 0', async () => {
    enumerateAskThreads.mockResolvedValue({} as EnumerationResult<AskThread>);
    const { result } = renderHook(() => useAskUnreadCount());
    await waitFor(() => expect(enumerateAskThreads).toHaveBeenCalled());
    expect(result.current).toBe(0);
  });

  it('refreshes after an Ask thread event and ignores unrelated events', async () => {
    enumerateAskThreads.mockResolvedValue(page([]));
    const { result } = renderHook(() => useAskUnreadCount());
    await waitFor(() => expect(enumerateAskThreads).toHaveBeenCalledTimes(1));

    await act(async () => {
      socketHandler?.({ type: 'mission.changed', data: { id: 'msn_1' } } as WebSocketMessage);
      await new Promise((resolve) => setTimeout(resolve, 600));
    });
    expect(enumerateAskThreads).toHaveBeenCalledTimes(1);

    enumerateAskThreads.mockResolvedValue(page([thread('a', 3)]));
    await act(async () => {
      socketHandler?.({ type: 'ask.thread', data: { threadId: 'a', thread: { id: 'a', unreadCount: 3 } } } as WebSocketMessage);
      await new Promise((resolve) => setTimeout(resolve, 600));
    });
    await waitFor(() => expect(result.current).toBe(1));
    expect(enumerateAskThreads).toHaveBeenCalledTimes(2);
  });
});
