/** Auto-land rules as the vessel form edits them. Numbers and path lists are text so inputs can be empty. */
export interface AutoLandForm {
  enabled: boolean;
  maxFiles: string;
  maxAddedLines: string;
  allowPaths: string;
  denyPaths: string;
  /** True when the stored predicate could not be parsed; the form then shows no rules instead of guessing. */
  unparsable: boolean;
}

/** Predicate object sent to the server as autoLandPredicate. */
export interface AutoLandPredicatePayload {
  enabled: boolean;
  maxFiles: number | null;
  maxAddedLines: number | null;
  allowPaths: string[];
  denyPaths: string[];
}

export const EMPTY_AUTO_LAND_FORM: AutoLandForm = {
  enabled: false,
  maxFiles: '',
  maxAddedLines: '',
  allowPaths: '',
  denyPaths: '',
  unparsable: false,
};

function readKey(source: Record<string, unknown>, name: string): unknown {
  const match = Object.keys(source).find((key) => key.toLowerCase() === name.toLowerCase());
  return match === undefined ? undefined : source[match];
}

function numberText(value: unknown): string {
  return typeof value === 'number' && Number.isFinite(value) ? String(value) : '';
}

function listText(value: unknown): string {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string').join('\n') : '';
}

/**
 * Load the stored AutoLandPredicate JSON string into form fields. Key casing is ignored, as on the server. A missing
 * Enabled value means enabled, matching the server model default.
 */
export function autoLandFormFromPredicate(stored: string | null | undefined): AutoLandForm {
  if (!stored || !stored.trim()) return { ...EMPTY_AUTO_LAND_FORM };

  let parsed: unknown;
  try {
    parsed = JSON.parse(stored);
  } catch {
    return { ...EMPTY_AUTO_LAND_FORM, unparsable: true };
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { ...EMPTY_AUTO_LAND_FORM, unparsable: true };
  }

  const source = parsed as Record<string, unknown>;
  const enabled = readKey(source, 'enabled');
  return {
    enabled: typeof enabled === 'boolean' ? enabled : true,
    maxFiles: numberText(readKey(source, 'maxFiles')),
    maxAddedLines: numberText(readKey(source, 'maxAddedLines')),
    allowPaths: listText(readKey(source, 'allowPaths')),
    denyPaths: listText(readKey(source, 'denyPaths')),
    unparsable: false,
  };
}

function parseCount(text: string): number | null {
  if (!text.trim()) return null;
  const value = parseInt(text, 10);
  return Number.isFinite(value) && value >= 0 ? value : null;
}

function parseList(text: string): string[] {
  return text.split(/\r?\n/).map((item) => item.trim()).filter((item) => item.length > 0);
}

/**
 * Build the autoLandPredicate object for a save. Returns null only when auto-land is off and no rule is set, so
 * rules on a disabled predicate are kept rather than dropped.
 */
export function autoLandPredicatePayload(form: AutoLandForm): AutoLandPredicatePayload | null {
  const payload: AutoLandPredicatePayload = {
    enabled: form.enabled,
    maxFiles: parseCount(form.maxFiles),
    maxAddedLines: parseCount(form.maxAddedLines),
    allowPaths: parseList(form.allowPaths),
    denyPaths: parseList(form.denyPaths),
  };
  const hasRule = payload.maxFiles !== null
    || payload.maxAddedLines !== null
    || payload.allowPaths.length > 0
    || payload.denyPaths.length > 0;
  return form.enabled || hasRule ? payload : null;
}
