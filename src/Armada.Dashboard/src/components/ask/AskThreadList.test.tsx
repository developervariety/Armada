import { fireEvent, render, screen } from '@testing-library/react';
import AskThreadList from './AskThreadList';
import { translateTemplate } from '../../i18n/runtime';
import type { AskThread } from '../../types/models';

const localeValue = {
  t: (text: string, params?: Record<string, string | number | null | undefined>) => translateTemplate('en', text, null, params),
  locale: 'en',
  formatDateTime: (v: string | null | undefined) => v ?? '',
  formatRelativeTime: (v: string | null | undefined) => (v ? 'recently' : ''),
};
vi.mock('../../context/LocaleContext', () => ({ useLocale: () => localeValue }));

function thread(id: string, title: string, overrides: Partial<AskThread> = {}): AskThread {
  return {
    id,
    title,
    captainId: null,
    autoApprove: false,
    pinned: false,
    archived: false,
    unreadCount: 0,
    lastMessageUtc: '2026-10-08T12:00:00Z',
    ...overrides,
  };
}

type ThreadListProps = Parameters<typeof AskThreadList>[0];

function renderList(overrides: Partial<ThreadListProps> = {}) {
  const handlers = {
    onSelect: vi.fn(),
    onToggleSelected: vi.fn(),
    onSelectAll: vi.fn(),
    onClearSelection: vi.fn(),
    onDeleteSelected: vi.fn(),
    onDelete: vi.fn(),
    onNew: vi.fn(),
    onMarkRead: vi.fn(),
    onMarkUnread: vi.fn(),
  };
  const props: ThreadListProps = {
    threads: [thread('ath_1', 'Alpha'), thread('ath_2', 'Beta', { pinned: true })],
    selectedId: null,
    activity: {},
    loading: false,
    error: null,
    search: '',
    onSearchChange: () => undefined,
    includeArchived: false,
    onIncludeArchivedChange: () => undefined,
    hasMore: false,
    onLoadMore: () => undefined,
    onRetry: () => undefined,
    onRename: () => undefined,
    onTogglePin: () => undefined,
    onSummarize: () => undefined,
    onToggleArchive: () => undefined,
    selectedIds: [],
    ...handlers,
    ...overrides,
  };
  render(<AskThreadList {...props} />);
  return handlers;
}

describe('AskThreadList multi-select', () => {
  it('ticking a conversation selects it without opening it', () => {
    const handlers = renderList();
    const box = screen.getByRole('checkbox', { name: 'Select Alpha' });
    expect(box).not.toBeChecked();
    fireEvent.click(box);
    expect(handlers.onToggleSelected).toHaveBeenCalledTimes(1);
    expect(handlers.onToggleSelected.mock.calls[0][0].id).toBe('ath_1');
    expect(handlers.onToggleSelected.mock.calls[0][1]).toBe(true);
    expect(handlers.onSelect).not.toHaveBeenCalled();
  });

  it('shows no bulk delete control until a conversation is selected', () => {
    renderList();
    expect(screen.queryByRole('button', { name: 'Delete (1)' })).toBeNull();
    expect(screen.queryByText('1 selected')).toBeNull();
    expect(screen.getByRole('checkbox', { name: 'Select all' })).not.toBeChecked();
  });

  it('shows the selected count, a bulk delete button, and a clear control when threads are selected', () => {
    const handlers = renderList({ selectedIds: ['ath_1', 'ath_2'] });
    expect(screen.getByText('2 selected')).toBeTruthy();
    expect(screen.getByRole('checkbox', { name: 'Select Alpha' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Select all' })).toBeChecked();
    fireEvent.click(screen.getByRole('button', { name: 'Delete (2)' }));
    expect(handlers.onDeleteSelected).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Clear selection' }));
    expect(handlers.onClearSelection).toHaveBeenCalledTimes(1);
  });

  it('select all ticks every visible thread, and unticking it clears the selection', () => {
    const unchecked = renderList();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select all' }));
    expect(unchecked.onSelectAll).toHaveBeenCalledTimes(1);
    expect(unchecked.onSelectAll.mock.calls[0][0].map((th: AskThread) => th.id)).toEqual(['ath_1', 'ath_2']);
    expect(unchecked.onClearSelection).not.toHaveBeenCalled();
  });

  it('unticking select all clears the selection', () => {
    const checked = renderList({ selectedIds: ['ath_1', 'ath_2'] });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select all' }));
    expect(checked.onClearSelection).toHaveBeenCalledTimes(1);
    expect(checked.onSelectAll).not.toHaveBeenCalled();
  });

  it('the New conversation button is compact and still named for screen readers', () => {
    renderList();
    const button = screen.getByRole('button', { name: 'New conversation' });
    expect(button.textContent?.trim()).toBe('+ New');
  });
});

describe('AskThreadList read state menu', () => {
  function openMenu(title: string) {
    fireEvent.click(screen.getByRole('button', { name: `Actions for ${title}` }));
  }

  it('offers Mark read for an unread conversation and calls the handler', () => {
    const handlers = renderList({ threads: [thread('ath_1', 'Alpha', { unreadCount: 3 })] });
    openMenu('Alpha');
    expect(screen.queryByRole('menuitem', { name: 'Mark unread' })).toBeNull();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Mark read' }));
    expect(handlers.onMarkRead).toHaveBeenCalledTimes(1);
    expect(handlers.onMarkRead.mock.calls[0][0].id).toBe('ath_1');
    expect(handlers.onMarkUnread).not.toHaveBeenCalled();
  });

  it('offers Mark unread for a read conversation and calls the handler', () => {
    const handlers = renderList({ threads: [thread('ath_1', 'Alpha')] });
    openMenu('Alpha');
    expect(screen.queryByRole('menuitem', { name: 'Mark read' })).toBeNull();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Mark unread' }));
    expect(handlers.onMarkUnread).toHaveBeenCalledTimes(1);
    expect(handlers.onMarkUnread.mock.calls[0][0].id).toBe('ath_1');
  });

  it('the open conversation shows as read, so it offers Mark unread even with a stale count', () => {
    renderList({ threads: [thread('ath_1', 'Alpha', { unreadCount: 2 })], selectedId: 'ath_1' });
    openMenu('Alpha');
    expect(screen.getByRole('menuitem', { name: 'Mark unread' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Mark read' })).toBeNull();
  });
});
