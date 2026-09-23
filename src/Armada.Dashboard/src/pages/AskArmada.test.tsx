import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import AskArmada from './AskArmada';
import { getCaptainAskTools, listCaptains } from '../api/client';
vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({ chatWithCaptain: vi.fn(), getCaptainAskTools: vi.fn(), listCaptains: vi.fn() }));
vi.mock('../context/LocaleContext', () => ({ useLocale: () => ({ t: (text: string) => text, formatDateTime: (v: string) => v, formatRelativeTime: (v: string) => v }) }));
vi.mock('../context/WebSocketContext', () => ({ useWebSocket: () => ({ subscribe: () => () => undefined }) }));
vi.mock('../components/shared/CaptainChatPanel', () => ({ default: (props: { emptyState?: unknown }) => <div>chat panel{props.emptyState as never}</div> }));
vi.mock('../components/askGreetings', () => ({ randomGreeting: () => 'Ahoy! How can Armada help?' }));
vi.mock('../components/shared/ErrorModal', () => ({ default: () => null }));
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
