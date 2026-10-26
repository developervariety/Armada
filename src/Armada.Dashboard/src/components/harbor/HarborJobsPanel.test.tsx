import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';
import { getHarborJobs } from '../../api/client';
import HarborJobsPanel from './HarborJobsPanel';

vi.mock('../../api/client', () => ({ getHarborJobs: vi.fn() }));
vi.mock('../../context/LocaleContext', () => {
  const locale = { t: (value: string) => value, formatDateTime: (value: string) => value };
  return { useLocale: () => locale };
});
const job = { jobId: 'job_1', runnerId: 'runner_1', state: 'Uncertain', missionId: 'msn_1', captainId: 'cpt_1', exitCode: null, failureReason: 'connection lost', stopRequested: true, stopConfirmed: false, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-02T00:00:00Z', completedUtc: null, launchKey: 'secret', tenantId: 'private', processId: 444 } as never;
function renderPanel() { return render(<MemoryRouter><HarborJobsPanel /></MemoryRouter>); }

beforeEach(() => { vi.mocked(getHarborJobs).mockReset(); });

test('a successful empty response shows an empty list', async () => {
  vi.mocked(getHarborJobs).mockResolvedValue({ jobs: [] });
  renderPanel();
  expect(await screen.findByText('No Harbor jobs match these filters.')).toBeInTheDocument();
});

test.each([
  [404, 'Harbor jobs are unavailable. Harbor may be disabled on this server.'],
  [403, 'You do not have access to these Harbor jobs.'],
])('HTTP %s has a distinct status message and is not an empty list', async (status, message) => {
  vi.mocked(getHarborJobs).mockRejectedValue(Object.assign(new Error('Refused'), { status }));
  renderPanel();
  expect(await screen.findByRole('alert')).toHaveTextContent(message);
  expect(screen.queryByText('No Harbor jobs match these filters.')).not.toBeInTheDocument();
});

test('401 remains an authentication error', async () => {
  vi.mocked(getHarborJobs).mockRejectedValue(new Error('Unauthorized'));
  renderPanel();
  expect(await screen.findByRole('alert')).toHaveTextContent('Your session is not authorized. Sign in again to view Harbor jobs.');
});

test('Uncertain does not imply stopped and sensitive runner fields are not shown', async () => {
  vi.mocked(getHarborJobs).mockResolvedValue({ jobs: [job] });
  renderPanel();
  expect(await screen.findByText('job_1')).toBeInTheDocument();
  expect(screen.getByText('Outcome is uncertain; this does not confirm that the job stopped.')).toBeInTheDocument();
  expect(screen.getByText('Stop confirmed').closest('th')).toBeInTheDocument();
  expect(screen.getByText('Stop requested').closest('th')).toBeInTheDocument();
  expect(screen.getByText('No')).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'msn_1' })).toHaveAttribute('href', '/missions/msn_1');
  expect(screen.getByRole('link', { name: 'cpt_1' })).toHaveAttribute('href', '/captains/cpt_1');
  expect(screen.queryByText(/secret|private|444/)).not.toBeInTheDocument();
});

test('a late response from an older refresh cannot replace the newest result', async () => {
  let finishFirst!: (value: { jobs: never[] }) => void;
  const first = new Promise<{ jobs: never[] }>((resolve) => { finishFirst = resolve; });
  vi.mocked(getHarborJobs).mockReturnValueOnce(first).mockResolvedValueOnce({ jobs: [job] });
  const { container } = renderPanel();
  fireEvent.submit(container.querySelector('form')!);
  expect(await screen.findByText('job_1')).toBeInTheDocument();
  await act(async () => { finishFirst({ jobs: [] }); });
  expect(screen.getByText('job_1')).toBeInTheDocument();
});
