export interface ColumnVisibilitySpec { key: string; required?: boolean; defaultHidden?: boolean }

export function resolveHiddenColumns(stored: unknown, columns: ColumnVisibilitySpec[]): string[] {
  const defaults = columns.filter(c => c.defaultHidden && !c.required).map(c => c.key);
  if (!stored || typeof stored !== 'object' || !Array.isArray((stored as { hidden?: unknown }).hidden)) return defaults;
  const known = new Set(columns.filter(c => !c.required).map(c => c.key));
  return [...new Set((stored as { hidden: unknown[] }).hidden.filter((key): key is string => typeof key === 'string' && known.has(key)))];
}

export function toggleHiddenColumn(columns: ColumnVisibilitySpec[], hidden: string[], key: string): string[] {
  if (!columns.some(c => c.key === key && !c.required)) return hidden;
  return hidden.includes(key) ? hidden.filter(k => k !== key) : [...hidden, key];
}
