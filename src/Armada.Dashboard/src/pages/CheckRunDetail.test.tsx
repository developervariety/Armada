import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';
import type { WebSocketMessage } from '../types/models';

const handlers = new Set<(msg: WebSocketMessage) => void>();

vi.mock('../context/WebSocketContext', () => ({
  RESYNC_MESSAGE_TYPE: 'client.resync',
  useWebSocket: () => ({
    connected: true,
    send: vi.fn(),
    subscribe: (handler: (msg: WebSocketMessage) => void) => {
      handlers.add(handler);
      return () => handlers.delete(handler);
    },
  }),
}));

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number>) =>
      (params ? text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(params[key] ?? '')) : text),
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', () => ({
  deleteCheckRun: vi.fn(),
  getCheckRun: vi.fn(),
  getVessel: vi.fn(),
  getWorkflowProfile: vi.fn(),
  listCheckRuns: vi.fn(),
  retryCheckRun: vi.fn(),
}));

import { getCheckRun, getVessel, getWorkflowProfile, listCheckRuns } from '../api/client';
import CheckRunDetail from './CheckRunDetail';
import { deferred, NavigateButton } from '../test/routeRace';

function run(status: string) {
  return {
    id: 'chk_1', tenantId: null, userId: null, workflowProfileId: null, vesselId: 'vsl_1', missionId: null, voyageId: null,
    deploymentId: null, label: 'Unit Tests', type: 'UnitTest', source: 'Armada', status, providerName: null, externalId: null,
    externalUrl: null, environmentName: null, command: 'dotnet test', workingDirectory: '/repo', branchName: 'main',
    commitHash: 'abc', exitCode: null, output: '', summary: null, testSummary: null, coverageSummary: null, artifacts: [],
    durationMs: null, startedUtc: null, completedUtc: null, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  };
}

beforeEach(() => {
  handlers.clear();
  vi.mocked(getCheckRun).mockReset();
  vi.mocked(getCheckRun).mockResolvedValue(run('Running') as never);
  vi.mocked(getVessel).mockResolvedValue({ id: 'vsl_1', name: 'Vessel One' } as never);
  vi.mocked(getWorkflowProfile).mockResolvedValue(null as never);
  // 900 earlier runs of this type exist; the route returns at most 500.
  vi.mocked(listCheckRuns).mockImplementation(async (params?: { pageSize?: number }) => {
    const pageSize = Math.min(params?.pageSize ?? 10, 500);
    return {
      success: true, pageNumber: 1, pageSize, totalPages: Math.ceil(900 / pageSize), totalRecords: 900, totalMs: 1,
      objects: [run('Passed')].map((item, index) => ({ ...item, id: `chk_old_${index}` })),
    } as never;
  });
});

function renderPage() {
  render(
    <MemoryRouter initialEntries={['/checks/chk_1']}>
      <Routes>
        <Route path="/checks/:id" element={<CheckRunDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

test('updates a running check when the server reports a change', async () => {
  renderPage();
  expect((await screen.findAllByText('Running')).length).toBeGreaterThan(0);
  act(() => {
    handlers.forEach((handler) => handler({ type: 'check-run.changed', data: { ...run('Passed'), exitCode: 0 } }));
  });
  await waitFor(() => expect(screen.queryAllByText('Running')).toHaveLength(0));
  expect(screen.getAllByText('Passed').length).toBeGreaterThan(0);
});

test('says when the previous-run comparison searched only the newest runs', async () => {
  renderPage();
  expect((await screen.findAllByText('Running')).length).toBeGreaterThan(0);
  for (const call of vi.mocked(listCheckRuns).mock.calls) {
    expect(call[0]?.pageSize ?? 0).toBeLessThanOrEqual(500);
  }
  expect(await screen.findByText(/newest 500 of 900/)).toBeInTheDocument();
});

test('a route change to another run shows loading, then the failure, never the previous run', async () => {
  const second = deferred<unknown>();
  vi.mocked(getCheckRun).mockImplementation((async (id: string) => (id === 'chk_2' ? second.promise : run('Running'))) as never);
  render(
    <MemoryRouter initialEntries={['/checks/chk_1']}>
      <NavigateButton to="/checks/chk_2" />
      <Routes>
        <Route path="/checks/:id" element={<CheckRunDetail />} />
      </Routes>
    </MemoryRouter>,
  );
  expect((await screen.findAllByText('Unit Tests')).length).toBeGreaterThan(0);

  fireEvent.click(screen.getByText('go /checks/chk_2'));

  expect(await screen.findByText('Loading...')).toBeInTheDocument();
  expect(screen.queryAllByText('Unit Tests')).toHaveLength(0);

  await act(async () => { second.reject(new Error('Check run chk_2 was deleted.')); });

  expect(await screen.findByText('Check run chk_2 was deleted.')).toBeInTheDocument();
  expect(screen.queryAllByText('Unit Tests')).toHaveLength(0);
});
