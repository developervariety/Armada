import { useRef, type KeyboardEvent } from 'react';
import { loadPromptHistory } from '../../lib/askStorage';

interface Browse { entries: string[]; index: number; draft: string; }

/**
 * Shell-style prompt history for the one-line Ask message box. ArrowUp with the caret at the start of the text
 * recalls earlier sent prompts, newest first, and keeps walking back while the recalled text is untouched.
 * ArrowDown with the caret at the end walks forward and finally restores the draft that was being typed.
 * Modifiers, a selection and IME composition leave the key alone, and typing ends the walk.
 */
export function usePromptHistory(input: string, setInput: (value: string) => void) {
  const browse = useRef<Browse | null>(null);

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
    if (event.nativeEvent.isComposing || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const field = event.currentTarget;
    const start = field.selectionStart ?? 0;
    const end = field.selectionEnd ?? 0;
    if (start !== end) return;
    let state = browse.current;
    if (state && input !== state.entries[state.index]) state = browse.current = null;

    if (event.key === 'ArrowUp') {
      if (!state && start !== 0) return;
      const entries = state ? state.entries : loadPromptHistory();
      const index = state ? state.index - 1 : entries.length - 1;
      if (index < 0) { if (state) event.preventDefault(); return; }
      event.preventDefault();
      browse.current = { entries, index, draft: state ? state.draft : input };
      setInput(entries[index]);
      return;
    }

    if (!state || end !== input.length) return;
    event.preventDefault();
    if (state.index + 1 < state.entries.length) {
      browse.current = { ...state, index: state.index + 1 };
      setInput(state.entries[state.index + 1]);
    } else {
      browse.current = null;
      setInput(state.draft);
    }
  }

  function stopBrowsing() { browse.current = null; }

  return { onKeyDown, stopBrowsing };
}
