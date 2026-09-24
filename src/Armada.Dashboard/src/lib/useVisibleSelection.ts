import { useEffect, useMemo, useState } from 'react';
import { retainSelection } from './selection';

/**
 * Row selection limited to the rows a table shows. `visibleIds` are the ids the current filters show (every page of
 * them); a selected row that a filter hides or a reload no longer returns leaves the selection, and it is not selected
 * again when it reappears, so a bulk action never acts on a row that is not on the list. Pass a memoized array.
 */
export function useVisibleSelection(visibleIds: string[]) {
  const [stored, setSelected] = useState<string[]>([]);
  const selected = useMemo(() => retainSelection(stored, visibleIds), [stored, visibleIds]);
  useEffect(() => {
    setSelected((prev) => retainSelection(prev, visibleIds));
  }, [visibleIds]);

  const allSelected = selected.length > 0 && selected.length === visibleIds.length;
  function toggleSelect(id: string) {
    setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));
  }
  function selectAll() {
    setSelected(visibleIds);
  }
  function clearSelection() {
    setSelected([]);
  }

  return { selected, setSelected, allSelected, toggleSelect, selectAll, clearSelection };
}
