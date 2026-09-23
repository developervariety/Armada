import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { expect, test, vi } from 'vitest';

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number>) =>
      (params ? text.replace(/\{\{(\w+)\}\}/g, (_, key: string) => String(params[key] ?? '')) : text),
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../components/shared/PlaybookSelector', () => ({ default: () => null }));

vi.mock('../api/client', async () => {
  const { withAllPages } = await import('../test/clientMock');
  const empty = { success: true, pageNumber: 1, pageSize: 1000, totalPages: 1, totalRecords: 0, totalMs: 1, objects: [] };
  return withAllPages({
    listVessels: vi.fn().mockResolvedValue(empty),
    listPipelines: vi.fn().mockResolvedValue(empty),
    createVoyage: vi.fn(),
  });
});

import VoyageCreate from './VoyageCreate';

test('does not offer landing options the voyage request cannot carry', async () => {
  render(<MemoryRouter><VoyageCreate /></MemoryRouter>);
  expect(await screen.findByRole('heading', { name: 'Create Voyage' })).toBeInTheDocument();
  expect(screen.queryByText('Auto-Push')).not.toBeInTheDocument();
  expect(screen.queryByText('Auto-Create PRs')).not.toBeInTheDocument();
  expect(screen.queryByText('Auto-Merge PRs')).not.toBeInTheDocument();
});
