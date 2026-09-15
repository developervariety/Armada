import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number>) =>
      (params ? text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(params[key] ?? '')) : text),
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ isAdmin: true, isTenantAdmin: false }),
}));

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../api/client', () => ({
  createRunbook: vi.fn(),
  deleteRunbook: vi.fn(),
  getRunbook: vi.fn(),
  listEnvironments: vi.fn(),
  listRunbookExecutions: vi.fn(),
  listWorkflowProfiles: vi.fn(),
  startRunbookExecution: vi.fn(),
  updateRunbook: vi.fn(),
  updateRunbookExecution: vi.fn(),
}));

import { getRunbook, listEnvironments, listRunbookExecutions, listWorkflowProfiles } from '../api/client';
import RunbookDetail from './RunbookDetail';

const empty = { success: true, pageNumber: 1, pageSize: 500, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };

function execution(index: number) {
  return {
    id: `rbx_${index}`, runbookId: 'rbk_1', title: `Execution ${index}`, status: 'Completed', workflowProfileId: null,
    environmentId: null, environmentName: null, checkType: null, deploymentId: null, incidentId: null,
    parameterValues: {}, completedStepIds: [], stepNotes: {}, notes: null,
    createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  };
}

beforeEach(() => {
  vi.mocked(listWorkflowProfiles).mockResolvedValue(empty as never);
  vi.mocked(listEnvironments).mockResolvedValue(empty as never);
  vi.mocked(getRunbook).mockResolvedValue({
    id: 'rbk_1', playbookId: 'pbk_1', fileName: 'RUNBOOK.md', title: 'Deploy Runbook', description: null,
    workflowProfileId: null, environmentId: null, environmentName: null, defaultCheckType: null, active: true,
    overviewMarkdown: '', parameters: [], steps: [], createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  } as never);
  // The runbook has 620 executions; one page holds at most 500.
  vi.mocked(listRunbookExecutions).mockImplementation(async (query) => {
    const size = Math.min(query?.pageSize ?? 10, 500, 620);
    return {
      ...empty, pageSize: size, totalPages: Math.ceil(620 / Math.max(1, size)), totalRecords: 620,
      objects: Array.from({ length: size }, (_, index) => execution(index)),
    } as never;
  });
});

function detailValue(label: string) {
  const field = Array.from(document.querySelectorAll('.detail-field'))
    .find((element) => element.querySelector('.detail-label')?.textContent === label);
  return field?.querySelectorAll('span')[1]?.textContent;
}

test('shows the server execution total and says when only the newest executions are listed', async () => {
  render(
    <MemoryRouter initialEntries={['/runbooks/rbk_1']}>
      <Routes>
        <Route path="/runbooks/:id" element={<RunbookDetail />} />
      </Routes>
    </MemoryRouter>,
  );
  await waitFor(() => expect(detailValue('Executions')).toBe('620'));
  expect(screen.getByText(/newest 500 of 620/)).toBeInTheDocument();
});
