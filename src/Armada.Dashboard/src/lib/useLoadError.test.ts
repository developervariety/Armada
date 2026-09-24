import { describe, it, expect } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useLoadError } from './useLoadError';

describe('useLoadError', () => {
  it('opens once for a run of failed loads and again after a success', () => {
    const { result } = renderHook(() => useLoadError());
    act(() => result.current.loadFailed('down'));
    expect(result.current.error).toBe('down');

    act(() => result.current.setError(''));
    act(() => result.current.loadFailed('down'));
    expect(result.current.error).toBe('');

    act(() => result.current.loadSucceeded());
    expect(result.current.error).toBe('');
    act(() => result.current.loadFailed('down again'));
    expect(result.current.error).toBe('down again');
  });
});
