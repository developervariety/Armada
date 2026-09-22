import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';
import { PICKER_RECORD_COUNT, optionValues, pickerRecords, serverDefaultPage } from '../test/pickerRecords';

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      (params ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text) : text),
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({ pushToast: vi.fn() }),
}));

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ isAdmin: true, isTenantAdmin: true, user: null }),
}));

vi.mock('../api/client', () => ({
  createPipeline: vi.fn(),
  getPipeline: vi.fn(),
  updatePipeline: vi.fn(),
  deletePipeline: vi.fn(),
  listPersonas: vi.fn(),
  listVessels: vi.fn(),
  listAllVessels: vi.fn(),
  createVoyage: vi.fn(),
}));

import { getPipeline, listAllVessels, listPersonas, listVessels } from '../api/client';
import PipelineDetail from './PipelineDetail';

const vessels = pickerRecords('vsl');

beforeEach(() => {
  vi.mocked(getPipeline).mockResolvedValue({
    id: 'ppl_1', tenantId: null, ownershipScope: 'Global', name: 'Reviewed', description: null,
    stages: [{ order: 1, personaName: 'Worker', isOptional: false, description: null }],
    isBuiltIn: false, active: true, createdUtc: '2026-01-01T00:00:00Z', lastUpdateUtc: '2026-01-01T00:00:00Z',
  } as never);
  vi.mocked(listPersonas).mockResolvedValue(serverDefaultPage([{ name: 'Worker' }]) as never);
  vi.mocked(listVessels).mockResolvedValue(serverDefaultPage(vessels) as never);
  vi.mocked(listAllVessels).mockResolvedValue(vessels as never);
});

test('offers every vessel in the run picker, not only the first server page', async () => {
  render(
    <MemoryRouter initialEntries={['/pipelines/Reviewed']}>
      <Routes><Route path="/pipelines/:name" element={<PipelineDetail />} /></Routes>
    </MemoryRouter>,
  );

  fireEvent.click(await screen.findByTitle('Dispatch a voyage using this pipeline'));
  const heading = await screen.findByText('Run Pipeline');
  const picker = heading.closest('form')!.querySelector('select') as HTMLSelectElement;
  await waitFor(() => expect(optionValues(picker)).toHaveLength(PICKER_RECORD_COUNT));
});
