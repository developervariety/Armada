import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import type { AskThread } from '../../types/models';
import { useLocale } from '../../context/LocaleContext';
import ActionMenu, { type ActionMenuItem } from '../shared/ActionMenu';
import { isThreadReplying, isThreadWorking, type ThreadActivityMap } from '../../lib/askThreads';

export interface AskThreadListProps {
  threads: AskThread[];
  selectedId: string | null;
  activity: ThreadActivityMap;
  loading: boolean;
  error: string | null;
  search: string;
  onSearchChange: (value: string) => void;
  includeArchived: boolean;
  onIncludeArchivedChange: (value: boolean) => void;
  hasMore: boolean;
  onLoadMore: () => void;
  onRetry: () => void;
  onRefresh?: () => void;
  onSelect: (thread: AskThread) => void;
  onNew: () => void;
  onRename: (thread: AskThread, title: string) => void;
  onTogglePin: (thread: AskThread) => void;
  onSummarize: (thread: AskThread) => void;
  onToggleArchive: (thread: AskThread) => void;
  onMarkRead: (thread: AskThread) => void;
  onMarkUnread: (thread: AskThread) => void;
  onDelete: (thread: AskThread) => void;
  /** IDs of the conversations ticked for a bulk delete (kept by the page). */
  selectedIds: string[];
  onToggleSelected: (thread: AskThread, selected: boolean) => void;
  onSelectAll: (threads: AskThread[]) => void;
  onClearSelection: () => void;
  onDeleteSelected: () => void;
  /** Shown in the drawer on narrow screens. */
  onClose?: () => void;
}

/**
 * The conversation list: search (server-side), New conversation, pinned threads first, unread badges, a live
 * "working" dot for threads whose tracked work is still running, and a per-row menu. Arrow keys move between
 * rows; Enter opens one. Each row also carries a selection checkbox, and selecting one or more reveals a bulk
 * delete control.
 */
export default function AskThreadList(props: AskThreadListProps) {
  const {
    threads, selectedId, activity, loading, error, search, onSearchChange, includeArchived, onIncludeArchivedChange,
    hasMore, onLoadMore, onRetry, onRefresh, onSelect, onNew, onRename, onTogglePin, onSummarize, onToggleArchive, onMarkRead, onMarkUnread, onDelete, onClose,
    selectedIds, onToggleSelected, onSelectAll, onClearSelection, onDeleteSelected,
  } = props;
  const { t, formatRelativeTime } = useLocale();
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const listRef = useRef<HTMLUListElement>(null);
  const renameRef = useRef<HTMLInputElement>(null);
  const selectAllRef = useRef<HTMLInputElement>(null);

  const selectedCount = threads.filter((th) => selectedIds.includes(th.id)).length;
  const allSelected = threads.length > 0 && selectedCount === threads.length;

  useEffect(() => {
    if (selectAllRef.current) selectAllRef.current.indeterminate = selectedCount > 0 && !allSelected;
  }, [selectedCount, allSelected]);

  useEffect(() => {
    if (renamingId) renameRef.current?.select();
  }, [renamingId]);

  function startRename(thread: AskThread) {
    setRenamingId(thread.id);
    setRenameValue(thread.title || '');
  }

  function commitRename(thread: AskThread) {
    const value = renameValue.trim();
    setRenamingId(null);
    if (value && value !== thread.title) onRename(thread, value);
  }

  function onListKeyDown(event: KeyboardEvent<HTMLUListElement>) {
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    const rows = Array.from(listRef.current?.querySelectorAll<HTMLButtonElement>('[data-thread-row]') ?? []);
    if (rows.length === 0) return;
    const index = rows.findIndex((row) => row === document.activeElement);
    let next = index;
    if (event.key === 'ArrowDown') next = index < 0 ? 0 : Math.min(rows.length - 1, index + 1);
    else if (event.key === 'ArrowUp') next = index < 0 ? rows.length - 1 : Math.max(0, index - 1);
    else if (event.key === 'Home') next = 0;
    else next = rows.length - 1;
    event.preventDefault();
    rows[next].focus();
  }

  function menuItems(thread: AskThread): ActionMenuItem[] {
    return [
      { label: 'Rename', onClick: () => startRename(thread) },
      thread.pinned
        ? { label: 'Unpin', onClick: () => onTogglePin(thread) }
        : { label: 'Pin', onClick: () => onTogglePin(thread) },
      // The open conversation always shows as read, so it only offers Mark unread.
      thread.id !== selectedId && (thread.unreadCount ?? 0) > 0
        ? { label: 'Mark read', onClick: () => onMarkRead(thread) }
        : { label: 'Mark unread', onClick: () => onMarkUnread(thread) },
      { label: 'Summarize', onClick: () => onSummarize(thread) },
      thread.archived
        ? { label: 'Unarchive', onClick: () => onToggleArchive(thread) }
        : { label: 'Archive', onClick: () => onToggleArchive(thread) },
      { label: 'Delete', onClick: () => onDelete(thread), danger: true },
    ];
  }

  return (
    <nav className="ask-threads" aria-label={t('Conversations')}>
      <div className="ask-threads-top">
        <div className="ask-threads-title-row">
          <h3 className="ask-threads-title">{t('Conversations')}</h3>
          {onRefresh && (
            <button
              type="button"
              className={`btn btn-sm refresh-btn${loading ? ' refreshing' : ''}`}
              onClick={onRefresh}
              disabled={loading}
              aria-busy={loading}
              aria-label="Refresh conversations"
              title="Refresh conversations"
            >
              <span className="refresh-btn-icon" aria-hidden="true">{'\u21BB'}</span>
            </button>
          )}
          {onClose && (
            <button type="button" className="ask-icon-btn ask-threads-close" onClick={onClose} aria-label={t('Close conversation list')} title={t('Close conversation list')}>
              &times;
            </button>
          )}
        </div>
        <button type="button" className="btn btn-primary btn-sm ask-new-btn" onClick={onNew} aria-label={t('New conversation')} title={t('Start an empty conversation. It is saved once you send the first message.')}>
          + {t('New')}
        </button>
        <input
          type="search"
          className="ask-threads-search"
          value={search}
          onChange={(e) => onSearchChange(e.target.value)}
          placeholder={t('Search conversations')}
          aria-label={t('Search conversations')}
          title={t('Find conversations by title text.')}
        />
        <label className="ask-threads-archived" title={t('Also list archived conversations. Archiving hides a conversation without deleting it; old inactive ones are archived automatically.')}>
          <input type="checkbox" title={t('Also list archived conversations. Archiving hides a conversation without deleting it; old inactive ones are archived automatically.')} checked={includeArchived} onChange={(e) => onIncludeArchivedChange(e.target.checked)} />
          {t('Show archived')}
        </label>
        {threads.length > 0 && (
          <div className="ask-threads-bulk">
            <label className="ask-threads-select-all" title={t('Select all')}>
              <input
                ref={selectAllRef}
                type="checkbox"
                aria-label={t('Select all')}
                checked={allSelected}
                disabled={threads.length === 0}
                onChange={(e) => { if (e.target.checked) onSelectAll(threads); else onClearSelection(); }}
              />
              {t('Select all')}
            </label>
            {selectedCount > 0 && (
              <>
                <span className="ask-threads-selected-count">{t('{{count}} selected', { count: selectedCount })}</span>
                <button
                  type="button"
                  className="btn btn-sm btn-danger ask-threads-delete-selected"
                  onClick={onDeleteSelected}
                  title={t('Delete the selected conversations.')}
                >
                  {t('Delete ({{count}})', { count: selectedCount })}
                </button>
                <button type="button" className="btn btn-sm ask-threads-clear-selection" onClick={onClearSelection} title={t('Clear selection')}>
                  {t('Clear selection')}
                </button>
              </>
            )}
          </div>
        )}
      </div>

      {error && (
        <div className="alert alert-error ask-threads-error" role="alert">
          <span>{error}</span>
          <button type="button" className="btn btn-sm" onClick={onRetry} title={t('Try loading the conversation list again.')}>{t('Retry')}</button>
        </div>
      )}

      {!error && !loading && threads.length === 0 && (
        <p className="ask-threads-empty text-dim">
          {search.trim() ? t('No conversations match your search.') : t('No conversations yet. Start one to ask a question or kick off work.')}
        </p>
      )}

      <ul className="ask-thread-items" ref={listRef} onKeyDown={onListKeyDown} aria-busy={loading || undefined}>
        {threads.map((thread) => {
          const working = isThreadWorking(thread, activity);
          const replying = isThreadReplying(thread, activity);
          const unread = thread.id === selectedId ? 0 : (thread.unreadCount ?? 0);
          const selected = thread.id === selectedId;
          const title = thread.title || t('New conversation');
          return (
            <li key={thread.id} className={`ask-thread-item${selected ? ' is-selected' : ''}${unread > 0 ? ' is-unread' : ''}`}>
              <label className="ask-thread-select" onClick={(e) => e.stopPropagation()}>
                <input
                  type="checkbox"
                  checked={selectedIds.includes(thread.id)}
                  aria-label={t('Select {{title}}', { title })}
                  onClick={(e) => e.stopPropagation()}
                  onChange={(e) => onToggleSelected(thread, e.target.checked)}
                />
              </label>
              {renamingId === thread.id ? (
                <form className="ask-thread-rename" onSubmit={(e) => { e.preventDefault(); commitRename(thread); }}>
                  <input
                    ref={renameRef}
                    value={renameValue}
                    maxLength={200}
                    aria-label={t('Conversation title')}
                    title={t('Type a new title and press Enter to save, or Escape to cancel. Up to 200 characters.')}
                    onChange={(e) => setRenameValue(e.target.value)}
                    onBlur={() => commitRename(thread)}
                    onKeyDown={(e) => { if (e.key === 'Escape') { e.preventDefault(); setRenamingId(null); } }}
                  />
                </form>
              ) : (
                <button
                  type="button"
                  data-thread-row
                  className="ask-thread-row"
                  aria-current={selected ? 'page' : undefined}
                  onClick={() => onSelect(thread)}
                  title={title}
                >
                  <span className="ask-thread-row-top">
                    {thread.pinned && <span className="ask-thread-pin" aria-label={t('Pinned')} title={t('Pinned: kept at the top of the list and never archived or deleted automatically.')}>&#128204;</span>}
                    <span className="ask-thread-name">{title}</span>
                    {unread > 0 && (
                      <span className="ask-unread-badge" aria-label={t('{{count}} unread', { count: unread })} title={t('{{count}} new messages since you last opened this conversation.', { count: unread })}>
                        {unread > 99 ? '99+' : unread}
                      </span>
                    )}
                  </span>
                  <span className="ask-thread-row-bottom text-dim">
                    {working && (
                      <span className="ask-thread-working" title={t('Work started from this conversation, such as a voyage or fleet action run, is still running.')}>
                        <span className="ask-live-dot" aria-hidden="true" />
                        {t('Working')}
                      </span>
                    )}
                    {!working && replying && <span className="ask-thread-working" title={t('The captain is writing a reply in this conversation.')}>{t('Replying...')}</span>}
                    {thread.archived && <span className="ask-thread-archived-tag" title={t('Archived: hidden from the list unless Show archived is on. Unarchive it from its menu.')}>{t('Archived')}</span>}
                    <span className="ask-thread-time">
                      {thread.lastMessageUtc ? formatRelativeTime(thread.lastMessageUtc) : ''}
                    </span>
                  </span>
                </button>
              )}
              <ActionMenu id={`ask-thread-${thread.id}`} items={menuItems(thread)} triggerLabel={t('Actions for {{title}}', { title })} />
            </li>
          );
        })}
      </ul>

      {loading && <p className="ask-threads-loading text-dim" role="status">{t('Loading conversations...')}</p>}
      {hasMore && !loading && (
        <button type="button" className="btn btn-sm ask-threads-more" onClick={onLoadMore} title={t('Load the next page of older conversations.')}>{t('Load more')}</button>
      )}
    </nav>
  );
}
