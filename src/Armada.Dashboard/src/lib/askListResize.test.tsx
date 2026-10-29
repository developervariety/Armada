import { act, renderHook } from '@testing-library/react';
import type { KeyboardEvent } from 'react';
import { beforeEach, describe, expect, it } from 'vitest';
import { ASK_LIST_MAX_WIDTH, ASK_LIST_MIN_WIDTH, ASK_LIST_WIDTH_STORAGE_KEY, clampListWidth, useAskListResize } from './askListResize';

const container = { current: { getBoundingClientRect: () => ({ width: 1200 }) } as HTMLElement };
const list = { current: { getBoundingClientRect: () => ({ width: 300 }) } as HTMLElement };

function key(name: string, shiftKey = false) {
  return { key: name, shiftKey, preventDefault: () => undefined } as unknown as KeyboardEvent<HTMLElement>;
}

beforeEach(() => localStorage.clear());

describe('Ask conversation list width', () => {
  it('bounds a width and keeps room for the conversation', () => {
    expect(clampListWidth(50)).toBe(ASK_LIST_MIN_WIDTH);
    expect(clampListWidth(9999)).toBe(ASK_LIST_MAX_WIDTH);
    expect(clampListWidth(700, 800)).toBe(440);
  });

  it('starts from the stylesheet default when the stored value is not a width', () => {
    localStorage.setItem(ASK_LIST_WIDTH_STORAGE_KEY, 'wide');
    expect(renderHook(() => useAskListResize(container, list)).result.current.width).toBeNull();
    localStorage.setItem(ASK_LIST_WIDTH_STORAGE_KEY, '9999');
    expect(renderHook(() => useAskListResize(container, list)).result.current.width).toBe(ASK_LIST_MAX_WIDTH);
  });

  it('resizes from the keyboard, saves the width, and restores the default on Enter', () => {
    const { result } = renderHook(() => useAskListResize(container, list));
    act(() => result.current.splitterProps.onKeyDown(key('ArrowRight')));
    expect(result.current.width).toBe(316);
    expect(localStorage.getItem(ASK_LIST_WIDTH_STORAGE_KEY)).toBe('316');
    act(() => result.current.splitterProps.onKeyDown(key('ArrowLeft', true)));
    expect(result.current.width).toBe(236);
    act(() => result.current.splitterProps.onKeyDown(key('End')));
    expect(result.current.width).toBe(ASK_LIST_MAX_WIDTH);
    act(() => result.current.splitterProps.onKeyDown(key('Enter')));
    expect(result.current.width).toBeNull();
    expect(localStorage.getItem(ASK_LIST_WIDTH_STORAGE_KEY)).toBeNull();
  });
});
