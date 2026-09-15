import { render, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';
import type { RunbookExecutionQuery } from '../types/models';

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
  listEnvironments: vi.fn(),
  listRunbookExecutions: vi.fn(),
  listRunbooks: vi.fn(),
  listWorkflowProfiles: vi.fn(),
}));

import { listEnvironments, listRunbookExecutions, listRunbooks, listWorkflowProfiles } from '../api/client';
import Runbooks from './Runbooks';

const empty = { success: true, pageNumber: 1, pageSize: 500, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };

function execution(index: number, status: string) {
  return { id: `rbx_${index}`, runbookId: 'rbk_1', title: `Execution ${index}`, status, completedStepIds: [], lastUpdateUtc: '2026-01-01T00:00:00Z' };
}

// Server totals: 750 executions overall (3 running); 620 for rbk_1 (2 running). A page holds at most 500.
function totalFor(query: RunbookExecutionQuery | undefined): number {
  if (query?.runbookId === 'rbk_1') return query.status === 'Running' ? 2 : 620;
  return query?.status === 'Running' ? 3 : 750;
}

beforeEach(() => {
  vi.mocked(listWorkflowProfiles).mockResolvedValue(empty as never);
  vi.mocked(listEnvironments).mockResolvedValue(empty as never);
  vi.mocked(listRunbooks).mockResolvedValue({
    ...empty,
    totalRecords: 1,
    objects: [{
      id: 'rbk_1', title: 'Deploy Runbook', fileName: 'RUNBOOK.md', description: null, workflowProfileId: null,
      environmentId: null, environmentName: null, defaultCheckType: null, active: true, steps: [], parameters: [],
      createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
    }],
  } as never);
  vi.mocked(listRunbookExecutions).mockImplementation(async (query) => {
    const total = totalFor(query);
    const size = Math.min(query?.pageSize ?? 10, 500, total);
    return {
      ...empty,
      pageSize: query?.pageSize ?? 10,
      totalPages: Math.max(1, Math.ceil(total / Math.max(1, Math.min(query?.pageSize ?? 10, 500)))),
      totalRecords: total,
      objects: Array.from({ length: size }, (_, index) => execution(index, index < 3 ? 'Running' : 'Completed')),
    } as never;
  });
});

function cardValue(label: string) {
  const card = Array.from(document.querySelectorAll('.playbook-overview-card'))
    .find((element) => element.querySelector('span')?.textContent === label);
  return card?.querySelector('strong')?.textContent;
}

test('execution totals come from the server, not from one capped page', async () => {
  const { container } = render(<MemoryRouter><Runbooks /></MemoryRouter>);
  await waitFor(() => expect(cardValue('Executions')).toBe('750'));
  expect(cardValue('Running')).toBe('3');
  expect(container.textContent).toContain('620 total');
  expect(container.textContent).toContain('2 running');
});
