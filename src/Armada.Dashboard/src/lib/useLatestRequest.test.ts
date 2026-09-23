import { act, renderHook } from '@testing-library/react';
import { useCallback, useEffect, useState } from 'react';
import { expect, test, vi } from 'vitest';
import { deferred, type Deferred } from '../test/routeRace';
import { DEFAULT_AUTO_REFRESH_SECONDS, useAutoRefresh } from './useAutoRefresh';
import { useLatestRequest } from './useLatestRequest';

test('only the most recently started load is current', () => {
  const { result } = renderHook(() => useLatestRequest());
  const first = result.current.begin('a');
  const second = result.current.begin('b');
  expect(first.isCurrent()).toBe(false);
  expect(second.isCurrent()).toBe(true);
});

test('a load is initial until a load for the same key succeeds', () => {
  const { result } = renderHook(() => useLatestRequest());
  const first = result.current.begin('a');
  expect(first.isInitialLoad).toBe(true);
  first.markLoaded();
  expect(result.current.begin('a').isInitialLoad).toBe(false);
  expect(result.current.begin('b').isInitialLoad).toBe(true);
});

test('a superseded load cannot mark its key as loaded', () => {
  const { result } = renderHook(() => useLatestRequest());
  const stale = result.current.begin('a');
  result.current.begin('b');
  stale.markLoaded();
  expect(result.current.begin('a').isInitialLoad).toBe(true);
});

// A list page's load: a filter change and the auto-refresh timer both call it, and only the newest load writes.
function useGuardedList(fetchRows: (filter: string) => Promise<string>) {
  const [filter, setFilter] = useState('a');
  const [rows, setRows] = useState('');
  const requests = useLatestRequest();
  const load = useCallback(async () => {
    const request = requests.begin();
    const result = await fetchRows(filter);
    if (request.isCurrent()) setRows(result);
  }, [requests, fetchRows, filter]);
  useEffect(() => { void load(); }, [load]);
  useAutoRefresh('latest-request-harness', () => { void load(); });
  return { rows, setFilter };
}

test('an older list load resolving after a newer one never writes state', async () => {
  const pending: Record<string, Deferred<string>> = { a: deferred<string>(), b: deferred<string>() };
  const fetchRows = (filter: string) => pending[filter].promise;
  const { result } = renderHook(() => useGuardedList(fetchRows));

  act(() => result.current.setFilter('b'));
  await act(async () => { pending.b.resolve('rows for b'); });
  expect(result.current.rows).toBe('rows for b');

  await act(async () => { pending.a.resolve('rows for a'); });
  expect(result.current.rows).toBe('rows for b');
});

test('an auto-refresh started before a filter change does not overwrite the newer filter', async () => {
  vi.useFakeTimers();
  try {
    const refreshA = deferred<string>();
    let callsForA = 0;
    const fetchRows = (filter: string) => {
      if (filter === 'b') return Promise.resolve('rows for b');
      callsForA += 1;
      return callsForA === 1 ? Promise.resolve('rows for a') : refreshA.promise;
    };
    const { result } = renderHook(() => useGuardedList(fetchRows));
    await act(async () => {});
    expect(result.current.rows).toBe('rows for a');

    await act(async () => { vi.advanceTimersByTime(DEFAULT_AUTO_REFRESH_SECONDS * 1000); });
    expect(callsForA).toBe(2);

    await act(async () => { result.current.setFilter('b'); });
    expect(result.current.rows).toBe('rows for b');

    await act(async () => { refreshA.resolve('refreshed rows for a'); });
    expect(result.current.rows).toBe('rows for b');
  } finally {
    vi.useRealTimers();
  }
});
