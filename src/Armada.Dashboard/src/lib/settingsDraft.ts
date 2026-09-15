/**
 * Draft handling for settings forms that reload from the server while an operator edits.
 *
 * A form keeps three copies: `base`, the server values its draft started from; `draft`, what the form
 * shows; and `next`, the server values of a new load or a save response. A field the operator has not
 * edited (draft equals base) adopts the new server value. A field the operator has edited keeps the
 * draft, so a reload or another section's save never discards unsaved work.
 */

type Plain = Record<string, unknown>;

function isPlainObject(value: unknown): value is Plain {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/** Structural equality for JSON-shaped values. */
export function sameValue(left: unknown, right: unknown): boolean {
  if (left === right) return true;
  if (typeof left !== 'object' || typeof right !== 'object' || left === null || right === null) return false;
  return JSON.stringify(left) === JSON.stringify(right);
}

/**
 * Merge a new server copy into a draft. Plain objects merge field by field; any other value is taken
 * whole from `next` when the draft still equals `base`, and kept from the draft otherwise.
 */
export function mergeDraft<T>(base: T, draft: T, next: T): T {
  if (isPlainObject(base) && isPlainObject(draft) && isPlainObject(next)) {
    const merged: Plain = { ...next };
    for (const key of Object.keys(draft)) {
      if (!(key in next)) {
        merged[key] = draft[key];
        continue;
      }
      merged[key] = mergeDraft(base[key], draft[key], next[key]);
    }
    return merged as T;
  }
  return sameValue(draft, base) ? next : draft;
}

/** Top-level keys whose draft value differs from the base value. */
export function changedKeys<T extends object>(base: T, draft: T): Array<keyof T> {
  return (Object.keys(draft) as Array<keyof T>).filter((key) => !sameValue(base[key], draft[key]));
}

/** True when any top-level field of the draft differs from the base. */
export function isDirty<T extends object>(base: T, draft: T): boolean {
  return changedKeys(base, draft).length > 0;
}

/**
 * The base to merge a save response against: the old base with the sent fields replaced by the values
 * that were sent. Sent fields then adopt the server's stored values, and fields edited during the save
 * or left unsaved stay as drafts.
 */
export function baseAfterSave<T extends object>(base: T, sent: Partial<T>): T {
  return { ...base, ...sent };
}
