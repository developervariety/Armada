import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import AskArmada from './AskArmada';
import { chatWithCaptain, getCaptainAskTools, listAskThreads, listCaptains } from '../api/client';
import type { Captain, CaptainChatRequest, CaptainChatResponse, CaptainToolAccessResult } from '../types/models';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  chatWithCaptain: vi.fn(), getCaptainAskTools: vi.fn(), listAskThreads: vi.fn(), listCaptains: vi.fn(),
}));
vi.mock('../context/LocaleContext', () => {
  const context = { t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v };
  return { useLocale: () => context };
});
vi.mock('../context/WebSocketContext', () => ({ useWebSocket: () => ({ subscribe: () => () => undefined }) }));
vi.mock('../components/shared/CaptainChatPanel', () => ({ default: (props: PanelProps) => (
  <div>{props.turns.map((turn) => <p key={turn.text}>{turn.text}</p>)}
    <input aria-label="Message" value={props.input} onChange={(event) => props.onInputChange(event.target.value)} />
    <button disabled={!props.canSend} onClick={props.onSend}>Send</button>
    <button onClick={props.onClear}>Clear conversation</button>
  </div>
) }));
vi.mock('../components/shared/ErrorModal', () => ({ default: () => null }));

interface PanelProps {
  turns: Array<{ text: string }>;
  input: string;
  onInputChange: (value: string) => void;
  onSend: () => void;
  onClear?: () => void;
  canSend: boolean;
}

const captain = { id: 'cpt_test', name: 'Test captain', runtime: 'ClaudeCode', model: 'test' } as Captain;
const page = { success: true, pageNumber: 1, pageSize: 10, totalPages: 1, totalRecords: 1, totalMs: 1, objects: [captain] };
const response = (reply: string): CaptainChatResponse => ({
  success: true, reply, model: 'test', error: null, thinking: null,
  metrics: { timeToFirstTokenMs: 10, streamingMs: 20, totalMs: 30, promptTokens: 4, completionTokens: 5, totalTokens: 9, tokensPerSecond: 2 },
});

describe('Ask Armada temporary chat compatibility', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(listCaptains).mockResolvedValue(page as never);
    vi.mocked(getCaptainAskTools).mockResolvedValue({ mcpConnectionPlanned: true, availabilityVerified: true, armadaToolCount: 3, summary: '' } as CaptainToolAccessResult);
    vi.mocked(chatWithCaptain).mockResolvedValueOnce(response('First answer')).mockResolvedValueOnce(response('Second answer'));
  });

  it('keeps the existing direct-chat endpoint, client history and clear action in temporary mode', async () => {
    render(<MemoryRouter><AskArmada /></MemoryRouter>);
    const input = await screen.findByLabelText('Message');
    expect(screen.getByRole('tab', { name: 'Temporary chat' })).toHaveAttribute('aria-selected', 'true');
    expect(listAskThreads).not.toHaveBeenCalled();

    fireEvent.change(input, { target: { value: 'First question' } });
    fireEvent.click(screen.getByText('Send'));
    expect(await screen.findByText('First answer')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Second question' } });
    fireEvent.click(screen.getByText('Send'));
    await screen.findByText('Second answer');

    await waitFor(() => expect(chatWithCaptain).toHaveBeenCalledTimes(2));
    const first = vi.mocked(chatWithCaptain).mock.calls[0][1] as CaptainChatRequest;
    const second = vi.mocked(chatWithCaptain).mock.calls[1][1] as CaptainChatRequest;
    expect(first.history).toEqual([]);
    expect(second.history).toEqual([
      { role: 'user', content: 'First question' },
      { role: 'assistant', content: 'First answer' },
    ]);
    fireEvent.click(screen.getByText('Clear conversation'));
    expect(screen.queryByText('First answer')).not.toBeInTheDocument();
  });
});
