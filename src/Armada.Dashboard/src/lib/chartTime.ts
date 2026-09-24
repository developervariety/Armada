// Chart time labels. They pass no locale, so the app's locale patch on Date formats them in the chosen language.

/** Axis label for a chart bucket: time of day for short ranges, date and time past two days. */
export function formatBucketLabel(ts: number, stepMinutes: number, hours: number): string {
  const d = new Date(ts);
  if (stepMinutes <= 15) return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  if (hours > 48) return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
}

/** Tooltip time for a chart point. */
export function formatTooltipTime(ts: number): string {
  const d = new Date(ts);
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}
