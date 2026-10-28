import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Jobs from './Jobs';
import { enumerateJobs } from '../api/client';
import type { LongRunningJobPage } from '../types/models';

vi.mock('../api/client', () => ({
  enumerateJobs: vi.fn(),
}));

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      params
        ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
        : text,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => (value ? `rel:${value}` : ''),
  };
  return { useLocale: () => locale };
});

vi.mock('../lib/useAutoRefresh', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lib/useAutoRefresh')>()),
  useAutoRefresh: () => ({ seconds: 0, setSeconds: vi.fn() }),
}));

function renderPage() {
  return render(
    <MemoryRouter>
      <Jobs />
    </MemoryRouter>,
  );
}

describe('Jobs page', () => {
  beforeEach(() => {
    vi.mocked(enumerateJobs).mockReset();
  });

  it('lists long-running jobs with status, objective and vessel links, times and the failure message', async () => {
    const list: LongRunningJobPage = {
      success: true,
      pageNumber: 1,
      pageSize: 50,
      totalPages: 1,
      totalMs: 0,
      totalRecords: 2,
      unreadableJournalRecords: 0,
      objects: [
        {
          jobId: 'job_running',
          operation: 'voyage_dispatch',
          status: 'Running',
          submittedAtUtc: '2026-01-02T00:00:00Z',
          startedAtUtc: '2026-01-02T00:00:01Z',
          completedAtUtc: null,
          failureMessage: null,
          objectiveId: 'obj_example',
          vesselId: 'vsl_example',
        },
        {
          jobId: 'job_lost',
          operation: 'code_index_update',
          status: 'Lost',
          submittedAtUtc: '2026-01-01T00:00:00Z',
          startedAtUtc: null,
          completedAtUtc: '2026-01-01T00:05:00Z',
          failureMessage: 'job_lost_on_restart: the admiral process stopped',
          objectiveId: null,
          vesselId: null,
        },
      ],
    };
    vi.mocked(enumerateJobs).mockResolvedValue(list);

    renderPage();

    expect(await screen.findByText('voyage_dispatch')).toBeInTheDocument();
    expect(screen.getByText('Running')).toBeInTheDocument();
    expect(screen.getByText('Lost')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'obj_example' })).toHaveAttribute('href', '/objectives/obj_example');
    expect(screen.getByRole('link', { name: 'vsl_example' })).toHaveAttribute('href', '/vessels/vsl_example');
    expect(screen.getByText('job_lost_on_restart: the admiral process stopped')).toBeInTheDocument();
    expect(screen.getByText('rel:2026-01-02T00:00:01Z')).toBeInTheDocument();
    expect(screen.getByText('rel:2026-01-01T00:05:00Z')).toBeInTheDocument();
    // Long-running jobs have no cancel operation.
    expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument();
  });

  it('says how many journal records could not be read', async () => {
    vi.mocked(enumerateJobs).mockResolvedValue({ success: true, pageNumber: 1, pageSize: 50, totalPages: 0, totalMs: 0, totalRecords: 0, unreadableJournalRecords: 2, objects: [] });

    renderPage();

    expect(await screen.findByText(/2 job journal records could not be read/)).toBeInTheDocument();
    expect(screen.getByText('No background jobs.')).toBeInTheDocument();
  });

  it('tells a caller who is not a global administrator why the list is unavailable', async () => {
    vi.mocked(enumerateJobs).mockRejectedValue(Object.assign(new Error('403: Forbidden'), { status: 403 }));

    renderPage();

    expect(await screen.findByText('Background jobs are available to global administrators.')).toBeInTheDocument();
    expect(screen.queryByText('Failed to load jobs.')).not.toBeInTheDocument();
  });

  it('reads one page at a time and asks for the next page from the pager', async () => {
    const job = (jobId: string) => ({
      jobId,
      operation: 'code_index_update',
      status: 'Succeeded' as const,
      submittedAtUtc: '2026-01-01T00:00:00Z',
      startedAtUtc: null,
      completedAtUtc: null,
      failureMessage: null,
      objectiveId: null,
      vesselId: null,
    });
    vi.mocked(enumerateJobs).mockImplementation(async (query) => ({
      success: true,
      pageNumber: query?.pageNumber ?? 1,
      pageSize: query?.pageSize ?? 50,
      totalPages: 2,
      totalRecords: 51,
      totalMs: 0,
      unreadableJournalRecords: 0,
      objects: [job(query?.pageNumber === 2 ? 'job_second_page' : 'job_first_page')],
    }));

    renderPage();

    expect(await screen.findByText('job_first_page')).toBeInTheDocument();
    expect(enumerateJobs).toHaveBeenLastCalledWith({ pageNumber: 1, pageSize: 50 });
    expect(screen.getByText('51 records')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText('job_second_page')).toBeInTheDocument();
    await waitFor(() => expect(enumerateJobs).toHaveBeenLastCalledWith({ pageNumber: 2, pageSize: 50 }));
    expect(screen.queryByText('job_first_page')).not.toBeInTheDocument();
  });
});
