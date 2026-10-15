import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import Jobs from './Jobs';
import { listJobs } from '../api/client';
import { translateTemplate } from '../i18n/runtime';
import type { Job } from '../types/models';

vi.mock('../api/client', () => ({ listJobs: vi.fn(), cancelJob: vi.fn() }));
const localeValue = {
  t: (text: string, params?: Record<string, string | number | null | undefined>) => translateTemplate('en', text, null, params),
  locale: 'en',
  formatDateTime: (v: string | null | undefined) => v ?? '',
  formatRelativeTime: (v: string | null | undefined) => v ?? '',
};
vi.mock('../context/LocaleContext', () => ({ useLocale: () => localeValue }));
vi.mock('../context/NotificationContext', () => ({ useNotifications: () => ({ pushToast: vi.fn() }) }));
vi.mock('../lib/useAutoRefresh', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lib/useAutoRefresh')>()),
  useAutoRefresh: () => ({ seconds: 0, setSeconds: vi.fn() }),
}));

function job(n: number): Job {
  return { id: `job_${n}`, tenantId: 'ten', userId: null, name: `Job ${n}`, kind: 'Generic', status: 'Succeeded', progress: 100, resultJson: null, errorReason: null, createdUtc: '', startedUtc: null, completedUtc: null, lastUpdateUtc: '' } as Job;
}

describe('Jobs', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.mocked(listJobs).mockImplementation(async (params) => {
      const pageNumber = params?.pageNumber ?? 1;
      const pageSize = params?.pageSize ?? 100;
      return { success: true, pageNumber, pageSize, totalPages: Math.ceil(240 / pageSize), totalRecords: 240, totalMs: 1, objects: [job(pageNumber)] };
    });
  });

  it('pages on the server and shows the true total instead of the 100 the route returns by default', async () => {
    render(<Jobs />);
    expect(await screen.findByText('Job 1')).toBeInTheDocument();
    expect(listJobs).toHaveBeenCalledWith({ pageNumber: 1, pageSize: 25 });
    expect(screen.getByText(/240 records/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Next/ }));
    await waitFor(() => expect(listJobs).toHaveBeenCalledWith({ pageNumber: 2, pageSize: 25 }));
    expect(await screen.findByText('Job 2')).toBeInTheDocument();
  });
});
