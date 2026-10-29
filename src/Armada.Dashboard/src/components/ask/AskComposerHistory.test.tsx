import { fireEvent, render, screen } from '@testing-library/react';
import AskComposer, { PROMPT_HISTORY_KEY, PROMPT_HISTORY_LIMIT } from './AskComposer';
import { translateTemplate } from '../../i18n/runtime';

const localeValue = {
  t: (text: string, params?: Record<string, string | number | null | undefined>) => translateTemplate('en', text, null, params),
  locale: 'en',
  formatDateTime: (v: string | null | undefined) => v ?? '',
  formatRelativeTime: (v: string | null | undefined) => (v ? 'recently' : ''),
};
vi.mock('../../context/LocaleContext', () => ({ useLocale: () => localeValue }));

function renderComposer() {
  const onSend = vi.fn();
  const onTextChange = vi.fn();
  render(
    <AskComposer
      quickActions={[]}
      turnActive={false}
      stopping={false}
      onStop={() => undefined}
      onSend={onSend}
      onQuickAction={async () => true}
      onLocalCommand={async () => ({ ok: true })}
      actionBusy={false}
      onOpenImport={() => undefined}
      noCaptain={false}
      showThinking={false}
      onShowThinkingChange={() => undefined}
      onTextChange={onTextChange}
    />,
  );
  const box = screen.getByRole('combobox', { name: 'Message' }) as HTMLTextAreaElement;
  return { box, onSend, onTextChange };
}

function type(box: HTMLTextAreaElement, text: string) {
  fireEvent.change(box, { target: { value: text } });
}

function send(box: HTMLTextAreaElement, text: string) {
  type(box, text);
  fireEvent.keyDown(box, { key: 'Enter' });
}

function press(box: HTMLTextAreaElement, key: 'ArrowUp' | 'ArrowDown', caret?: number, init: Record<string, boolean> = {}) {
  const at = caret ?? box.value.length;
  box.setSelectionRange(at, at);
  return fireEvent.keyDown(box, { key, ...init });
}

function stored(): string[] {
  return JSON.parse(localStorage.getItem(PROMPT_HISTORY_KEY) ?? '[]') as string[];
}

describe('AskComposer prompt history', () => {
  beforeEach(() => localStorage.clear());

  it('ArrowUp recalls the last sent prompt with the caret at the end', () => {
    const { box, onSend, onTextChange } = renderComposer();
    send(box, 'first question');
    expect(onSend).toHaveBeenCalledWith('first question');
    expect(box.value).toBe('');

    const notCancelled = press(box, 'ArrowUp');
    expect(notCancelled).toBe(false);
    expect(box.value).toBe('first question');
    expect(box.selectionStart).toBe('first question'.length);
    expect(box.selectionEnd).toBe('first question'.length);
    expect(onTextChange).toHaveBeenLastCalledWith('first question');
  });

  it('repeated ArrowUp walks back and stops at the oldest entry', () => {
    const { box } = renderComposer();
    send(box, 'one');
    send(box, 'two');
    send(box, 'three');

    press(box, 'ArrowUp');
    expect(box.value).toBe('three');
    press(box, 'ArrowUp', 0);
    expect(box.value).toBe('two');
    press(box, 'ArrowUp');
    expect(box.value).toBe('one');
    const notCancelled = press(box, 'ArrowUp');
    expect(notCancelled).toBe(true);
    expect(box.value).toBe('one');
  });

  it('ArrowDown walks forward and restores the draft being typed', () => {
    const { box, onTextChange } = renderComposer();
    send(box, 'one');
    send(box, 'two');
    type(box, 'half typed');

    press(box, 'ArrowUp');
    press(box, 'ArrowUp');
    expect(box.value).toBe('one');
    press(box, 'ArrowDown');
    expect(box.value).toBe('two');
    press(box, 'ArrowDown');
    expect(box.value).toBe('half typed');
    expect(onTextChange).toHaveBeenLastCalledWith('half typed');

    // Past the draft, ArrowDown is no longer browsing and leaves the text alone.
    const notCancelled = press(box, 'ArrowDown');
    expect(notCancelled).toBe(true);
    expect(box.value).toBe('half typed');
  });

  it('typing ends browsing so ArrowDown no longer moves through history', () => {
    const { box } = renderComposer();
    send(box, 'one');
    send(box, 'two');
    press(box, 'ArrowUp');
    press(box, 'ArrowUp');
    type(box, 'one edited');
    expect(press(box, 'ArrowDown')).toBe(true);
    expect(box.value).toBe('one edited');
    // Browsing starts over from the newest entry and the edit becomes the draft.
    press(box, 'ArrowUp');
    expect(box.value).toBe('two');
    press(box, 'ArrowDown');
    expect(box.value).toBe('one edited');
  });

  it('ArrowUp with the slash menu open moves the menu selection, not the history', () => {
    const { box } = renderComposer();
    send(box, 'earlier prompt');
    type(box, '/');
    const before = box.getAttribute('aria-activedescendant');
    expect(before).toBeTruthy();

    const notCancelled = press(box, 'ArrowUp');
    expect(notCancelled).toBe(false);
    expect(box.value).toBe('/');
    const after = box.getAttribute('aria-activedescendant');
    expect(after).toBeTruthy();
    expect(after).not.toBe(before);
  });

  it('ArrowUp with the caret on line 2 of multi-line text does not replace the text', () => {
    const { box } = renderComposer();
    send(box, 'earlier prompt');
    type(box, 'line one\nline two');

    const notCancelled = press(box, 'ArrowUp', 'line one\nli'.length);
    expect(notCancelled).toBe(true);
    expect(box.value).toBe('line one\nline two');

    // On the first line it recalls history.
    press(box, 'ArrowUp', 3);
    expect(box.value).toBe('earlier prompt');
  });

  it('ArrowDown on a line above the last does not leave the recalled entry', () => {
    const { box } = renderComposer();
    send(box, 'a\nb');
    press(box, 'ArrowUp', 0);
    expect(box.value).toBe('a\nb');
    expect(press(box, 'ArrowDown', 0)).toBe(true);
    expect(box.value).toBe('a\nb');
    press(box, 'ArrowDown');
    expect(box.value).toBe('');
  });

  it('ignores ArrowUp with a modifier held or a selected range', () => {
    const { box } = renderComposer();
    send(box, 'earlier prompt');
    for (const mod of ['altKey', 'ctrlKey', 'metaKey', 'shiftKey']) {
      expect(press(box, 'ArrowUp', 0, { [mod]: true })).toBe(true);
      expect(box.value).toBe('');
    }
    type(box, 'draft');
    box.setSelectionRange(0, 3);
    expect(fireEvent.keyDown(box, { key: 'ArrowUp' })).toBe(true);
    expect(box.value).toBe('draft');
  });

  it('ignores ArrowUp during IME composition', () => {
    const { box } = renderComposer();
    send(box, 'earlier prompt');
    expect(press(box, 'ArrowUp', 0, { isComposing: true })).toBe(true);
    expect(box.value).toBe('');
  });

  it('does not record empty or duplicate sends', () => {
    const { box, onSend } = renderComposer();
    send(box, '   ');
    expect(onSend).not.toHaveBeenCalled();
    expect(stored()).toEqual([]);
    send(box, 'same');
    send(box, 'same');
    expect(onSend).toHaveBeenCalledTimes(2);
    expect(stored()).toEqual(['same']);
    send(box, 'other');
    send(box, 'same');
    expect(stored()).toEqual(['same', 'other', 'same']);
  });

  it('records typed slash commands', async () => {
    const { box } = renderComposer();
    send(box, '/new');
    expect(stored()).toEqual(['/new']);
    await screen.findByRole('combobox', { name: 'Message' });
  });

  it('caps the history and survives a remount', () => {
    localStorage.setItem(PROMPT_HISTORY_KEY, JSON.stringify(Array.from({ length: PROMPT_HISTORY_LIMIT }, (_, i) => `p${i}`)));
    const { box } = renderComposer();
    send(box, 'newest');
    const history = stored();
    expect(history).toHaveLength(PROMPT_HISTORY_LIMIT);
    expect(history[0]).toBe('p1');
    expect(history[history.length - 1]).toBe('newest');
  });

  it('tolerates a corrupt stored history', () => {
    localStorage.setItem(PROMPT_HISTORY_KEY, '{not json');
    const { box } = renderComposer();
    expect(press(box, 'ArrowUp')).toBe(true);
    send(box, 'fresh');
    expect(stored()).toEqual(['fresh']);
  });
});
