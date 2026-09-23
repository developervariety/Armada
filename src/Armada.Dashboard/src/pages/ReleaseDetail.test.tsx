import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      params
        ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
        : text,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../context/AuthContext', () => ({ useAuth: () => ({ isAdmin: true, isTenantAdmin: false }) }));

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  createRelease: vi.fn(),
  deleteRelease: vi.fn(),
  getCheckRun: vi.fn(),
  getRelease: vi.fn(),
  getReleaseGitHubPullRequests: vi.fn(),
  listDeployments: vi.fn(),
  listObjectives: vi.fn(),
  listVessels: vi.fn(),
  listVoyages: vi.fn(),
  listWorkflowProfiles: vi.fn(),
  refreshRelease: vi.fn(),
  updateRelease: vi.fn(),
}));

import {
  getCheckRun, getRelease, getReleaseGitHubPullRequests, listDeployments, listObjectives, listVessels, listVoyages, listWorkflowProfiles,
} from '../api/client';
import { servesPages } from '../test/clientMock';
import ReleaseDetail from './ReleaseDetail';

const release = {
  id: 'rel_1',
  tenantId: null,
  userId: null,
  vesselId: null,
  workflowProfileId: null,
  title: 'Spring release',
  version: '1.0.0',
  tagName: null,
  summary: null,
  notes: null,
  status: 'Draft',
  voyageIds: ['vyg_1000'],
  missionIds: [],
  checkRunIds: ['chk_linked'],
  artifacts: [],
  createdUtc: '2026-09-01T00:00:00Z',
  lastUpdateUtc: '2026-09-01T00:00:00Z',
  publishedUtc: null,
};

function renderDetail() {
  return render(
    <MemoryRouter initialEntries={['/releases/rel_1']}>
      <Routes>
        <Route path="/releases/:id" element={<ReleaseDetail />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  const empty = servesPages([]);
  vi.mocked(getRelease).mockResolvedValue(release as never);
  vi.mocked(getReleaseGitHubPullRequests).mockResolvedValue([] as never);
  vi.mocked(listVessels).mockImplementation(empty as never);
  vi.mocked(listWorkflowProfiles).mockImplementation(empty as never);
  vi.mocked(listObjectives).mockImplementation(empty as never);
  vi.mocked(getCheckRun).mockResolvedValue({ id: 'chk_linked', label: 'Linked unit run', type: 'UnitTest' } as never);
});

test('names a linked voyage that sits past the server page cap', async () => {
  const voyages = Array.from({ length: 1001 }, (_, index) => ({ id: `vyg_${index}`, title: `Voyage ${index}` }));
  vi.mocked(listVoyages).mockImplementation(servesPages(voyages) as never);
  vi.mocked(listDeployments).mockImplementation(servesPages([]) as never);

  renderDetail();

  expect(await screen.findByText('Voyage 1000')).toBeInTheDocument();
});

test('lists the deployments linked to the release through the release filter', async () => {
  vi.mocked(listVoyages).mockImplementation(servesPages([]) as never);
  vi.mocked(listDeployments).mockImplementation((async (query?: { releaseId?: string | null; pageNumber?: number; pageSize?: number }) => {
    const linked = query?.releaseId === 'rel_1'
      ? [{ id: 'dpl_1', title: 'Linked deployment', releaseId: 'rel_1', status: 'Succeeded', verificationStatus: 'Passed', checkRunIds: [] }]
      : [];
    return servesPages(linked)(query);
  }) as never);

  renderDetail();

  expect(await screen.findByText('Linked deployment')).toBeInTheDocument();
  expect(listDeployments).toHaveBeenCalledWith(expect.objectContaining({ releaseId: 'rel_1' }));
});

test('names a linked check run by reading that run, not every run', async () => {
  vi.mocked(listVoyages).mockImplementation(servesPages([]) as never);
  vi.mocked(listDeployments).mockImplementation(servesPages([]) as never);

  renderDetail();

  expect(await screen.findByText('Linked unit run')).toBeInTheDocument();
  expect(getCheckRun).toHaveBeenCalledWith('chk_linked');
});
