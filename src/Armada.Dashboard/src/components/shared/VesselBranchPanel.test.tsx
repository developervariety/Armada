import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import VesselBranchPanel from './VesselBranchPanel';
import type { BranchListResponse, BranchWriteControls, BranchWriteResult } from '../../types/models';

const api = vi.hoisted(() => ({
  getVesselBranches: vi.fn(),
  pushVesselBranch: vi.fn(),
  mergeVesselBranch: vi.fn(),
}));

vi.mock('../../api/client', () => api);

vi.mock('../../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      params
        ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
        : text,
  };
  return { useLocale: () => locale };
});

function listing(controls: Partial<BranchWriteControls>): BranchListResponse {
  return {
    vesselId: 'vsl_example',
    defaultBranch: 'main',
    source: 'LocalPath',
    headState: 'bare',
    headRef: 'main',
    branchCount: 2,
    error: null,
    branches: [
      { name: 'main', isCurrent: true, isDefault: true, commitHash: 'aaa1111', commitSubject: 'Base', commitDate: null, ahead: 0, behind: 0, divergenceError: null },
      { name: 'feature/work', isCurrent: false, isDefault: false, commitHash: 'bbb2222', commitSubject: 'Feature', commitDate: null, ahead: 1, behind: 0, divergenceError: null },
    ],
    writeControls: {
      mergeAvailable: true,
      mergeUnavailableReason: null,
      pushAvailable: true,
      pushUnavailableReason: null,
      remote: 'origin',
      ...controls,
    },
  };
}

function writeResult(overrides: Partial<BranchWriteResult>): BranchWriteResult {
  return {
    succeeded: true,
    operation: 'merge',
    reason: null,
    message: 'Merged.',
    vesselId: 'vsl_example',
    sourceRef: 'feature/work',
    targetRef: 'main',
    remote: null,
    strategy: 'FastForward',
    sourceCommit: 'bbb2222',
    previousTargetCommit: 'aaa1111',
    targetCommit: 'bbb2222',
    workingCheckoutSync: 'fast_forwarded',
    ...overrides,
  };
}

describe('VesselBranchPanel', () => {
  beforeEach(() => {
    api.getVesselBranches.mockReset();
    api.pushVesselBranch.mockReset();
    api.mergeVesselBranch.mockReset();
  });

  it('shows no write controls when the server reports them unavailable', async () => {
    api.getVesselBranches.mockResolvedValue(listing({ mergeAvailable: false, pushAvailable: false, mergeUnavailableReason: 'administrator_required', pushUnavailableReason: 'administrator_required' }));
    render(<VesselBranchPanel vesselId="vsl_example" />);

    expect(await screen.findByText('feature/work')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Push' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Merge' })).toBeNull();
    expect(screen.getByText('Branch write controls are unavailable: administrator_required')).toBeInTheDocument();
  });

  it('shows only the control the server allows and never offers merging the default branch', async () => {
    api.getVesselBranches.mockResolvedValue(listing({ pushAvailable: false, pushUnavailableReason: 'remote_mismatch' }));
    render(<VesselBranchPanel vesselId="vsl_example" />);

    expect(await screen.findByText('feature/work')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Push' })).toBeNull();
    expect(screen.getAllByRole('button', { name: 'Merge' })).toHaveLength(1);
  });

  it('confirms a merge by stating source, target and strategy, then sends exactly that request', async () => {
    api.getVesselBranches.mockResolvedValue(listing({}));
    api.mergeVesselBranch.mockResolvedValue(writeResult({ message: "Merged 'feature/work' into 'main' (MergeCommit). Nothing was pushed." }));
    render(<VesselBranchPanel vesselId="vsl_example" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Merge' }));
    const dialog = screen.getByRole('dialog', { name: 'Confirm merge' });
    fireEvent.change(within(dialog).getByLabelText('Strategy'), { target: { value: 'MergeCommit' } });
    expect(within(dialog).getByTestId('branch-write-summary').textContent).toBe(
      'Merge feature/work into main in the landing repository using MergeCommit. Nothing is pushed. The working checkout is fast-forwarded only when it is on main.',
    );
    expect(api.mergeVesselBranch).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm merge' }));
    await waitFor(() => expect(api.mergeVesselBranch).toHaveBeenCalledWith('vsl_example', { sourceRef: 'feature/work', targetRef: 'main', strategy: 'MergeCommit' }));
    expect(await screen.findByRole('status')).toHaveTextContent("Merged 'feature/work' into 'main' (MergeCommit). Nothing was pushed.");
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(api.getVesselBranches).toHaveBeenCalledTimes(2);
  });

  it('names the remote in the push confirmation, shows the busy state and keeps a refusal visible', async () => {
    api.getVesselBranches.mockResolvedValue(listing({}));
    let reject: (reason: Error) => void = () => {};
    api.pushVesselBranch.mockReturnValue(new Promise((_, rejectPush) => { reject = rejectPush; }));
    render(<VesselBranchPanel vesselId="vsl_example" />);

    await screen.findByText('feature/work');
    fireEvent.click(screen.getAllByRole('button', { name: 'Push' })[1]);
    const dialog = screen.getByRole('dialog', { name: 'Confirm push' });
    fireEvent.change(within(dialog).getByLabelText('Target'), { target: { value: 'main' } });
    expect(within(dialog).getByTestId('branch-write-summary').textContent).toBe(
      'Push feature/work to origin/main. This is not a force push; the remote refuses an update that would rewrite its history.',
    );

    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm push' }));
    expect(await within(dialog).findByRole('button', { name: 'Pushing...' })).toBeDisabled();
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(api.pushVesselBranch).toHaveBeenCalledWith('vsl_example', { sourceRef: 'feature/work', targetRef: 'main', remote: 'origin' });

    await act(async () => { reject(new Error('origin/main is not an ancestor of \'feature/work\'. The push would rewrite remote history.')); });
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('The push would rewrite remote history.');
    expect(screen.getByRole('dialog', { name: 'Confirm push' })).toBeInTheDocument();
    expect(api.getVesselBranches).toHaveBeenCalledTimes(1);
  });
});
