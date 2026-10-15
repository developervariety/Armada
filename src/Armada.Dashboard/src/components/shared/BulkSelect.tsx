import { useLocale } from '../../context/LocaleContext';

interface BulkSelectProps {
  /** All item IDs on the current page */
  allIds: string[];
  /** Currently selected IDs */
  selectedIds: string[];
  /** Toggle selection of one item */
  onToggle: (id: string) => void;
  /** Select all items */
  onSelectAll: () => void;
  /** Clear selection */
  onClearSelection: () => void;
  /** Bulk action buttons to render when items are selected */
  actions?: { label: string; onClick: () => void; danger?: boolean }[];
}

export function BulkSelectCheckbox({
  id,
  selectedIds,
  onToggle,
}: {
  id: string;
  selectedIds: string[];
  onToggle: (id: string) => void;
}) {
  const { t } = useLocale();
  return (
    <input
      type="checkbox"
      title={t('Include this row in the bulk actions shown above the table.')}
      aria-label={t('Select row')}
      checked={selectedIds.includes(id)}
      onChange={(e) => { e.stopPropagation(); onToggle(id); }}
      onClick={(e) => e.stopPropagation()}
    />
  );
}

export function BulkSelectHeader({
  allIds,
  selectedIds,
  onSelectAll,
  onClearSelection,
}: {
  allIds: string[];
  selectedIds: string[];
  onSelectAll: () => void;
  onClearSelection: () => void;
}) {
  const allSelected = allIds.length > 0 && allIds.every(id => selectedIds.includes(id));
  const someSelected = selectedIds.length > 0 && !allSelected;
  const { t } = useLocale();

  return (
    <input
      type="checkbox"
      title={allSelected ? t('Clear the selection on this page.') : t('Select every row on this page (not other pages) for bulk actions.')}
      aria-label={t('Select all')}
      checked={allSelected}
      ref={(el) => {
        if (el) el.indeterminate = someSelected;
      }}
      onChange={() => {
        if (allSelected) {
          onClearSelection();
        } else {
          onSelectAll();
        }
      }}
    />
  );
}

export default function BulkSelectBar({
  selectedIds,
  onClearSelection,
  actions = [],
}: Pick<BulkSelectProps, 'selectedIds' | 'onClearSelection' | 'actions'>) {
  const { t } = useLocale();
  if (selectedIds.length === 0) return null;

  return (
    <div className="view-actions" style={{ marginBottom: '0.5rem' }}>
      <span className="text-dim" style={{ fontSize: '0.85rem' }} title={t('Rows ticked for the bulk actions next to this count.')}>
        {selectedIds.length} {t('selected')}
      </span>
      {actions.map((action, i) => (
        <button
          key={i}
          className={`btn btn-sm${action.danger ? ' btn-danger' : ''}`}
          onClick={action.onClick}
          title={t('Apply {{action}} to all {{count}} selected rows.', { action: t(action.label), count: selectedIds.length })}
        >
          {t(action.label)}
        </button>
      ))}
      <button className="btn btn-sm" onClick={onClearSelection} title={t('Untick every selected row. Nothing is changed.')}>
        {t('Clear')}
      </button>
    </div>
  );
}
