import { useRef, useState, type KeyboardEvent, type PointerEvent, type RefObject } from 'react';

/** Stored width in pixels of the Ask Armada conversation list, set by dragging the splitter beside it. */
export const ASK_LIST_WIDTH_STORAGE_KEY = 'armada_ask_list_width';
export const ASK_LIST_MIN_WIDTH = 200;
export const ASK_LIST_MAX_WIDTH = 640;
/** Room always left for the conversation pane beside the list. */
export const ASK_CONV_MIN_WIDTH = 360;
const KEY_STEP = 16;
const KEY_STEP_LARGE = 64;

function readStoredWidth(): number | null {
  try {
    const value = Number(localStorage.getItem(ASK_LIST_WIDTH_STORAGE_KEY));
    return Number.isFinite(value) && value > 0 ? value : null;
  } catch { return null; }
}

function writeStoredWidth(width: number | null) {
  try {
    if (width === null) localStorage.removeItem(ASK_LIST_WIDTH_STORAGE_KEY);
    else localStorage.setItem(ASK_LIST_WIDTH_STORAGE_KEY, String(Math.round(width)));
  } catch { /* storage unavailable */ }
}

/** Clamps a list width to the allowed range, keeping room for the conversation when the container width is known. */
export function clampListWidth(width: number, containerWidth?: number): number {
  let max = ASK_LIST_MAX_WIDTH;
  if (containerWidth && containerWidth > 0) max = Math.min(max, containerWidth - ASK_CONV_MIN_WIDTH);
  return Math.round(Math.max(ASK_LIST_MIN_WIDTH, Math.min(max, width)));
}

/**
 * Width of the conversation list and the handlers for the splitter between the list and the conversation. Drag
 * with a pointer, or focus the splitter and use the arrow keys, Home, and End; double-click or Enter restores the
 * default width. A null width means the stylesheet default.
 */
export function useAskListResize(containerRef: RefObject<HTMLElement | null>, listRef: RefObject<HTMLElement | null>) {
  const [width, setWidth] = useState<number | null>(() => {
    const stored = readStoredWidth();
    return stored === null ? null : clampListWidth(stored);
  });
  const [dragging, setDragging] = useState(false);
  const dragRef = useRef<{ startX: number; startWidth: number; pointerId: number } | null>(null);
  const widthRef = useRef(width);
  widthRef.current = width;

  const containerWidth = () => containerRef.current?.getBoundingClientRect().width;
  // The rendered width, since the stylesheet narrows a stored width that no longer fits the window.
  const currentWidth = () => listRef.current?.getBoundingClientRect().width || widthRef.current || ASK_LIST_MIN_WIDTH;

  const apply = (next: number | null, persist: boolean) => {
    const value = next === null ? null : clampListWidth(next, containerWidth());
    widthRef.current = value;
    setWidth(value);
    if (persist) writeStoredWidth(value);
  };

  const onPointerDown = (e: PointerEvent<HTMLElement>) => {
    if (e.button !== 0) return;
    e.preventDefault();
    dragRef.current = { startX: e.clientX, startWidth: currentWidth(), pointerId: e.pointerId };
    try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* not supported */ }
    setDragging(true);
  };

  const onPointerMove = (e: PointerEvent<HTMLElement>) => {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== e.pointerId) return;
    apply(drag.startWidth + (e.clientX - drag.startX), false);
  };

  const endDrag = (e: PointerEvent<HTMLElement>) => {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== e.pointerId) return;
    dragRef.current = null;
    try { e.currentTarget.releasePointerCapture(e.pointerId); } catch { /* not supported */ }
    setDragging(false);
    writeStoredWidth(widthRef.current);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLElement>) => {
    const step = e.shiftKey ? KEY_STEP_LARGE : KEY_STEP;
    if (e.key === 'ArrowLeft') apply(currentWidth() - step, true);
    else if (e.key === 'ArrowRight') apply(currentWidth() + step, true);
    else if (e.key === 'Home') apply(ASK_LIST_MIN_WIDTH, true);
    else if (e.key === 'End') apply(ASK_LIST_MAX_WIDTH, true);
    else if (e.key === 'Enter') apply(null, true);
    else return;
    e.preventDefault();
  };

  const reset = () => apply(null, true);

  return {
    width,
    dragging,
    splitterProps: {
      onPointerDown,
      onPointerMove,
      onPointerUp: endDrag,
      onPointerCancel: endDrag,
      onKeyDown,
      onDoubleClick: reset,
    },
  };
}
