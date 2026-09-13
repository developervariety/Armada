import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import ChatMetricsInfo from './ChatMetricsInfo';
import type { ToolEvent } from './ChatToolChips';
import type { CaptainChatMetrics } from '../../types/models';

vi.mock('../../context/LocaleContext', () => ({ useLocale: () => ({ t: (s: string) => s }) }));

const metrics: CaptainChatMetrics = {
  timeToFirstTokenMs: 400,
  streamingMs: 1600,
  totalMs: 2000,
  promptTokens: 10,
  completionTokens: 40,
  totalTokens: 50,
  tokensPerSecond: 25,
};

function rowValue(label: string): string | null {
  const row = Array.from(document.querySelectorAll('.chat-metrics-row'))
    .find((item) => item.querySelector('.chat-metrics-row-label')?.textContent === label);
  return row?.querySelector('.chat-metrics-row-value')?.textContent ?? null;
}

describe('ChatMetricsInfo', () => {
  it('reports the tool call count and the time summed across completed calls', () => {
    const tools: ToolEvent[] = [
      { id: 'a', name: 'list_vessels', status: 'success', elapsedMs: 1200 },
      { id: 'b', name: 'read_log', status: 'failed', elapsedMs: 300 },
      { id: 'c', name: 'list_missions', status: 'running' },
    ];
    render(<ChatMetricsInfo metrics={metrics} tools={tools} />);

    fireEvent.click(screen.getByRole('button', { name: 'Turn statistics' }));

    expect(rowValue('tool calls')).toBe('2');
    expect(rowValue('tool time')).toBe('1.50s');
  });

  it('adds no tool rows for a turn that called no tools', () => {
    render(<ChatMetricsInfo metrics={metrics} tools={[]} />);

    fireEvent.click(screen.getByRole('button', { name: 'Turn statistics' }));

    expect(rowValue('total')).toBe('2.00s');
    expect(rowValue('tool calls')).toBeNull();
    expect(rowValue('tool time')).toBeNull();
  });
});
