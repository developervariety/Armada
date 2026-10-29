import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { beforeEach, describe, expect, it } from 'vitest';
import { usePromptHistory } from './usePromptHistory';
import { recordPrompt } from '../../lib/askStorage';

function Composer() {
  const [input, setInput] = useState('');
  const history = usePromptHistory(input, setInput);
  return <input aria-label="Message" value={input} onChange={(event) => setInput(event.target.value)} onKeyDown={history.onKeyDown} />;
}

function press(key: string, init: Partial<{ shiftKey: boolean; selectionStart: number; selectionEnd: number }> = {}) {
  const field = screen.getByLabelText('Message') as HTMLInputElement;
  if (init.selectionStart !== undefined) field.setSelectionRange(init.selectionStart, init.selectionEnd ?? init.selectionStart);
  return fireEvent.keyDown(field, { key, shiftKey: init.shiftKey });
}

beforeEach(() => { localStorage.clear(); recordPrompt('first prompt'); recordPrompt('second prompt'); });

describe('Ask composer prompt history', () => {
  it('walks back through sent prompts from the start of the text and forward to the draft', () => {
    render(<Composer />);
    const field = screen.getByLabelText('Message') as HTMLInputElement;
    press('ArrowUp');
    expect(field.value).toBe('second prompt');
    press('ArrowUp');
    expect(field.value).toBe('first prompt');
    press('ArrowUp');
    expect(field.value).toBe('first prompt');
    field.setSelectionRange(field.value.length, field.value.length);
    press('ArrowDown');
    expect(field.value).toBe('second prompt');
    press('ArrowDown');
    expect(field.value).toBe('');
  });

  it('restores the draft that was being typed', () => {
    render(<Composer />);
    const field = screen.getByLabelText('Message') as HTMLInputElement;
    fireEvent.change(field, { target: { value: 'half-typed' } });
    press('ArrowUp', { selectionStart: 0 });
    expect(field.value).toBe('second prompt');
    field.setSelectionRange(field.value.length, field.value.length);
    press('ArrowDown');
    expect(field.value).toBe('half-typed');
  });

  it('leaves the key alone when the caret is not at the start, text is selected, or a modifier is held', () => {
    render(<Composer />);
    const field = screen.getByLabelText('Message') as HTMLInputElement;
    fireEvent.change(field, { target: { value: 'typed text' } });
    expect(press('ArrowUp', { selectionStart: 4 })).toBe(true);
    expect(press('ArrowUp', { selectionStart: 0, selectionEnd: 5 })).toBe(true);
    expect(press('ArrowUp', { selectionStart: 0, shiftKey: true })).toBe(true);
    expect(press('ArrowDown', { selectionStart: 10 })).toBe(true);
    expect(field.value).toBe('typed text');
  });

  it('ends the walk once the recalled text is edited', () => {
    render(<Composer />);
    const field = screen.getByLabelText('Message') as HTMLInputElement;
    press('ArrowUp');
    fireEvent.change(field, { target: { value: 'second prompt, edited' } });
    field.setSelectionRange(field.value.length, field.value.length);
    expect(press('ArrowDown')).toBe(true);
    expect(field.value).toBe('second prompt, edited');
    press('ArrowUp', { selectionStart: 0 });
    expect(field.value).toBe('second prompt');
  });
});
