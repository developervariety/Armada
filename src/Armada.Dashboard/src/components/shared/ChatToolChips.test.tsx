import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import ChatToolChips, { type ToolEvent } from './ChatToolChips';

const labels = {
  runningLabel: 'running…',
  argumentsLabel: 'Arguments',
  resultLabel: 'Result',
  noDetailsLabel: 'No details available.',
};

function renderTools(tools: ToolEvent[], runtimeLabel?: string) {
  return render(<ChatToolChips tools={tools} runtimeLabel={runtimeLabel} {...labels} />);
}

describe('ChatToolChips', () => {
  it('shows a one-line result preview on a completed tool card', () => {
    const { container } = renderTools([
      { id: 'a', name: 'list_vessels', status: 'success', result: '{\n  "count": 2,\n  "items": ["a"]\n}', elapsedMs: 120 },
    ]);

    expect(container.querySelector('.chat-tool-result-preview')?.textContent).toBe('{"count":2,"items":["a"]}');
  });

  it('truncates a long preview so the summary row does not wrap', () => {
    const { container } = renderTools([{ id: 'a', name: 'read_log', status: 'success', result: 'x'.repeat(120) }]);

    const preview = container.querySelector('.chat-tool-result-preview')?.textContent ?? '';
    expect(preview).toBe('x'.repeat(80) + '…');
  });

  it('shows no preview while the tool is still running', () => {
    const { container } = renderTools([{ id: 'a', name: 'list_vessels', status: 'running' }]);

    expect(container.querySelector('.chat-tool-result-preview')).toBeNull();
  });

  it('labels each card with the runtime that ran it, only when the runtime is known', () => {
    const tool: ToolEvent = { id: 'a', name: 'list_vessels', status: 'success', result: '{}' };

    expect(renderTools([tool], 'Codex').container.querySelector('.chat-tool-runtime')?.textContent).toBe('Codex');
    expect(renderTools([tool]).container.querySelector('.chat-tool-runtime')).toBeNull();
  });
});
