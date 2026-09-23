import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import History from './History';
import { enumerateHistoryTimeline, listObjectives, listVessels } from '../api/client';
import type { HistoricalTimelineQuery } from '../types/models';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  enumerateHistoryTimeline: vi.fn(),
  listObjectives: vi.fn(),
  listVessels: vi.fn(),
  deleteRequestHistoryEntry: vi.fn(),
}));

// One stable locale object, like the real provider.
vi.mock('../context/LocaleContext', () => {
  const translate = (text: string, params?: Record<string, string | number | null | undefined>) => {
    if (!params) return text;
    return Object.entries(params).reduce(
      (current, [key, value]) => current.split(`{{${key}}}`).join(value == null ? '' : String(value)),
      text,
    );
  };
  const locale = {
    t: translate,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

const SERVER_TOTAL = 734;

function entry(id: string, title: string) {
  return {
    id,
    sourceType: 'CheckRun',
    sourceId: 'chk_123',
    entityType: 'CheckRun',
    entityId: 'chk_123',
    objectiveId: null,
    vesselId: 'vsl_123',
    environmentId: null,
    deploymentId: null,
    incidentId: null,
    missionId: 'msn_123',
    voyageId: 'voy_123',
    actorId: 'usr_123',
    actorDisplay: 'captain@armada',
    title,
    description: 'Deployment verification passed.',
    status: 'Passed',
    severity: 'Success',
    route: '/checks/chk_123',
    occurredUtc: '2026-05-03T12:00:00Z',
    metadataJson: '{"summary":"ok"}',
  };
}

// The server contract: a history page holds at most 500 entries, with totals for the whole set.
function serverTimeline(query?: HistoricalTimelineQuery) {
  const pageSize = Math.min(Math.max(query?.pageSize ?? 100, 1), 500);
  const pageNumber = query?.pageNumber ?? 1;
  const totalPages = Math.max(1, Math.ceil(SERVER_TOTAL / pageSize));
  return {
    success: true,
    pageNumber,
    pageSize,
    totalPages,
    totalRecords: SERVER_TOTAL,
    totalMs: 1,
    objects: pageNumber <= totalPages ? [entry(`his_${pageNumber}`, pageNumber === 1 ? 'Deploy finished' : `Page ${pageNumber} entry`)] : [],
  };
}

function lastQuery(): HistoricalTimelineQuery | undefined {
  const calls = vi.mocked(enumerateHistoryTimeline).mock.calls;
  return calls[calls.length - 1]?.[0];
}

function GoToObjective() {
  const navigate = useNavigate();
  return <button type="button" onClick={() => navigate('/history?objectiveId=obj_123')}>Go to objective history</button>;
}

function renderHistory() {
  render(
    <MemoryRouter initialEntries={['/history']}>
      <Routes>
        <Route path="/history" element={<><GoToObjective /><History /></>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('History', () => {
  const createObjectUrlMock = vi.fn(() => 'blob:history');
  const revokeObjectUrlMock = vi.fn();
  const anchorClickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

  beforeAll(() => {
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      writable: true,
      value: createObjectUrlMock,
    });
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      writable: true,
      value: revokeObjectUrlMock,
    });
  });

  beforeEach(() => {
    localStorage.clear();

    vi.mocked(enumerateHistoryTimeline).mockImplementation(async (query?: HistoricalTimelineQuery) => serverTimeline(query) as never);

    vi.mocked(listVessels).mockResolvedValue({
      success: true,
      pageNumber: 1,
      pageSize: 1000,
      totalPages: 1,
      totalRecords: 1,
      totalMs: 1,
      objects: [
        {
          id: 'vsl_123',
          name: 'History Vessel',
        } as never,
      ],
    });

    vi.mocked(listObjectives).mockResolvedValue({
      success: true,
      pageNumber: 1,
      pageSize: 500,
      totalPages: 1,
      totalRecords: 1,
      totalMs: 1,
      objects: [
        {
          id: 'obj_123',
          title: 'History Objective',
        } as never,
      ],
    });
  });

  afterEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
    createObjectUrlMock.mockClear();
    revokeObjectUrlMock.mockClear();
    anchorClickSpy.mockClear();
  });

  it('saves a view, reloads when the view is applied, and exports every page the server returns', async () => {
    renderHistory();

    expect(await screen.findByText('Deploy finished')).toBeInTheDocument();

    const searchInput = screen.getByPlaceholderText('Search title, status, route, or metadata...');
    fireEvent.change(searchInput, { target: { value: 'deploy' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(lastQuery()?.text).toBe('deploy'));

    fireEvent.click(screen.getByRole('button', { name: 'Save View' }));
    fireEvent.change(screen.getByPlaceholderText('Staging failures'), { target: { value: 'Deploy view' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('button', { name: 'Deploy view' })).toBeInTheDocument();
    expect(localStorage.getItem('armada_history_saved_views')).toContain('Deploy view');

    fireEvent.change(searchInput, { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(lastQuery()?.text ?? null).toBeNull());

    // Applying a saved view reloads the table with the view's filters.
    fireEvent.click(screen.getByRole('button', { name: 'Deploy view' }));
    expect(screen.getByDisplayValue('deploy')).toBeInTheDocument();
    await waitFor(() => expect(lastQuery()?.text).toBe('deploy'));

    const callsBeforeExport = vi.mocked(enumerateHistoryTimeline).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'Export JSON' }));
    await waitFor(() => expect(createObjectUrlMock).toHaveBeenCalled());

    const exportQueries = vi.mocked(enumerateHistoryTimeline).mock.calls.slice(callsBeforeExport).map((call) => call[0]);
    expect(exportQueries.length).toBe(2);
    for (const query of exportQueries) {
      expect(query?.pageSize ?? 0).toBeLessThanOrEqual(500);
      expect(query?.text).toBe('deploy');
    }
    expect(anchorClickSpy).toHaveBeenCalled();
  });

  it('applies the postmortem-only filter to history queries', async () => {
    renderHistory();

    expect(await screen.findByText('Deploy finished')).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText('Postmortem context only'));
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));

    await waitFor(() => {
      expect(lastQuery()).toMatchObject({
        pageNumber: 1,
        pageSize: 250,
        postmortemOnly: true,
      });
    });
  });

  it('reloads when the URL filters change', async () => {
    renderHistory();
    expect(await screen.findByText('Deploy finished')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Go to objective history' }));
    await waitFor(() => expect(lastQuery()?.objectiveId).toBe('obj_123'));
  });

  it('refreshes with the applied filters, not text still being typed', async () => {
    renderHistory();
    expect(await screen.findByText('Deploy finished')).toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText('Search title, status, route, or metadata...'), { target: { value: 'half-typ' } });
    const callsBefore = vi.mocked(enumerateHistoryTimeline).mock.calls.length;
    fireEvent.click(screen.getByTitle('Refresh history'));
    await waitFor(() => expect(vi.mocked(enumerateHistoryTimeline).mock.calls.length).toBeGreaterThan(callsBefore));
    expect(lastQuery()?.text ?? null).toBeNull();
  });

  it('offers every source type and shows the server total', async () => {
    renderHistory();
    expect(await screen.findByText('Deploy finished')).toBeInTheDocument();
    const sourceSelect = screen.getByDisplayValue('All source types') as HTMLSelectElement;
    const options = Array.from(sourceSelect.options).map((option) => option.value);
    expect(options).toEqual(expect.arrayContaining(['CheckRun', 'Deployment', 'Incident', 'Mission', 'Request', 'Voyage']));
    expect(screen.getByText(new RegExp(`of ${SERVER_TOTAL}`))).toBeInTheDocument();
  });
});
