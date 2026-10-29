import { describe, expect, it } from 'vitest';
import catalogSource from '../../../Armada.Server/wwwroot/i18n/armada.json?raw';
import pageSource from '../pages/Memories.tsx?raw';
import type { I18nCatalog } from '../i18n/runtime';

const S = "'((?:[^'\\\\]|\\\\.)*)'";
const CALL = new RegExp(`\\bt\\(\\s*${S}`, 'g');

/**
 * Strings the Memories page translates outside a t('...') literal: the memory types and their tips (passed to t()
 * as data) and the row action menu labels and tooltip (translated by ActionMenu).
 */
const SHARED_KEYS = [
  'Episodic',
  'Semantic',
  'Procedural',
  'Episodic: what happened and when, such as events, actions taken and key decisions.',
  'Semantic: a fact on its own, such as a preference or a rule about the code.',
  'Procedural: how to do something, such as a workflow or checklist.',
  'Duplicate',
  'View JSON',
  'Delete',
  'Edit a new memory before saving. Its ID, key and agent history are not reused.',
];

function memoryKeys(): string[] {
  const keys = new Set<string>(SHARED_KEYS);
  for (const m of pageSource.matchAll(CALL)) {
    const key = m[1].replace(/\\'/g, "'");
    if (key.trim()) keys.add(key);
  }
  return [...keys].sort();
}

function placeholders(text: string): string {
  return (text.match(/\{\{[\w.]+\}\}/g) ?? []).sort().join(',');
}

describe('Memories i18n catalog', () => {
  const catalog = JSON.parse(catalogSource) as I18nCatalog;
  const keys = memoryKeys();

  it('finds the Memories page strings, including the column filter tooltips', () => {
    expect(keys.length).toBeGreaterThan(50);
    expect(keys).toContain('Filter by vessel');
    expect(keys).toContain('Show only memories whose vessel name contains this text. Not case sensitive.');
    expect(keys).toContain('Show only tenant-wide or only personal memories.');
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
