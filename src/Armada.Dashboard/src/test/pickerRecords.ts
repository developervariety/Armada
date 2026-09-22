/** Twelve records: more than the server's default page of ten, so a picker that reads one default page is short. */
export const PICKER_RECORD_COUNT = 12;

export function pickerRecords(prefix: string) {
  return Array.from({ length: PICKER_RECORD_COUNT }, (_, index) => ({
    id: `${prefix}_${String(index).padStart(2, '0')}`,
    name: `${prefix} ${String(index).padStart(2, '0')}`,
  }));
}

/** One enumeration page the way the server returns it when the caller sends no page size. */
export function serverDefaultPage<T>(records: T[]) {
  return { success: true, pageNumber: 1, pageSize: 10, totalPages: Math.ceil(records.length / 10), totalRecords: records.length, totalMs: 1, objects: records.slice(0, 10) };
}

export function optionValues(select: HTMLElement): string[] {
  return Array.from((select as HTMLSelectElement).options).map((option) => option.value).filter(Boolean);
}
