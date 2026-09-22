/**
 * Keep the selected ids whose rows are still present after a reload and drop the ids that vanished.
 * Returns the same array when nothing was dropped, so a refresh that changes no selection does not
 * trigger a re-render of the selection state.
 */
export function retainSelection(selected: string[], presentIds: Iterable<string>): string[] {
  if (selected.length === 0) return selected;
  const present = new Set(presentIds);
  const kept = selected.filter((id) => present.has(id));
  return kept.length === selected.length ? selected : kept;
}
