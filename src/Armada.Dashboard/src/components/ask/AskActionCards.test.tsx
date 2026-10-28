import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import AskActionCards from './AskActionCards';
import { decideAskProposal, listAskProposals, listAskWorkReports } from '../../api/client';

vi.mock('../../api/client', () => ({
  decideAskProposal: vi.fn(), listAskProposals: vi.fn(), listAskWorkReports: vi.fn(),
}));

const proposal = {
  id: 'p1', tenantId: 't', userId: 'u', threadId: 'thread-a', requestId: 'r1', toolName: 'create_mission',
  argumentsJson: '{"title":"Repair item","vesselId":"vsl_1"}', summary: 'Create mission "Repair item" on vessel Harbor Alpha', fingerprint: 'a'.repeat(64), state: 'Pending' as const,
  createdUtc: '2026-10-25T12:00:00Z', expiresUtc: '2099-10-25T12:00:00Z', decidedUtc: null,
  completedUtc: null, outcome: null, workReferences: [],
};
const t = (value: string) => value;

describe('AskActionCards', () => {
  beforeEach(() => { vi.clearAllMocks(); vi.mocked(listAskProposals).mockResolvedValue([proposal]); vi.mocked(listAskWorkReports).mockResolvedValue([]); });

  it('shows canonical arguments and sends the stored fingerprint once for a manual decision', async () => {
    vi.mocked(decideAskProposal).mockResolvedValue({ statusCode: 200, proposal: { ...proposal, state: 'Executing' }, message: null });
    vi.mocked(listAskProposals).mockResolvedValueOnce([proposal]).mockResolvedValue([{ ...proposal, state: 'Executing' }]);
    render(<AskActionCards threadId="thread-a" busy={false} t={t} />);
    expect(await screen.findByText(proposal.argumentsJson)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(decideAskProposal).toHaveBeenCalledTimes(1));
    expect(decideAskProposal).toHaveBeenCalledWith('thread-a', 'p1', 'approve', proposal.fingerprint);
    expect(screen.queryByRole('button', { name: 'Approve' })).toBeNull();
  });

  it('titles the card with the summary that names the vessel and keeps the saved arguments untouched', async () => {
    render(<AskActionCards threadId="thread-a" busy={false} t={t} />);
    expect(await screen.findByText('Create mission "Repair item" on vessel Harbor Alpha')).toBeTruthy();
    expect(screen.getByText('create_mission')).toBeTruthy();
    expect(screen.getByText(proposal.argumentsJson)).toBeTruthy();
  });

  it('falls back to the tool name when a proposal has no summary', async () => {
    vi.mocked(listAskProposals).mockResolvedValue([{ ...proposal, summary: '' }]);
    render(<AskActionCards threadId="thread-a" busy={false} t={t} />);
    expect(await screen.findByText('create_mission')).toBeTruthy();
  });

  it('keeps a refused decision visible and does not retry it', async () => {
    vi.mocked(decideAskProposal).mockResolvedValue({ statusCode: 409, proposal, message: 'Fingerprint changed.' });
    render(<AskActionCards threadId="thread-a" busy={false} t={t} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Approve' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Fingerprint changed.');
    expect(decideAskProposal).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Approve' })).toBeTruthy();
  });

  it('treats a successful informational decision message as status and refreshes both views', async () => {
    vi.mocked(decideAskProposal).mockResolvedValue({ statusCode: 200, proposal: { ...proposal, state: 'Executing' }, message: 'Job accepted.' });
    vi.mocked(listAskProposals).mockResolvedValueOnce([proposal]).mockResolvedValue([{ ...proposal, state: 'Executing' }]);
    render(<AskActionCards threadId="thread-a" busy={false} t={t} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Approve' }));
    expect(await screen.findByText('Job accepted.')).toHaveAttribute('role', 'status');
    await waitFor(() => expect(listAskWorkReports).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(listAskProposals).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it.each(['Executing', 'Uncertain'] as const)('does not offer approval or retry for %s', async (state) => {
    vi.mocked(listAskProposals).mockResolvedValue([{ ...proposal, state }]);
    render(<AskActionCards threadId="thread-a" busy={false} t={t} />);
    expect(await screen.findByRole('status')).toHaveTextContent('will not retry');
    expect(screen.queryByRole('button', { name: 'Approve' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('shows a terminal job status without labelling the outcome unknown', async () => {
    vi.mocked(listAskProposals).mockResolvedValue([]);
    vi.mocked(listAskWorkReports).mockResolvedValue([{
      kind: 'Job', id: 'job1', title: 'Dispatch job', status: 'Succeeded', deliveryState: 'Observed',
      parentMissionId: null, rescueMissionId: null, producedWork: false, delivered: false,
      summary: 'The accepted job completed.', updatedUtc: null,
    }]);
    render(<AskActionCards threadId="thread-a" busy={false} t={t} />);
    expect(await screen.findByText(/Job: Succeeded/)).toBeTruthy();
    expect(screen.queryByText('The job outcome is unknown. Armada will not retry it automatically.')).toBeNull();
  });

  it('ignores a work response from the previous thread and distinguishes produced work from delivery', async () => {
    let release: ((value: never[]) => void) | undefined;
    vi.mocked(listAskProposals).mockResolvedValue([]);
    vi.mocked(listAskWorkReports).mockImplementation((id: string) => id === 'thread-a'
      ? new Promise((resolve) => { release = resolve as (value: never[]) => void; })
      : Promise.resolve([{
        kind: 'Mission', id: 'm2', title: 'Rescue', status: 'Completed', deliveryState: 'LandingFailed',
        parentMissionId: 'm1', rescueMissionId: 'm2', producedWork: true, delivered: false,
        summary: 'Changes exist in the dock.', updatedUtc: null,
      }]));
    const view = render(<AskActionCards threadId="thread-a" busy={false} t={t} />);
    view.rerender(<AskActionCards threadId="thread-b" busy={false} t={t} />);
    expect(await screen.findByText('Work was produced; delivery is not confirmed.')).toBeTruthy();
    release?.([]);
    await waitFor(() => expect(screen.getByText('Rescue')).toBeTruthy());
    expect(screen.getByText('Parent mission: m1')).toBeTruthy();
    expect(screen.getByText('Rescue mission: m2')).toBeTruthy();
    expect(screen.getByText(/LandingFailed/)).toBeTruthy();
  });
});
