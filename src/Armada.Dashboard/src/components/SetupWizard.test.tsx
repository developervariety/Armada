import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import SetupWizard from './SetupWizard';
import { dispatchMission, getMission, getVesselReadiness, listCaptains, listEnvironments, listFleets, listVessels, listWorkflowProfiles } from '../api/client';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listFleets: vi.fn(),
  listVessels: vi.fn(),
  listCaptains: vi.fn(),
  listWorkflowProfiles: vi.fn(),
  listEnvironments: vi.fn(),
  createFleet: vi.fn(),
  createVessel: vi.fn(),
  createCaptain: vi.fn(),
  dispatchMission: vi.fn(),
  getMission: vi.fn(),
  getVesselReadiness: vi.fn(),
}));

vi.mock('../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      params
        ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
        : text,
  };
  return { useLocale: () => locale };
});

const page = (objects: unknown[]) => ({ objects });

describe('Setup wizard mission failure', () => {
  beforeEach(() => {
    vi.mocked(listFleets).mockResolvedValue(page([{ id: 'flt_1', name: 'Starter' }]) as never);
    vi.mocked(listVessels).mockResolvedValue(page([{ id: 'vsl_1', name: 'Repo', defaultBranch: 'main' }]) as never);
    vi.mocked(listCaptains).mockResolvedValue(page([{ id: 'cpt_1', name: 'Setup Captain', state: 'Idle' }]) as never);
    vi.mocked(listWorkflowProfiles).mockResolvedValue(page([]) as never);
    vi.mocked(listEnvironments).mockResolvedValue(page([]) as never);
    vi.mocked(getVesselReadiness).mockResolvedValue({ setupChecklist: [], setupChecklistSatisfiedCount: 0, setupChecklistTotalCount: 0, errorCount: 0 } as never);
    vi.mocked(dispatchMission).mockResolvedValue({ id: 'msn_setup', title: 'Repository onboarding survey', status: 'Pending' } as never);
    vi.mocked(getMission).mockResolvedValue({
      id: 'msn_setup',
      title: 'Repository onboarding survey',
      status: 'Failed',
      failureReason: 'The worktree was not created.',
      captainId: 'cpt_1',
      vesselId: 'vsl_1',
    } as never);
  });

  it('shows the failure reason and an open-mission link after refresh', async () => {
    render(
      <MemoryRouter>
        <SetupWizard onClose={() => {}} />
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Start Setup' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Use Fleet' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Use Vessel' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Use Captain' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Dispatch Mission' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Refresh Mission Status' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The worktree was not created.');
    expect(screen.getByRole('link', { name: 'Open Mission' })).toHaveAttribute('href', '/missions/msn_setup');
  });
});
