import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Jobs from './Jobs';
import { listJobs } from '../api/client';
import type { LongRunningJobList } from '../types/models';

vi.mock('../api/client', () => ({
  listJobs: vi.fn(),
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
    vi.mocked(listJobs).mockReset();
  });

  it('lists long-running jobs with status, objective and vessel links, times and the failure message', async () => {
    const list: LongRunningJobList = {
      success: true,
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
    vi.mocked(listJobs).mockResolvedValue(list);

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
    vi.mocked(listJobs).mockResolvedValue({ success: true, totalRecords: 0, unreadableJournalRecords: 2, objects: [] });

    renderPage();

    expect(await screen.findByText(/2 job journal records could not be read/)).toBeInTheDocument();
    expect(screen.getByText('No background jobs.')).toBeInTheDocument();
  });

  it('tells a caller who is not a global administrator why the list is unavailable', async () => {
    vi.mocked(listJobs).mockRejectedValue(Object.assign(new Error('403: Forbidden'), { status: 403 }));

    renderPage();

    expect(await screen.findByText('Background jobs are available to global administrators.')).toBeInTheDocument();
    expect(screen.queryByText('Failed to load jobs.')).not.toBeInTheDocument();
  });
});
