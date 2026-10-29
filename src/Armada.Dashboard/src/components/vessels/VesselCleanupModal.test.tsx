import { act, fireEvent, render, screen, within } from '@testing-library/react';
import VesselCleanupModal from './VesselCleanupModal';
import { getVesselCleanupInventory, runVesselCleanup, type VesselCleanupInventory, type VesselCleanupResource } from '../../api/client';
import { translateTemplate } from '../../i18n/runtime';

vi.mock('../../api/client', () => ({
  getVesselCleanupInventory: vi.fn(),
  runVesselCleanup: vi.fn(),
}));
vi.mock('../../context/LocaleContext', () => ({
  useLocale: () => ({ t: (text: string, params?: Record<string, string | number>) => translateTemplate('en', text, null, params) }),
}));

function resource(overrides: Partial<VesselCleanupResource>): VesselCleanupResource {
  return {
    key: 'dock:dck_1',
    resourceType: 'Dock',
    disposition: 'Safe',
    reason: 'Inactive dock; worktree is clean.',
    identity: 'dck_1',
    path: '/docks/project/dck_1',
    branchName: 'armada/c/one',
    repositoryPath: '/repos/project.git',
    harborId: null,
    dockId: 'dck_1',
    missionId: 'msn_1',
    missionStatus: 'Failed',
    voyageId: 'vyg_1',
    captainId: null,
    sizeBytes: 2048,
    ...overrides,
  };
}

function inventory(resources: VesselCleanupResource[]): VesselCleanupInventory {
  return {
    vesselId: 'vsl_1',
    vesselName: 'project',
    scannedUtc: '2026-10-28T00:00:00Z',
    resources,
    safeCount: resources.filter(r => r.disposition === 'Safe').length,
    inUseCount: resources.filter(r => r.disposition === 'InUse').length,
    unknownCount: resources.filter(r => r.disposition === 'Unknown').length,
    safeMeasuredBytes: resources.filter(r => r.disposition === 'Safe').reduce((s, r) => s + (r.sizeBytes ?? 0), 0),
    safeUnmeasuredCount: 0,
  };
}

const safeDock = resource({});
const safeBranch = resource({ key: 'branch:admiral|/repos/project.git|armada/c/two', resourceType: 'Branch', identity: 'armada/c/two', branchName: 'armada/c/two', path: null, dockId: null, missionId: null, missionStatus: null, voyageId: null, sizeBytes: null, reason: 'Fully merged into main.' });
const busyDock = resource({ key: 'dock:dck_2', identity: 'dck_2', disposition: 'InUse', reason: 'Mission msn_2 is InProgress.', missionStatus: 'InProgress', captainId: 'cpt_1' });
const harborDock = resource({ key: 'dock:dck_3', identity: 'dck_3', disposition: 'Unknown', reason: 'Harbor hbr_1 is not connected; the worktree cannot be inspected.', harborId: 'hbr_1', sizeBytes: null });

async function renderModal() {
  await act(async () => {
    render(<VesselCleanupModal vesselId="vsl_1" vesselName="project" open onClose={vi.fn()} />);
  });
}

function group(name: string): HTMLElement {
  return screen.getByRole('region', { name });
}

describe('VesselCleanupModal', () => {
  beforeEach(() => {
    vi.mocked(getVesselCleanupInventory).mockReset();
    vi.mocked(runVesselCleanup).mockReset();
  });

  it('scans on open and groups resources as safe, in use and unknown with reasons, owners and sizes', async () => {
    vi.mocked(getVesselCleanupInventory).mockResolvedValue(inventory([safeDock, safeBranch, busyDock, harborDock]));
    await renderModal();

    expect(getVesselCleanupInventory).toHaveBeenCalledWith('vsl_1');
    expect(runVesselCleanup).not.toHaveBeenCalled();

    const safe = group('Safe to clean up');
    expect(within(safe).getByText('dck_1')).toBeInTheDocument();
    expect(within(safe).getByText('armada/c/two')).toBeInTheDocument();
    expect(within(safe).getByText('Inactive dock; worktree is clean.')).toBeInTheDocument();
    expect(within(safe).getByText('2.0 KB')).toBeInTheDocument();
    expect(within(safe).getByText('Mission msn_1 (Failed)')).toBeInTheDocument();
    expect(within(safe).getByText('Voyage vyg_1')).toBeInTheDocument();

    const inUse = group('Still in use');
    expect(within(inUse).getByText('Mission msn_2 is InProgress.')).toBeInTheDocument();
    expect(within(inUse).getByRole('checkbox', { name: 'Select dck_2' })).toBeDisabled();

    const unknown = group('Unknown disposition');
    expect(within(unknown).getByText(/Harbor hbr_1 is not connected/)).toBeInTheDocument();
    expect(within(unknown).getByText('Harbor hbr_1')).toBeInTheDocument();
    expect(within(unknown).getByRole('checkbox', { name: 'Select dck_3' })).toBeDisabled();
    expect(within(unknown).getByTitle('Size could not be measured.')).toBeInTheDocument();

    expect(screen.getByRole('button', { name: 'Clean up selected (0)' })).toBeDisabled();
  });

  it('shows the loading state while scanning', async () => {
    vi.mocked(getVesselCleanupInventory).mockReturnValue(new Promise(() => {}));
    await renderModal();
    expect(screen.getByText('Scanning vessel resources...')).toBeInTheDocument();
  });

  it('shows the empty state when there is nothing to inventory', async () => {
    vi.mocked(getVesselCleanupInventory).mockResolvedValue(inventory([]));
    await renderModal();
    expect(screen.getByText(/Nothing to clean up/)).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Safe to clean up' })).toBeNull();
  });

  it('shows a scan error with a retry that rescans', async () => {
    vi.mocked(getVesselCleanupInventory).mockRejectedValueOnce(new Error('Vessel not found')).mockResolvedValueOnce(inventory([safeDock]));
    await renderModal();
    expect(screen.getByRole('alert')).toHaveTextContent('Vessel not found');
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Retry' })); });
    expect(getVesselCleanupInventory).toHaveBeenCalledTimes(2);
    expect(within(group('Safe to clean up')).getByText('dck_1')).toBeInTheDocument();
  });

  it('cleans up only the selected safe resources after confirmation and shows per-item results with the refreshed inventory', async () => {
    vi.mocked(getVesselCleanupInventory).mockResolvedValue(inventory([safeDock, safeBranch, busyDock]));
    vi.mocked(runVesselCleanup).mockResolvedValue({
      results: [
        { key: safeDock.key, resourceType: 'Dock', identity: 'dck_1', status: 'Deleted', message: 'Removed.' },
        { key: safeBranch.key, resourceType: 'Branch', identity: 'armada/c/two', status: 'Failed', message: 'Branch armada/c/two still exists.' },
      ],
      deleted: 1,
      skipped: 0,
      failed: 1,
      inventory: inventory([safeBranch, busyDock]),
    });
    await renderModal();

    fireEvent.click(screen.getByRole('checkbox', { name: 'Select all safe resources' }));
    const cleanButton = screen.getByRole('button', { name: 'Clean up selected (2)' });
    expect(cleanButton).toBeEnabled();

    fireEvent.click(cleanButton);
    expect(runVesselCleanup).not.toHaveBeenCalled();
    const dialog = screen.getByRole('alertdialog');
    expect(dialog).toHaveTextContent('Remove 2 selected resources (2.0 KB measured) from project?');

    await act(async () => { fireEvent.click(within(dialog).getByRole('button', { name: 'Clean up' })); });
    expect(runVesselCleanup).toHaveBeenCalledWith('vsl_1', [safeDock.key, safeBranch.key]);

    const results = screen.getByLabelText('Cleanup results');
    expect(results).toHaveTextContent('1 removed, 0 skipped, 1 failed.');
    expect(within(results).getByText('Branch armada/c/two still exists.', { exact: false })).toBeInTheDocument();
    expect(results.querySelector('[data-status="Failed"]')).not.toBeNull();

    // Refreshed inventory: the removed dock is gone, the failed branch stays selectable for another attempt.
    expect(within(group('Safe to clean up')).queryByText('dck_1')).toBeNull();
    expect(within(group('Safe to clean up')).getByRole('checkbox', { name: 'Select armada/c/two' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Clean up selected (0)' })).toBeDisabled();
  });

  it('cancelling the confirmation deletes nothing', async () => {
    vi.mocked(getVesselCleanupInventory).mockResolvedValue(inventory([safeDock]));
    await renderModal();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select dck_1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Clean up selected (1)' }));
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('alertdialog')).toBeNull();
    expect(runVesselCleanup).not.toHaveBeenCalled();
  });

  it('reports a failed cleanup request and keeps the inventory', async () => {
    vi.mocked(getVesselCleanupInventory).mockResolvedValue(inventory([safeDock]));
    vi.mocked(runVesselCleanup).mockRejectedValue(new Error('Forbidden'));
    await renderModal();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select dck_1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Clean up selected (1)' }));
    await act(async () => { fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Clean up' })); });
    expect(screen.getByRole('alert')).toHaveTextContent('Cleanup failed: Forbidden');
    expect(within(group('Safe to clean up')).getByText('dck_1')).toBeInTheDocument();
  });
});
