import { describe, expect, it } from 'vitest';
import catalogSource from '../../../Armada.Server/wwwroot/i18n/armada.json?raw';
import modalSource from '../components/vessels/VesselCleanupModal.tsx?raw';
import type { I18nCatalog } from '../i18n/runtime';

const S = "'((?:[^'\\\\]|\\\\.)*)'";
const CALL = new RegExp(`\\bt\\(\\s*${S}`, 'g');
const FIELD = new RegExp(`\\b(?:title|hint|confirmLabel):\\s*${S}`, 'g');
const PROP = /\b(?:title|confirmLabel)="([^"]+)"/g;

/**
 * Strings the vessel Cleanup feature shows outside t() literals in its modal: the action menu label and tooltip on
 * the Vessels and vessel pages (translated by ActionMenu), and the resource types and item outcomes the modal passes
 * to t() as data.
 */
const SHARED_KEYS = [
  'Cleanup',
  "Inspect this vessel's docks, worktrees and Armada branches, see which are safe, in use or unknown, and remove safe ones after you confirm. Opening it deletes nothing.",
  'Dock',
  'Worktree',
  'Branch',
  'Repository',
  'Deleted',
  'Skipped',
  'Failed',
];

function cleanupKeys(): string[] {
  const keys = new Set<string>(SHARED_KEYS);
  for (const re of [CALL, FIELD]) {
    for (const m of modalSource.matchAll(re)) {
      const key = m[1].replace(/\\'/g, "'");
      if (key.trim()) keys.add(key);
    }
  }
  for (const m of modalSource.matchAll(PROP)) keys.add(m[1]);
  return [...keys].sort();
}

function placeholders(text: string): string {
  return (text.match(/\{\{[\w.]+\}\}/g) ?? []).sort().join(',');
}

describe('Vessel cleanup i18n catalog', () => {
  const catalog = JSON.parse(catalogSource) as I18nCatalog;
  const keys = cleanupKeys();

  it('finds the vessel cleanup strings', () => {
    expect(keys).toContain('Safe to clean up');
    expect(keys).toContain('Still in use');
    expect(keys).toContain('Unknown disposition');
    expect(keys).toContain('Clean up vessel resources');
    expect(keys).toContain('Clean up selected ({{count}})');
  });

  it('has a translation for every string in every non-English locale', () => {
    const missing: string[] = [];
    for (const meta of catalog.supportedLocales) {
      if (meta.code === catalog.defaultLocale) continue;
      const pack = catalog.locales[meta.code] ?? {};
      for (const key of keys) {
        if (!pack.phrases?.[key] && !pack.terms?.[key] && !pack.sections?.[key]) missing.push(`${meta.code}: ${key}`);
      }
    }
    expect(missing).toEqual([]);
  });

  it('keeps placeholders intact in translations', () => {
    const bad: string[] = [];
    for (const [code, pack] of Object.entries(catalog.locales)) {
      for (const key of keys) {
        const value = pack.phrases?.[key] ?? pack.terms?.[key];
        if (value && placeholders(key) !== placeholders(value)) bad.push(`${code}: ${key}`);
      }
    }
    expect(bad).toEqual([]);
  });
});
