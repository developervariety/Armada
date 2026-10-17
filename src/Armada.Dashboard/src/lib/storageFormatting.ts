/** Binary units are identifiers; numeric values always use the active locale. */
export function formatStorageBytes(value: number | null | undefined, locale: string, unavailable: string): string {
  if (value == null) return unavailable;
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  const power = value > 0 ? Math.min(4, Math.floor(Math.log(value) / Math.log(1024))) : 0;
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(value / 1024 ** power)} ${units[power]}`;
}
