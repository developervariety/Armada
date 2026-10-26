import { useEffect, useMemo, useState } from 'react';
import { resolveHiddenColumns, toggleHiddenColumn, type ColumnVisibilitySpec } from './tableColumns';

export function useColumnVisibility(tableKey: string, columns: ColumnVisibilitySpec[]) {
  const signature = columns.map(c => `${c.key}:${c.required ? 1 : 0}:${c.defaultHidden ? 1 : 0}`).join('|');
  const specs = useMemo(() => columns.map(c => ({ key: c.key, required: c.required, defaultHidden: c.defaultHidden })), [signature]);
  const read = () => {
    try { return resolveHiddenColumns(JSON.parse(localStorage.getItem(`armada_columns_${tableKey}`) || 'null'), specs); }
    catch { return resolveHiddenColumns(null, specs); }
  };
  const [hidden, setHidden] = useState(read);
  const [loadedKey, setLoadedKey] = useState(tableKey);
  if (loadedKey !== tableKey) { setLoadedKey(tableKey); setHidden(read()); }
  useEffect(() => {
    try { localStorage.setItem(`armada_columns_${tableKey}`, JSON.stringify({ hidden })); } catch { /* Choice still applies for this session. */ }
  }, [hidden, tableKey]);
  const isVisible = (key: string) => specs.find(c => c.key === key)?.required === true || !hidden.includes(key);
  const toggle = (key: string) => setHidden(current => toggleHiddenColumn(specs, current, key));
  const reset = () => setHidden(specs.filter(c => c.defaultHidden && !c.required).map(c => c.key));
  return { isVisible, toggle, reset };
}
