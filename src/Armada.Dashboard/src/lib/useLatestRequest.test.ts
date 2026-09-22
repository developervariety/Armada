import { renderHook } from '@testing-library/react';
import { expect, test } from 'vitest';
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
