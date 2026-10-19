import { act, fireEvent, render, screen, within } from '@testing-library/react';
import Memories from './Memories';
import { listMemories, createMemory } from '../api/client';
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
  listMemories: vi.fn(),
  createMemory: vi.fn(),
  deleteMemory: vi.fn(),
}));
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
    vi.mocked(listMemories).mockResolvedValue({ success: true, pageNumber: 1, pageSize: 25, totalPages: 1, totalRecords: 1, totalMs: 1, objects: [memory] } as never);
  });

  it('shows the summary on one line with the full content in the tooltip, and the vessel ID clipped', async () => {
    render(<Memories />);
    const text = await screen.findByText(CONTENT);
    expect(text).toHaveClass('truncate-text');
    expect(text.closest('td')).toHaveAttribute('title', CONTENT);
    expect(screen.getByTitle(VESSEL)).toHaveClass('cell-clip');
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
  vi.mocked(listMemories).mockResolvedValue({ objects: [{ ...memory, key: 'canonical-key' }], totalRecords: 1, totalPages: 1 } as never);
  vi.mocked(createMemory).mockResolvedValue(memory);
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
