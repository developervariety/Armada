import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Voyages from './Voyages';
import { listVoyages, purgeVoyage } from '../api/client';
import { translateTemplate } from '../i18n/runtime';
import type { Voyage } from '../types/models';

const pushToast = vi.hoisted(() => vi.fn());

vi.mock('../api/client', () => ({
  listVoyages: vi.fn(),
  cancelVoyage: vi.fn(),
  purgeVoyage: vi.fn(),
  getVoyageStatus: vi.fn(),
}));

const localeValue = {
  t: (text: string, params?: Record<string, string | number | null | undefined>) => translateTemplate('en', text, null, params),
  locale: 'en',
  formatDateTime: (v: string | null | undefined) => v ?? '',
  formatRelativeTime: (v: string | null | undefined) => v ?? '',
};
vi.mock('../context/LocaleContext', () => ({ useLocale: () => localeValue }));
vi.mock('../context/NotificationContext', () => ({ useNotifications: () => ({ pushToast }) }));
vi.mock('../context/AuthContext', () => ({ useAuth: () => ({ isTenantAdmin: true, isAdmin: true, user: { id: 'usr_1' } }) }));
vi.mock('../components/shared/UserScopeFilter', () => ({ default: () => null }));

function voyage(id: string, title: string, landingMode: string | null): Voyage {
  return {
    id, tenantId: 'default', title, description: null, status: 'InProgress',
    createdUtc: '2026-10-04T00:00:00Z', completedUtc: null, lastUpdateUtc: '2026-10-04T00:00:00Z',
    autoPush: true, autoCreatePullRequests: true, autoMergePullRequests: null, landingMode,
  };
}

describe('Voyages landing mode column', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(purgeVoyage).mockReset().mockResolvedValue(undefined);
    vi.mocked(listVoyages).mockResolvedValue({
      success: true, pageNumber: 1, pageSize: 25, totalPages: 1, totalRecords: 2, totalMs: 1,
      objects: [voyage('vyg_1', 'Gateway hardening', 'MergeAndPush'), voyage('vyg_2', 'Docs sweep', null)],
    } as never);
  });

  it('shows Landing Mode (Default when inherited) and drops the Auto Push and Auto Create PRs columns', async () => {
    render(<MemoryRouter><Voyages /></MemoryRouter>);
    const row = (await screen.findByText('Gateway hardening')).closest('tr')!;
    expect(within(row).getByText('MergeAndPush')).toBeInTheDocument();
    const inherited = screen.getByText('Docs sweep').closest('tr')!;
    expect(within(inherited).getByText('Default')).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Landing Mode' })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Auto Push' })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: 'Auto Create PRs' })).toBeNull();
  });

  async function selectBoth() {
    const first = (await screen.findByText('Gateway hardening')).closest('tr')!;
    const second = screen.getByText('Docs sweep').closest('tr')!;
    fireEvent.click(within(first).getByRole('checkbox'));
    expect(screen.queryByRole('button', { name: /Delete Multiple/ })).not.toBeInTheDocument();
    fireEvent.click(within(second).getByRole('checkbox'));
  }

  it('calls voyage removal Delete in the action menu, confirmation and success message', async () => {
    render(<MemoryRouter><Voyages /></MemoryRouter>);
    const row = (await screen.findByText('Gateway hardening')).closest('tr')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Actions' }));
    expect(screen.queryByRole('menuitem', { name: 'Purge' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Delete' }));
    const confirmation = screen.getByRole('alertdialog', { name: 'Delete Voyage' });
    expect(confirmation).toHaveTextContent('Delete voyage "Gateway hardening"?');
    expect(purgeVoyage).not.toHaveBeenCalled();
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(purgeVoyage).toHaveBeenCalledExactlyOnceWith('vyg_1'));
    expect(pushToast).toHaveBeenCalledWith('warning', 'Voyage "Gateway hardening" deleted.');
  });

  it('shows Delete Multiple for multiple selections and allows cancelling without deleting', async () => {
    render(<MemoryRouter><Voyages /></MemoryRouter>);
    await selectBoth();
    fireEvent.click(screen.getByRole('button', { name: 'Delete Multiple (2)' }));
    const confirmation = screen.getByRole('alertdialog', { name: 'Delete Multiple Voyages' });
    expect(confirmation).toHaveTextContent('Delete 2 selected voyages and all their associated missions?');
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Cancel' }));
    expect(purgeVoyage).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Delete Multiple (2)' })).toBeInTheDocument();
  });

  it('deletes each selected voyage after confirmation, clears selection and reloads', async () => {
    render(<MemoryRouter><Voyages /></MemoryRouter>);
    await selectBoth();
    fireEvent.click(screen.getByRole('button', { name: 'Delete Multiple (2)' }));
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(listVoyages).toHaveBeenCalledTimes(2));
    expect(vi.mocked(purgeVoyage).mock.calls).toEqual([['vyg_1'], ['vyg_2']]);
    expect(pushToast).toHaveBeenCalledWith('success', 'Deleted 2 voyages.');
    expect(screen.queryByRole('button', { name: /Delete Multiple/ })).not.toBeInTheDocument();
  });

  it('retains failed selections and reports partial failure after refreshing', async () => {
    vi.mocked(purgeVoyage).mockRejectedValueOnce(new Error('Active voyage'));
    render(<MemoryRouter><Voyages /></MemoryRouter>);
    await selectBoth();
    fireEvent.click(screen.getByRole('button', { name: 'Delete Multiple (2)' }));
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Delete' }));
    await screen.findByText('Deleted 1 voyages, 1 failed. Failed voyages remain selected.');
    expect(purgeVoyage).toHaveBeenCalledTimes(2);
    expect(pushToast).toHaveBeenCalledWith('warning', 'Deleted 1 voyages. 1 failed.');
    expect(within(screen.getByText('Gateway hardening').closest('tr')!).getByRole('checkbox')).toBeChecked();
    expect(within(screen.getByText('Docs sweep').closest('tr')!).getByRole('checkbox')).not.toBeChecked();
  });

  it('disables bulk actions while deleting and retains all selections when every deletion fails', async () => {
    let rejectFirst!: (error: Error) => void;
    vi.mocked(purgeVoyage).mockImplementationOnce(() => new Promise<void>((_resolve, reject) => { rejectFirst = reject; }))
      .mockRejectedValueOnce(new Error('Active mission'));
    render(<MemoryRouter><Voyages /></MemoryRouter>);
    await selectBoth();
    fireEvent.click(screen.getByRole('button', { name: 'Delete Multiple (2)' }));
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Delete' }));
    expect(screen.getByRole('button', { name: 'Deleting... (2)' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cancel Selected (2)' })).toBeDisabled();
    await act(async () => rejectFirst(new Error('Active voyage')));
    await screen.findByText('Deleted 0 voyages, 2 failed. Failed voyages remain selected.');
    expect(screen.getByRole('button', { name: 'Delete Multiple (2)' })).toBeEnabled();
    expect(pushToast).not.toHaveBeenCalled();
  });

});
