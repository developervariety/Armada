import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, expect, test, vi } from 'vitest';
import CheckoutImport from './CheckoutImport';
import type { LocalCheckoutDiscovery, Vessel } from '../../types/models';

const state = vi.hoisted(() => ({ admin: true }));
const api = vi.hoisted(() => ({ discover: vi.fn(), importCheckout: vi.fn() }));
vi.mock('../../context/AuthContext', () => ({ useAuth: () => ({ isAdmin: state.admin }) }));
vi.mock('../../context/LocaleContext', () => ({ useLocale: () => ({
  t: (text: string, params?: Record<string, string | number | null | undefined>) => params
    ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
    : text,
}) }));
vi.mock('../../api/client', () => ({
  getVesselLocalCheckouts: () => api.discover(),
  importVesselCheckout: (data: unknown) => api.importCheckout(data),
}));

const candidate = { checkoutPath: '/srv/checkouts/app', suggestedName: 'app', originUrl: 'https://example.invalid/app.git', defaultBranch: 'main', existingVesselId: null };
const discovery = (overrides: Partial<LocalCheckoutDiscovery> = {}): LocalCheckoutDiscovery => ({ configured: true, roots: ['/srv/checkouts'], candidates: [candidate], errors: [], truncated: false, ...overrides });
const vessel = { id: 'vsl_new', name: 'Imported app' } as Vessel;
const fleets = [{ id: 'flt_1', name: 'Fleet One' }] as never[];

beforeEach(() => {
  state.admin = true;
  api.discover.mockReset(); api.importCheckout.mockReset();
  api.discover.mockResolvedValue(discovery());
  api.importCheckout.mockResolvedValue(vessel);
});

test('hides checkout import from a non-global administrator', () => {
  state.admin = false;
  render(<MemoryRouter><CheckoutImport fleets={[]} onImported={vi.fn()} /></MemoryRouter>);
  expect(screen.queryByRole('button', { name: 'Import checkout' })).not.toBeInTheDocument();
  expect(api.discover).not.toHaveBeenCalled();
});

test('shows unconfigured, partial scan, truncated, and empty discovery states clearly', async () => {
  api.discover.mockResolvedValue(discovery({ configured: false, roots: [], candidates: [], errors: ['root unavailable'], truncated: true }));
  render(<CheckoutImport fleets={[]} onImported={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Import checkout' }));
  expect(await screen.findByText('Checkout roots are not configured on the server.')).toBeInTheDocument();
  expect(screen.getByText('Some checkout roots could not be scanned: root unavailable')).toBeInTheDocument();
  expect(screen.getByText('The checkout list is incomplete. Refresh after narrowing the configured roots.')).toBeInTheDocument();
  api.discover.mockResolvedValueOnce(discovery({ candidates: [], errors: [] }));
  fireEvent.click(screen.getByRole('button', { name: 'Refresh checkouts' }));
  expect(await screen.findByText('No local checkouts were found.')).toBeInTheDocument();
});

test('imports the selected server path with the edited name and optional fleet, then reports the vessel', async () => {
  const onImported = vi.fn();
  let finishImport!: (created: Vessel) => void;
  api.importCheckout.mockReturnValueOnce(new Promise<Vessel>((resolve) => { finishImport = resolve; }));
  render(<CheckoutImport fleets={fleets} onImported={onImported} />);
  fireEvent.click(screen.getByRole('button', { name: 'Import checkout' }));
  const dialog = await screen.findByRole('dialog');
  fireEvent.change(await within(dialog).findByRole('combobox', { name: 'Checkout' }), { target: { value: candidate.checkoutPath } });
  fireEvent.change(within(dialog).getByLabelText('Vessel name'), { target: { value: 'Imported app' } });
  fireEvent.change(within(dialog).getByLabelText('Fleet (optional)'), { target: { value: 'flt_1' } });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Import checkout' }));
  expect(within(dialog).getByRole('combobox', { name: 'Checkout' })).toBeDisabled();
  expect(within(dialog).getByLabelText('Vessel name')).toBeDisabled();
  expect(within(dialog).getByLabelText('Fleet (optional)')).toBeDisabled();
  await waitFor(() => expect(api.importCheckout).toHaveBeenCalledWith({ checkoutPath: '/srv/checkouts/app', name: 'Imported app', fleetId: 'flt_1' }));
  finishImport(vessel);
  await waitFor(() => expect(onImported).toHaveBeenCalledWith(vessel));
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
});

test('does not import an already registered checkout and links to its vessel', async () => {
  api.discover.mockResolvedValue(discovery({ candidates: [{ ...candidate, existingVesselId: 'vsl_existing' }] }));
  render(<MemoryRouter><CheckoutImport fleets={[]} onImported={vi.fn()} /></MemoryRouter>);
  fireEvent.click(screen.getByRole('button', { name: 'Import checkout' }));
  const dialog = await screen.findByRole('dialog');
  fireEvent.change(await within(dialog).findByRole('combobox', { name: 'Checkout' }), { target: { value: candidate.checkoutPath } });
  expect(within(dialog).getByRole('link', { name: 'Open vessel' })).toHaveAttribute('href', '/vessels/vsl_existing');
  expect(within(dialog).getByRole('button', { name: 'Import checkout' })).toBeDisabled();
  expect(api.importCheckout).not.toHaveBeenCalled();
});

test('keeps the selection readable and reports import failures without calling the success handler', async () => {
  const onImported = vi.fn();
  api.importCheckout.mockRejectedValueOnce(new Error('registration failed'));
  render(<CheckoutImport fleets={[]} onImported={onImported} />);
  fireEvent.click(screen.getByRole('button', { name: 'Import checkout' }));
  const dialog = await screen.findByRole('dialog');
  fireEvent.change(await within(dialog).findByRole('combobox', { name: 'Checkout' }), { target: { value: candidate.checkoutPath } });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Import checkout' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('registration failed');
  expect(within(dialog).getByText('/srv/checkouts/app')).toBeInTheDocument();
  expect(onImported).not.toHaveBeenCalled();
});

test('ignores an earlier discovery response after close and reopen', async () => {
  let resolveFirst!: (result: LocalCheckoutDiscovery) => void;
  api.discover.mockReturnValueOnce(new Promise<LocalCheckoutDiscovery>((resolve) => { resolveFirst = resolve; }));
  api.discover.mockResolvedValueOnce(discovery({ candidates: [] }));
  render(<CheckoutImport fleets={[]} onImported={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Import checkout' }));
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  fireEvent.click(screen.getByRole('button', { name: 'Import checkout' }));
  expect(await screen.findByText('No local checkouts were found.')).toBeInTheDocument();
  resolveFirst(discovery());
  await waitFor(() => expect(screen.queryByText('app — /srv/checkouts/app')).not.toBeInTheDocument());
});

test('shows a discovery failure and allows a manual retry', async () => {
  api.discover.mockRejectedValueOnce(new Error('server scan failed'));
  render(<CheckoutImport fleets={[]} onImported={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Import checkout' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('server scan failed');
  fireEvent.click(screen.getByRole('button', { name: 'Refresh checkouts' }));
  expect(await screen.findByRole('combobox', { name: 'Checkout' })).toBeInTheDocument();
});
