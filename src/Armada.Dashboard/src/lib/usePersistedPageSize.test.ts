import { beforeEach, describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { usePersistedPageSize } from './usePersistedPageSize';
import { useTablePrefs } from './useTablePrefs';

describe('usePersistedPageSize', () => {
  beforeEach(() => localStorage.clear());

  it('keeps the chosen size for the table across visits', () => {
    const first = renderHook(() => usePersistedPageSize('missions', 25));
    expect(first.result.current[0]).toBe(25);
    act(() => first.result.current[1](100));
    first.unmount();

    const again = renderHook(() => usePersistedPageSize('missions', 25));
    expect(again.result.current[0]).toBe(100);
  });

  it('starts an unsized table with the size last chosen anywhere, and keeps per-table choices', () => {
    const missions = renderHook(() => usePersistedPageSize('missions', 25));
    act(() => missions.result.current[1](50));
    const voyages = renderHook(() => usePersistedPageSize('voyages', 25));
    expect(voyages.result.current[0]).toBe(50);
    act(() => voyages.result.current[1](10));
    missions.unmount();
    const missionsAgain = renderHook(() => usePersistedPageSize('missions', 25));
    expect(missionsAgain.result.current[0]).toBe(50);
  });

  it('ignores sizes the selector does not offer', () => {
    localStorage.setItem('armada_pagesize_events', '9999');
    const events = renderHook(() => usePersistedPageSize('events', 25));
    expect(events.result.current[0]).toBe(25);
  });

  it('shares the last-chosen size with server-paged tables', () => {
    const missions = renderHook(() => usePersistedPageSize('missions', 25));
    act(() => missions.result.current[1](250));
    const health = renderHook(() => useTablePrefs('vessel-health'));
    expect(health.result.current.pageSize).toBe(250);
  });
});
