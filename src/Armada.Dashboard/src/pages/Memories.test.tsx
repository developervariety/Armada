import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import MemoriesPage from './Memories';
import { enumerateMemories, createMemory, listVessels } from '../api/client';
import type { Memory } from '../types/models';

const translate = (text: string, params?: Record<string, string | number | null | undefined>) => {
  if (!params) return text;
  return Object.entries(params).reduce(
    (current, [key, value]) => current.split(`{{${key}}}`).join(value == null ? '' : String(value)),
    text,
  );
};
const localeValue = { t: translate, formatDateTime: (v: string | null | undefined) => v ?? '', formatRelativeTime: (v: string | null | undefined) => v ?? '' };
const notifications = { pushToast: vi.fn() };

vi.mock('../api/client', () => ({
  enumerateMemories: vi.fn(),
  createMemory: vi.fn(),
  deleteMemory: vi.fn(),
  listVessels: vi.fn(),
}));
vi.mock('../api/paging', () => ({ listAll: (fn: () => Promise<unknown>) => fn() }));

function LocationProbe() {
  return <div data-testid="location">{useLocation().pathname}</div>;
}

function Memories() {
  return (
    <MemoryRouter initialEntries={['/memories']}>
      <Routes><Route path="*" element={<><MemoriesPage /><LocationProbe /></>} /></Routes>
    </MemoryRouter>
  );
}
vi.mock('../context/AuthContext', () => ({ useAuth: () => ({ isAdmin: true, isTenantAdmin: true, user: { user: { id: 'usr_1', tenantId: 'ten_1' } } }) }));
vi.mock('../context/LocaleContext', () => ({ useLocale: () => localeValue }));
vi.mock('../context/NotificationContext', () => ({ useNotifications: () => notifications }));

const CONTENT = 'The billing service retries webhook deliveries three times with exponential backoff before parking them in the dead-letter queue.';
const VESSEL = 'vsl_0123456789abcdefghijkl';
const memory = {
  id: 'mem_1', scope: 'Tenant', type: 'Semantic', topic: 'billing', summary: null, content: CONTENT, salience: 0.5,
  version: 1, sourceKind: 'Agent', tags: [], vesselId: VESSEL, createdUtc: '2026-10-01T00:00:00Z', lastUpdateUtc: '2026-10-01T00:00:00Z',
} as unknown as Memory;

describe('Memories table', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
    vi.mocked(enumerateMemories).mockResolvedValue({ success: true, pageNumber: 1, pageSize: 25, totalPages: 1, totalRecords: 1, totalMs: 1, objects: [memory] } as never);
    vi.mocked(listVessels).mockResolvedValue({ objects: [] } as never);
  });

  it('shows the summary on one line with the full content in the tooltip, and falls back to the vessel ID when the vessel is unknown', async () => {
    render(<Memories />);
    const text = await screen.findByText(CONTENT);
    expect(text).toHaveClass('truncate-text');
    expect(text.closest('td')).toHaveAttribute('title', CONTENT);
    expect(screen.getByTitle(VESSEL)).toHaveClass('cell-clip');
    expect(within(screen.getByTitle(VESSEL)).getByRole('link', { name: VESSEL })).toBeInTheDocument();
  });

  it('puts Vessel first and shows the vessel name linking to the vessel, with the ID in the tooltip', async () => {
    vi.mocked(listVessels).mockResolvedValue({ objects: [{ id: VESSEL, name: 'billing-api' }] } as never);
    const { container } = render(<Memories />);
    const link = await screen.findByRole('link', { name: 'billing-api' });
    const headers = [...container.querySelectorAll('thead tr:first-child th')].map(th => th.textContent?.trim() ?? '');
    expect(headers[0]).toMatch(/^Vessel/);
    expect(link.closest('td')).toBe(container.querySelector('tbody tr td:first-child'));
    expect(link.closest('[title]')).toHaveAttribute('title', VESSEL);
    expect(screen.queryByText(VESSEL)).toBeNull();
    fireEvent.click(link);
    expect(screen.getByTestId('location')).toHaveTextContent(`/vessels/${VESSEL}`);
  });

  it('shows a dash when the memory has no vessel', async () => {
    vi.mocked(enumerateMemories).mockResolvedValue({ objects: [{ ...memory, vesselId: null, sourceVesselId: null }], totalRecords: 1, totalPages: 1 } as never);
    const { container } = render(<Memories />);
    await screen.findByText(CONTENT);
    expect(container.querySelector('tbody tr td:first-child')).toHaveTextContent(/^-$/);
  });

  it('loads the first page with the default sort (Updated, descending) from the enumerate endpoint', async () => {
    render(<Memories />);
    await screen.findByText(CONTENT);
    expect(enumerateMemories).toHaveBeenCalledTimes(1);
    expect(enumerateMemories).toHaveBeenLastCalledWith({ pageNumber: 1, pageSize: 25, sortBy: 'Updated', order: 'CreatedDescending' });
  });

  it('sends header sorts to the server and renders rows in the order the server returns', async () => {
    const other = { ...memory, id: 'mem_2', topic: 'auth', content: 'Second', vesselId: 'vsl_other', sourceVesselId: null, lastUpdateUtc: '2026-10-02T00:00:00Z' };
    // The server order (zeta first) is kept even after an ascending vessel sort: the page never re-sorts rows.
    vi.mocked(enumerateMemories).mockResolvedValue({ objects: [memory, other], totalRecords: 2, totalPages: 1 } as never);
    vi.mocked(listVessels).mockResolvedValue({ objects: [{ id: VESSEL, name: 'zeta-billing' }, { id: 'vsl_other', name: 'alpha-auth' }] } as never);
    const { container } = render(<Memories />);
    await screen.findByRole('link', { name: 'alpha-auth' });
    const firstVessel = () => container.querySelector('tbody tr td:first-child')?.textContent;
    const header = screen.getByRole('button', { name: /^Vessel/ });
    expect(header.closest('th')).toHaveAttribute('aria-sort', 'none');
    fireEvent.click(header);
    expect(header.closest('th')).toHaveAttribute('aria-sort', 'ascending');
    await vi.waitFor(() => expect(enumerateMemories).toHaveBeenLastCalledWith(expect.objectContaining({ sortBy: 'Vessel', order: 'CreatedAscending', pageNumber: 1 })));
    expect(firstVessel()).toBe('zeta-billing');
    fireEvent.click(header);
    expect(header.closest('th')).toHaveAttribute('aria-sort', 'descending');
    await vi.waitFor(() => expect(enumerateMemories).toHaveBeenLastCalledWith(expect.objectContaining({ sortBy: 'Vessel', order: 'CreatedDescending' })));
    fireEvent.click(screen.getByRole('button', { name: /^Topic/ }));
    await vi.waitFor(() => expect(enumerateMemories).toHaveBeenLastCalledWith(expect.objectContaining({ sortBy: 'Topic', order: 'CreatedAscending' })));
    fireEvent.click(screen.getByRole('button', { name: /^Salience/ }));
    await vi.waitFor(() => expect(enumerateMemories).toHaveBeenLastCalledWith(expect.objectContaining({ sortBy: 'Salience' })));
    expect(container.querySelector('tbody tr td:nth-child(3)')?.textContent).toBe('billing');
  });

  it('sends every column filter, the type filter and the search to the server and does not filter rows locally', async () => {
    const other = { ...memory, id: 'mem_2', topic: 'auth', content: 'Second', vesselId: 'vsl_other', sourceVesselId: null };
    vi.mocked(enumerateMemories).mockResolvedValue({ objects: [memory, other], totalRecords: 2, totalPages: 1 } as never);
    vi.mocked(listVessels).mockResolvedValue({ objects: [{ id: VESSEL, name: 'zeta-billing' }, { id: 'vsl_other', name: 'alpha-auth' }] } as never);
    render(<Memories />);
    await screen.findByRole('link', { name: 'alpha-auth' });
    fireEvent.change(screen.getByLabelText('Filter by vessel'), { target: { value: 'ALPHA' } });
    await vi.waitFor(() => expect(enumerateMemories).toHaveBeenLastCalledWith(expect.objectContaining({ vesselName: 'ALPHA', pageNumber: 1 })));
    // Rows the server returned stay visible: no client-side filtering.
    expect(screen.getByText(CONTENT)).toBeInTheDocument();
    expect(screen.getByText('Second')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Filter by vessel'), { target: { value: '' } });
    fireEvent.change(screen.getByLabelText('Filter by topic'), { target: { value: 'bill' } });
    fireEvent.change(screen.getByLabelText('Filter by summary'), { target: { value: 'webhook' } });
    fireEvent.change(screen.getByLabelText('Minimum salience'), { target: { value: '0.4' } });
    fireEvent.change(screen.getByLabelText('Filter by visibility'), { target: { value: 'TenantWide' } });
    fireEvent.change(screen.getByLabelText('Filter by type'), { target: { value: 'Procedural' } });
    fireEvent.change(screen.getByPlaceholderText('Search content, topic, tags...'), { target: { value: 'retries' } });
    await vi.waitFor(() => expect(enumerateMemories).toHaveBeenLastCalledWith({
      pageNumber: 1, pageSize: 25, sortBy: 'Updated', order: 'CreatedDescending',
      topicContains: 'bill', summaryContains: 'webhook', minSalience: 0.4, scope: 'TenantWide', type: 'Procedural', search: 'retries',
    }));
    const calls = vi.mocked(enumerateMemories).mock.calls;
    const lastQuery = calls[calls.length - 1]?.[0];
    expect(lastQuery).not.toHaveProperty('vesselName');
  });

  it('requests the next page from the server', async () => {
    vi.mocked(enumerateMemories).mockResolvedValue({ objects: [memory], pageNumber: 1, pageSize: 25, totalRecords: 60, totalPages: 3 } as never);
    render(<Memories />);
    await screen.findByText(CONTENT);
    fireEvent.click(screen.getByRole('button', { name: /next/i }));
    await vi.waitFor(() => expect(enumerateMemories).toHaveBeenLastCalledWith(expect.objectContaining({ pageNumber: 2, pageSize: 25 })));
  });

  it('puts refresh in the toolbar and locks Summary in the column chooser', async () => {
    const { container } = render(<Memories />);
    await screen.findByText(CONTENT);
    const bar = container.querySelector('.data-table .pagination-bar') as HTMLElement;
    expect(within(bar).getByTitle('Refresh')).toBeInTheDocument();
    expect(container.querySelector('.page-header .refresh-btn')).toBeNull();
    await act(async () => { fireEvent.click(within(bar).getByRole('button', { name: /^Columns/ })); });
    const menu = screen.getByRole('menu', { name: 'Choose visible columns' });
    expect(within(menu).getByRole('menuitemcheckbox', { name: /^Summary/ })).toHaveAttribute('aria-disabled', 'true');
    expect(within(menu).getByRole('menuitemcheckbox', { name: /^Salience/ })).not.toHaveAttribute('aria-disabled');
  });
});


it('duplicates an editable memory draft without reusing its upsert key or identity', async () => {
  vi.mocked(enumerateMemories).mockResolvedValue({ objects: [{ ...memory, key: 'canonical-key' }], totalRecords: 1, totalPages: 1 } as never);
  vi.mocked(createMemory).mockResolvedValue(memory);
  vi.mocked(listVessels).mockResolvedValue({ objects: [] } as never);
  render(<Memories />);
  await screen.findByText(CONTENT);
  fireEvent.click(screen.getByRole('button', { name: 'Actions' }));
  fireEvent.click(screen.getByRole('menuitem', { name: 'Duplicate' }));
  const dialog = screen.getByRole('dialog', { name: 'Duplicate Memory' });
  expect(createMemory).not.toHaveBeenCalled();
  fireEvent.change(within(dialog).getByLabelText('Content'), { target: { value: 'A new fact' } });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save Copy' }));
  await vi.waitFor(() => expect(createMemory).toHaveBeenCalledWith(expect.objectContaining({ content: 'A new fact', type: 'Semantic', sourceKind: 'Manual' })));
  const payload = vi.mocked(createMemory).mock.calls[0][0];
  expect(payload).not.toHaveProperty('id');
  expect(payload).not.toHaveProperty('key');
  expect(payload).not.toHaveProperty('version');
});
