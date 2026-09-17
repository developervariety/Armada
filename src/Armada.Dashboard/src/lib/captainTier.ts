/** A preference rank from a form field: an integer clamped to [-1000, 1000], or 0 when the field is not a number. */
export function parsePreferenceRank(value: string): number {
  const parsed = Number.parseInt(value, 10);
  if (Number.isNaN(parsed)) return 0;
  return Math.max(-1000, Math.min(1000, parsed));
}
