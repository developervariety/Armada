import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import AskArmada from './AskArmada';
import { chatWithCaptain, getCaptainAskTools, listCaptains } from '../api/client';
import type { WebSocketMessage } from '../types/models';
const handlers = vi.hoisted(() => new Set<(message: WebSocketMessage) => void>());
vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({ chatWithCaptain: vi.fn(), getCaptainAskTools: vi.fn(), listCaptains: vi.fn() }));
vi.mock('../context/LocaleContext', () => {
  const locale = { t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v };
  return { useLocale: () => locale };
});
vi.mock('../context/WebSocketContext', () => ({ useWebSocket: () => ({ subscribe: (handler: (message: WebSocketMessage) => void) => { handlers.add(handler); return () => handlers.delete(handler); } }) }));
vi.mock('../components/askGreetings', () => ({ randomGreeting: () => 'Ahoy! How can Armada help?' }));
vi.mock('../components/shared/ConfirmDialog', () => ({ default: () => null }));
const captain = { id: 'cpt_test', name: 'Test captain', runtime: 'ClaudeCode', model: 'test' };
const page = { success: true, pageNumber: 1, pageSize: 10, totalPages: 1, totalRecords: 1, totalMs: 1, objects: [captain] };
function renderPage() { return render(<MemoryRouter><AskArmada /></MemoryRouter>); }
describe('Ask MCP preflight states', () => {
  beforeEach(() => { vi.mocked(listCaptains).mockResolvedValue(page as never); });
  it('greets the operator on the blank chat and names the selected captain', async () => {
    vi.mocked(getCaptainAskTools).mockResolvedValue({ mcpConnectionPlanned: true, availabilityVerified: true, armadaToolCount: 3, summary: '' } as never);
    renderPage();
    expect(await screen.findByText('Ahoy! How can Armada help?')).toHaveClass('ask-empty-greeting');
    expect(await screen.findByText('Chatting with {{name}}')).toHaveClass('ask-empty-sub');
  });
  it('shows planned preflight failure summary', async () => {
    vi.mocked(getCaptainAskTools).mockResolvedValue({ mcpConnectionPlanned: true, availabilityVerified: true, armadaToolCount: 0, summary: 'Ask launch plan targets Armada MCP, but endpoint preflight failed.' } as never);
    renderPage();
    expect(await screen.findByText('Ask MCP preflight found no Armada tools.')).toBeInTheDocument();
    expect(screen.getByText('Ask launch plan targets Armada MCP, but endpoint preflight failed.')).toBeInTheDocument();
    expect(screen.queryByText('How to connect')).not.toBeInTheDocument();
  });
  it('does not show disconnected warning for planned reachable tools', async () => {
    vi.mocked(getCaptainAskTools).mockResolvedValue({ mcpConnectionPlanned: true, availabilityVerified: true, armadaToolCount: 3, summary: 'Ask launch plan reached Armada tools.' } as never);
    renderPage();
    await waitFor(() => expect(screen.queryByText('This captain is not connected to Armada over MCP.')).not.toBeInTheDocument());
  });
  it('shows unverified state for unsupported runtime', async () => {
    vi.mocked(getCaptainAskTools).mockResolvedValue({ mcpConnectionPlanned: false, availabilityVerified: false, armadaToolCount: 0, summary: 'Custom captains have no supported Ask MCP launch contract.' } as never);
    renderPage();
    expect(await screen.findByText('Custom captains have no supported Ask MCP launch contract.')).toBeInTheDocument();
  });
});

it('keeps the question and partial reply after a dropped chat response and accepts the next question without reloading', async () => {
  vi.mocked(listCaptains).mockResolvedValue(page as never);
  vi.mocked(getCaptainAskTools).mockResolvedValue({ armadaToolCount: 3 } as never);
  let rejectTurn!: (error: Error) => void;
  vi.mocked(chatWithCaptain).mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectTurn = reject; }));
  renderPage();
  const input = await screen.findByPlaceholderText('Message the captain...');
  fireEvent.change(input, { target: { value: 'Check the fleet' } });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
  await waitFor(() => expect(chatWithCaptain).toHaveBeenCalled());
  const request = vi.mocked(chatWithCaptain).mock.calls[vi.mocked(chatWithCaptain).mock.calls.length - 1][1];
  act(() => {
    handlers.forEach((handler) => handler({ type: 'ask.chunk', data: { turnId: request.turnId, delta: 'I found the fleet' } } as WebSocketMessage));
  });
  expect(screen.getByText('I found the fleet')).toBeInTheDocument();
  await act(async () => { rejectTurn(Object.assign(new Error('Connection to Armada failed'), { name: 'NetworkError' })); });
  expect(screen.getByText('Check the fleet')).toBeInTheDocument();
  expect(screen.getByText('I found the fleet')).toBeInTheDocument();
  expect(screen.getByText(/The connection ended before the reply was confirmed/)).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
  expect(input).toBeEnabled();
  vi.mocked(chatWithCaptain).mockResolvedValueOnce({ success: true, reply: 'Ready again' } as never);
  fireEvent.change(input, { target: { value: 'Are you ready?' } });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
  expect(await screen.findByText('Ready again')).toBeInTheDocument();
  expect(vi.mocked(chatWithCaptain).mock.calls[vi.mocked(chatWithCaptain).mock.calls.length - 1][1].history).toEqual([
    { role: 'user', content: 'Check the fleet' },
    { role: 'assistant', content: 'I found the fleet' },
  ]);
});
