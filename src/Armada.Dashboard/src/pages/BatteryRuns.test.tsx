import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import BatteryRuns from './BatteryRuns';
import BatteryRunDetail from './BatteryRunDetail';
import { getBatteryRun, listBatteryRuns } from '../api/client';
import type { BatteryRun } from '../types/models';

vi.mock('../api/client', () => ({
  listBatteryRuns: vi.fn(),
  getBatteryRun: vi.fn(),
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

const run: BatteryRun = {
  id: 'bat_example',
  captainId: 'cpt_example',
  captainName: 'example-captain',
  packId: 'csharp',
  packVersion: 2,
  status: 'Completed',
  error: null,
  startedUtc: '2026-01-02T00:00:00Z',
  completedUtc: '2026-01-02T00:10:00Z',
  passed: 1,
  failed: 1,
  couldNotRun: 1,
  results: [
    {
      taskId: 'add-member', title: 'Add a member', grade: 'Passed', score: 1, summary: 'Passed.', reason: null,
      voyageId: 'vyg_example', missionId: 'msn_example', startedUtc: '2026-01-02T00:00:00Z', completedUtc: '2026-01-02T00:03:00Z',
      durationMs: 180000, missionDurationMs: 150000,
      details: { commands: [{ commandLine: 'dotnet test', exitCode: 0, timedOut: false, passed: true, outputExcerpt: null }], files: [], mutants: [] },
    },
    {
      taskId: 'bom-crlf', title: 'Keep BOM and CRLF', grade: 'Failed', score: 0, summary: 'Failed.', reason: 'codes.txt differs',
      voyageId: 'vyg_example', missionId: 'msn_two', startedUtc: '2026-01-02T00:03:00Z', completedUtc: '2026-01-02T00:05:00Z',
      durationMs: 1500, missionDurationMs: null,
      details: { commands: [], files: [{ path: 'codes.txt', matched: false, expectedHash: 'a', actualHash: 'b', reason: 'CRLF line endings were converted to LF' }], mutants: [] },
    },
    {
      taskId: 'fix-bug', title: 'Fix a bug', grade: 'CouldNotRun', score: null, summary: 'Could not run.', reason: 'The Admiral host lacks the pack toolchain: dotnet',
      voyageId: null, missionId: null, startedUtc: '2026-01-02T00:05:00Z', completedUtc: '2026-01-02T00:05:01Z',
      durationMs: 1000, missionDurationMs: null,
      details: { commands: [], files: [], mutants: [] },
    },
  ],
};

describe('Battery pages', () => {
  beforeEach(() => {
    vi.mocked(listBatteryRuns).mockReset();
    vi.mocked(getBatteryRun).mockReset();
  });

  it('lists runs with captain, pack, grade counts and a link to the run', async () => {
    vi.mocked(listBatteryRuns).mockResolvedValue([run]);
    render(<MemoryRouter><BatteryRuns /></MemoryRouter>);

    const link = await screen.findByRole('link', { name: 'bat_example' });
    expect(link.getAttribute('href')).toBe('/battery/bat_example');
    expect(screen.getByText('example-captain')).toBeTruthy();
    expect(screen.getByText('csharp')).toBeTruthy();
    expect(screen.getByText('Completed')).toBeTruthy();
  });

  it('says runs are for global administrators when the route refuses the caller, and asks once', async () => {
    vi.mocked(listBatteryRuns).mockRejectedValue({ status: 403 });
    render(<MemoryRouter><BatteryRuns /></MemoryRouter>);

    expect(await screen.findByText('Battery runs are available to global administrators.')).toBeTruthy();
    expect(listBatteryRuns).toHaveBeenCalledTimes(1);
  });

  it('shows each task grade with its reason, evidence and mission link on the run page', async () => {
    vi.mocked(getBatteryRun).mockResolvedValue(run);
    render(
      <MemoryRouter initialEntries={['/battery/bat_example']}>
        <Routes><Route path="/battery/:id" element={<BatteryRunDetail />} /></Routes>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText('Add a member')).toBeTruthy());
    expect(screen.getByText('1 passed, 1 failed, 1 could not run')).toBeTruthy();
    expect(screen.getByText('Passed')).toBeTruthy();
    expect(screen.getByText('CouldNotRun')).toBeTruthy();
    expect(screen.getByText('codes.txt differs')).toBeTruthy();
    expect(screen.getByText('CRLF line endings were converted to LF')).toBeTruthy();
    expect(screen.getByText('dotnet test')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'msn_example' }).getAttribute('href')).toBe('/missions/msn_example');
    expect(screen.getByText('3.0 min')).toBeTruthy();
    expect(screen.getByText('2.5 min')).toBeTruthy();
  });

  it('says when a run is not found', async () => {
    vi.mocked(getBatteryRun).mockRejectedValue({ status: 404 });
    render(
      <MemoryRouter initialEntries={['/battery/bat_missing']}>
        <Routes><Route path="/battery/:id" element={<BatteryRunDetail />} /></Routes>
      </MemoryRouter>,
    );

    expect(await screen.findByText('Battery run not found.')).toBeTruthy();
  });
});
