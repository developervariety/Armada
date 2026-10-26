import { fireEvent, render, screen } from '@testing-library/react';
import { useRef } from 'react';
import {
  ASK_LIST_MAX_WIDTH,
  ASK_LIST_MIN_WIDTH,
  ASK_LIST_WIDTH_STORAGE_KEY,
  clampListWidth,
  useAskListResize,
} from './askListResize';

// jsdom has no layout, so each test sets the rendered sizes of the container and the list.
let containerWidth = 1200;
let listWidth = 280;

function rect(width: number): DOMRect {
  return { width, height: 600, top: 0, left: 0, right: width, bottom: 600, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
}

function Harness() {
  const homeRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const resize = useAskListResize(homeRef, listRef);
  return (
    <div ref={homeRef} data-testid="home" data-dragging={resize.dragging ? 'yes' : 'no'}>
      <div ref={listRef} data-testid="list" />
      <div role="separator" tabIndex={0} data-testid="splitter" {...resize.splitterProps} />
      <output data-testid="width">{resize.width ?? 'default'}</output>
    </div>
  );
}

function renderHarness() {
  const result = render(<Harness />);
  screen.getByTestId('home').getBoundingClientRect = () => rect(containerWidth);
  // The list follows the hook's width, as the stylesheet does, unless no width is set.
  screen.getByTestId('list').getBoundingClientRect = () => {
    const shown = screen.getByTestId('width').textContent;
    return rect(shown === 'default' ? listWidth : Number(shown));
  };
  return result;
}

function shownWidth(): string {
  return screen.getByTestId('width').textContent ?? '';
}

beforeEach(() => {
  localStorage.clear();
  containerWidth = 1200;
  listWidth = 280;
});

describe('clampListWidth', () => {
  it('keeps the width between the minimum and maximum', () => {
    expect(clampListWidth(50)).toBe(ASK_LIST_MIN_WIDTH);
    expect(clampListWidth(5000)).toBe(ASK_LIST_MAX_WIDTH);
    expect(clampListWidth(321.6)).toBe(322);
  });

  it('leaves room for the conversation in a narrow container, but never goes below the minimum', () => {
    expect(clampListWidth(600, 800)).toBe(440);
    expect(clampListWidth(600, 300)).toBe(ASK_LIST_MIN_WIDTH);
  });
});

describe('useAskListResize', () => {
  it('starts at the stylesheet default when nothing is stored', () => {
    renderHarness();
    expect(shownWidth()).toBe('default');
  });

  it('restores a stored width, clamped, and ignores a stored value that is not a number', () => {
    localStorage.setItem(ASK_LIST_WIDTH_STORAGE_KEY, '9999');
    const { unmount } = renderHarness();
    expect(shownWidth()).toBe(String(ASK_LIST_MAX_WIDTH));
    unmount();
    localStorage.setItem(ASK_LIST_WIDTH_STORAGE_KEY, 'wide');
    renderHarness();
    expect(shownWidth()).toBe('default');
  });

  it('resizes while dragging and stores the width only when the drag ends', () => {
    renderHarness();
    const splitter = screen.getByTestId('splitter');
    fireEvent.pointerDown(splitter, { button: 0, clientX: 300, pointerId: 1 });
    expect(screen.getByTestId('home').dataset.dragging).toBe('yes');
    fireEvent.pointerMove(splitter, { clientX: 400, pointerId: 1 });
    expect(shownWidth()).toBe('380');
    expect(localStorage.getItem(ASK_LIST_WIDTH_STORAGE_KEY)).toBeNull();
    fireEvent.pointerMove(splitter, { clientX: 350, pointerId: 1 });
    fireEvent.pointerUp(splitter, { clientX: 350, pointerId: 1 });
    expect(shownWidth()).toBe('330');
    expect(localStorage.getItem(ASK_LIST_WIDTH_STORAGE_KEY)).toBe('330');
    expect(screen.getByTestId('home').dataset.dragging).toBe('no');
    // Moves after the drag ends do nothing.
    fireEvent.pointerMove(splitter, { clientX: 600, pointerId: 1 });
    expect(shownWidth()).toBe('330');
  });

  it('clamps a drag past either edge and keeps room for the conversation', () => {
    containerWidth = 900;
    renderHarness();
    const splitter = screen.getByTestId('splitter');
    fireEvent.pointerDown(splitter, { button: 0, clientX: 300, pointerId: 1 });
    fireEvent.pointerMove(splitter, { clientX: -500, pointerId: 1 });
    expect(shownWidth()).toBe(String(ASK_LIST_MIN_WIDTH));
    fireEvent.pointerMove(splitter, { clientX: 2000, pointerId: 1 });
    expect(shownWidth()).toBe('540');
    fireEvent.pointerCancel(splitter, { pointerId: 1 });
    expect(localStorage.getItem(ASK_LIST_WIDTH_STORAGE_KEY)).toBe('540');
  });

  it('ignores secondary buttons and moves from another pointer', () => {
    renderHarness();
    const splitter = screen.getByTestId('splitter');
    fireEvent.pointerDown(splitter, { button: 2, clientX: 300, pointerId: 1 });
    fireEvent.pointerMove(splitter, { clientX: 400, pointerId: 1 });
    expect(shownWidth()).toBe('default');
    fireEvent.pointerDown(splitter, { button: 0, clientX: 300, pointerId: 1 });
    fireEvent.pointerMove(splitter, { clientX: 400, pointerId: 2 });
    expect(shownWidth()).toBe('default');
  });

  it('resizes from the keyboard and restores the default with Enter or a double-click', () => {
    renderHarness();
    const splitter = screen.getByTestId('splitter');
    fireEvent.keyDown(splitter, { key: 'ArrowRight' });
    expect(shownWidth()).toBe('296');
    fireEvent.keyDown(splitter, { key: 'ArrowLeft', shiftKey: true });
    expect(shownWidth()).toBe('232');
    expect(localStorage.getItem(ASK_LIST_WIDTH_STORAGE_KEY)).toBe('232');
    fireEvent.keyDown(splitter, { key: 'End' });
    expect(shownWidth()).toBe(String(ASK_LIST_MAX_WIDTH));
    fireEvent.keyDown(splitter, { key: 'Home' });
    expect(shownWidth()).toBe(String(ASK_LIST_MIN_WIDTH));
    fireEvent.keyDown(splitter, { key: 'Enter' });
    expect(shownWidth()).toBe('default');
    expect(localStorage.getItem(ASK_LIST_WIDTH_STORAGE_KEY)).toBeNull();
    fireEvent.keyDown(splitter, { key: 'ArrowRight' });
    fireEvent.doubleClick(splitter);
    expect(shownWidth()).toBe('default');
    expect(localStorage.getItem(ASK_LIST_WIDTH_STORAGE_KEY)).toBeNull();
  });

  it('leaves other keys alone', () => {
    renderHarness();
    const splitter = screen.getByTestId('splitter');
    const notHandled = fireEvent.keyDown(splitter, { key: 'a' });
    expect(notHandled).toBe(true);
    expect(shownWidth()).toBe('default');
  });
});
