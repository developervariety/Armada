import type { Ref } from 'react';
import type { AskThread } from '../../types/models';

interface AskThreadShelfProps {
  threads: AskThread[];
  selectedId: string;
  includeArchived: boolean;
  loading: boolean;
  t: (value: string) => string;
  onSelect: (thread: AskThread) => void;
  onCreate: () => void;
  onRename: (thread: AskThread) => void;
  onPin: (thread: AskThread) => void;
  onArchive: (thread: AskThread) => void;
  onToggleUnread: (thread: AskThread) => void;
  /** Ids of archived conversations ticked for deletion. */
  deleteSelection: string[];
  onToggleDeleteSelection: (thread: AskThread) => void;
  onDelete: (thread: AskThread) => void;
  onDeleteSelected: () => void;
  /** Receives the shelf element so the page can size it. */
  shelfRef?: Ref<HTMLElement>;
  onToggleArchived: () => void;
}

export default function AskThreadShelf(props: AskThreadShelfProps) {
  const { threads, selectedId, includeArchived, loading, t } = props;
  return (
    <aside ref={props.shelfRef} className="ask-thread-shelf" aria-label={t('Saved conversations')}>
      <div className="ask-thread-shelf-heading">
        <strong>{t('Conversations')}</strong>
        <button className="btn btn-sm" onClick={props.onCreate} disabled={loading}>{t('New')}</button>
      </div>
      <label className="ask-thread-archive-filter">
        <input type="checkbox" checked={includeArchived} onChange={props.onToggleArchived} disabled={loading} />
        {t('Show archived')}
      </label>
      {props.deleteSelection.length > 0 && (
        <button className="btn btn-sm btn-danger" onClick={props.onDeleteSelected} disabled={loading}>
          {t('Delete selected')} ({props.deleteSelection.length})
        </button>
      )}
      {loading && <p className="text-dim" role="status">{t('Loading conversations…')}</p>}
      {!loading && threads.length === 0 && <p className="text-dim">{t('No saved conversations yet.')}</p>}
      <div className="ask-thread-list">
        {threads.map((thread) => (
          <article key={thread.id} className={thread.id === selectedId ? 'ask-thread-item selected' : 'ask-thread-item'}>
            {thread.archived && (
              <input
                type="checkbox"
                aria-label={t('Select conversation for deletion')}
                checked={props.deleteSelection.includes(thread.id)}
                onChange={() => props.onToggleDeleteSelection(thread)}
                disabled={loading}
              />
            )}
            <button className="ask-thread-select" onClick={() => props.onSelect(thread)} disabled={loading}>
              {thread.pinned && <span aria-label={t('Pinned')}>★ </span>}
              <span>{thread.title}</span>
              {thread.unreadCount > 0 && <small className="ask-thread-unread" aria-label={t('Unread reply')}>●</small>}
              {thread.archived && <small>{t('Archived')}</small>}
            </button>
            <div className="ask-thread-actions">
              <button className="icon-btn" aria-label={thread.unreadCount > 0 ? t('Mark conversation read') : t('Mark conversation unread')} title={thread.unreadCount > 0 ? t('Mark read') : t('Mark unread')} onClick={() => props.onToggleUnread(thread)} disabled={loading}>{thread.unreadCount > 0 ? '○' : '●'}</button>
              <button className="icon-btn" aria-label={thread.pinned ? t('Unpin conversation') : t('Pin conversation')} title={thread.pinned ? t('Unpin') : t('Pin')} onClick={() => props.onPin(thread)} disabled={loading}>★</button>
              <button className="icon-btn" aria-label={t('Rename conversation')} title={t('Rename')} onClick={() => props.onRename(thread)} disabled={loading}>✎</button>
              <button className="icon-btn" aria-label={thread.archived ? t('Restore conversation') : t('Archive conversation')} title={thread.archived ? t('Restore') : t('Archive')} onClick={() => props.onArchive(thread)} disabled={loading}>{thread.archived ? '↩' : '⌑'}</button>
              {thread.archived && <button className="icon-btn" aria-label={t('Delete conversation')} title={t('Delete')} onClick={() => props.onDelete(thread)} disabled={loading}>🗑</button>}
            </div>
          </article>
        ))}
      </div>
    </aside>
  );
}
