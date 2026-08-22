/**
 * Shared formatting helpers. Previously copy-pasted into individual pages
 * (e.g. RequestHistory, ApiExplorer); centralized here so they stay consistent.
 */

/** Format a byte count as a human-readable size (e.g. "0 B", "12 KB", "3.4 MB"). */
export function formatBytes(bytes: number): string {
  if (!bytes) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(value >= 10 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}

/** Safely parse a JSON string, returning the fallback on null/empty/invalid input. */
export function parseJsonString<T>(value: string | null | undefined, fallback: T): T {
  if (!value) return fallback;
  try {
    return JSON.parse(value) as T;
  } catch {
    return fallback;
  }
}

/** CSS class for an HTTP-method pill (e.g. GET/POST). */
export function methodClass(method: string): string {
  return `request-method-pill request-method-${method.toLowerCase()}`;
}
