import { render, screen, within } from '@testing-library/react';
import RuntimeLogEntries from './RuntimeLogEntries';
import type { FormattedLogEntry } from '../../types/models';

vi.mock('../../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      params
        ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
        : text,
  };
  return { useLocale: () => locale };
});

function entry(overrides: Partial<FormattedLogEntry>): FormattedLogEntry {
  return {
    kind: 'Text',
    text: '',
    isToolCall: false,
    toolName: null,
    redacted: false,
    truncated: false,
    dropped: false,
    ...overrides,
  };
}

describe('RuntimeLogEntries', () => {
  it('labels thinking, tool and status entries so they stay distinct from answer text', () => {
    render(
      <RuntimeLogEntries
        entriesTruncated={false}
        entries={[
          entry({ kind: 'Text', text: 'Final answer text' }),
          entry({ kind: 'Thinking', text: 'Considering the diff' }),
          entry({ kind: 'ToolCall', text: 'ls -la', isToolCall: true, toolName: 'Bash' }),
          entry({ kind: 'ToolResult', text: 'total 0', isToolCall: true, toolName: 'Bash' }),
          entry({ kind: 'Status', text: 'session started' }),
        ]}
      />,
    );

    const answer = screen.getByText('Final answer text').closest('[data-log-kind]') as HTMLElement;
    expect(answer).toHaveAttribute('data-log-kind', 'Text');
    expect(within(answer).queryByText(/thinking|tool|status/i)).not.toBeInTheDocument();

    const thinking = screen.getByText('Considering the diff').closest('[data-log-kind]') as HTMLElement;
    expect(within(thinking).getByText('thinking')).toBeInTheDocument();

    const call = screen.getByText('ls -la').closest('[data-log-kind]') as HTMLElement;
    expect(within(call).getByText('tool call: Bash')).toBeInTheDocument();

    const result = screen.getByText('total 0').closest('[data-log-kind]') as HTMLElement;
    expect(within(result).getByText('tool result: Bash')).toBeInTheDocument();

    const status = screen.getByText('session started').closest('[data-log-kind]') as HTMLElement;
    expect(within(status).getByText('status')).toBeInTheDocument();
  });

  it('marks redacted and truncated entries and hides dropped noise', () => {
    render(
      <RuntimeLogEntries
        entriesTruncated={false}
        entries={[
          entry({ text: 'token was here', redacted: true }),
          entry({ text: 'long output', truncated: true }),
          entry({ text: 'noise line', dropped: true }),
        ]}
      />,
    );

    const redacted = screen.getByText('token was here').closest('[data-log-kind]') as HTMLElement;
    expect(within(redacted).getByText('redacted')).toBeInTheDocument();
    const truncated = screen.getByText('long output').closest('[data-log-kind]') as HTMLElement;
    expect(within(truncated).getByText('truncated')).toBeInTheDocument();
    expect(screen.queryByText('noise line')).not.toBeInTheDocument();
  });

  it('says when the server omitted entries at its page limit', () => {
    render(<RuntimeLogEntries entriesTruncated entries={[entry({ text: 'kept' })]} />);

    expect(screen.getByText('Some entries were omitted at the page limit.')).toBeInTheDocument();
  });

  it('shows an empty state when no displayable entry remains', () => {
    render(<RuntimeLogEntries entriesTruncated={false} entries={[entry({ text: 'noise', dropped: true })]} />);

    expect(screen.getByText('(empty log)')).toBeInTheDocument();
  });
});
