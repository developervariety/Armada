import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, expect, test, vi } from 'vitest';

vi.mock('../api/client', () => ({ listAskThreads: vi.fn() }));

import { listAskThreads } from '../api/client';
import { notifyAskUnreadChanged, useAskUnreadCount } from './useAskUnreadCount';

beforeEach(() => vi.mocked(listAskThreads).mockReset());

test('counts the conversations that hold an unread reply and recounts when the Ask page reports a change', async () => {
  vi.mocked(listAskThreads).mockResolvedValue([{ unreadCount: 2 }, { unreadCount: 0 }, { unreadCount: 1 }] as never);
  const { result } = renderHook(() => useAskUnreadCount(true));
  await waitFor(() => expect(result.current).toBe(2));
  vi.mocked(listAskThreads).mockResolvedValue([{ unreadCount: 0 }] as never);
  act(() => notifyAskUnreadChanged());
  await waitFor(() => expect(result.current).toBe(0));
});

test('makes no request when the caller may not use the Ask routes', async () => {
  const { result } = renderHook(() => useAskUnreadCount(false));
  expect(result.current).toBe(0);
  expect(listAskThreads).not.toHaveBeenCalled();
});
