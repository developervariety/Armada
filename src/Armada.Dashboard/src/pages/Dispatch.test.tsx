import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
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

vi.mock('../components/shared/PlaybookSelector', () => ({ default: () => null }));

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listVessels: vi.fn(),
  listPipelines: vi.fn(),
  listCaptains: vi.fn(),
  listPersonas: vi.fn(),
  createVoyage: vi.fn(),
  getVesselReadiness: vi.fn(),
}));

import { listCaptains, listPersonas, listPipelines, listVessels } from '../api/client';
import { servesPages } from '../test/clientMock';
import Dispatch from './Dispatch';

/** One more record than the server serves in a single page. */
const COUNT = 1001;
const LAST = COUNT - 1;

function records<T>(build: (index: number) => T): T[] {
  return Array.from({ length: COUNT }, (_, index) => build(index));
}

function optionValues(select: HTMLElement): string[] {
  return Array.from((select as HTMLSelectElement).options).map((option) => option.value).filter(Boolean);
}

function selectOffering(label: string): HTMLSelectElement {
  const select = screen.getAllByRole('combobox').find((element) =>
    Array.from((element as HTMLSelectElement).options).some((option) => option.textContent === label));
  if (!select) throw new Error(`No select offers "${label}"`);
  return select as HTMLSelectElement;
}

beforeEach(() => {
  vi.mocked(listVessels).mockImplementation(servesPages(records((index) => ({ id: `vsl_${index}`, name: `Vessel ${index}` }))) as never);
  vi.mocked(listCaptains).mockImplementation(servesPages(records((index) => ({ id: `cpt_${index}`, name: `Captain ${index}` }))) as never);
  // Pipeline 0 runs the last persona, so its step reads a persona that only a second page returns.
  vi.mocked(listPipelines).mockImplementation(servesPages(records((index) => ({
    id: `ppl_${index}`,
    name: `Pipeline ${index}`,
    stages: [{ order: 1, personaName: `Persona ${LAST - index}` }],
  }))) as never);
  // The last persona defaults to the first captain, so only the persona read decides the seeded captain.
  vi.mocked(listPersonas).mockImplementation(servesPages(records((index) => ({
    name: `Persona ${index}`,
    defaultCaptainId: `cpt_${LAST - index}`,
  }))) as never);
});

test.each([
  ['vessel', () => selectOffering('Select a vessel...'), `vsl_${LAST}`],
  ['pipeline', () => selectOffering('Inherit (vessel, then fleet, then WorkerOnly)'), `Pipeline ${LAST}`],
  ['captain', () => screen.getByLabelText('Preferred captain for *'), `cpt_${LAST}`],
] as const)('offers every %s, not only the first server page', async (_name, picker, lastValue) => {
  render(<MemoryRouter><Dispatch /></MemoryRouter>);

  await waitFor(() => expect(optionValues(picker())).toHaveLength(COUNT));
  expect(optionValues(picker())).toContain(lastValue);
});

test('seeds a step with the default captain of a persona past the first server page', async () => {
  render(<MemoryRouter><Dispatch /></MemoryRouter>);

  const pipelineSelect = await waitFor(() => {
    const select = selectOffering('Inherit (vessel, then fleet, then WorkerOnly)');
    expect(optionValues(select)).toContain('Pipeline 0');
    return select;
  });
  await waitFor(() => expect(optionValues(screen.getByLabelText('Preferred captain for *'))).toContain('cpt_0'));
  fireEvent.change(pipelineSelect, { target: { value: 'Pipeline 0' } });

  await waitFor(() => expect((screen.getByLabelText(`Preferred captain for Persona ${LAST}`) as HTMLSelectElement).value).toBe('cpt_0'));
});
