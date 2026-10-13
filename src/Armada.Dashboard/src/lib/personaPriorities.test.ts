import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { translateTemplate, type I18nCatalog } from '../i18n/catalog';
import { buildPersonaDuplicatePayload } from './duplicates';
import type { Persona } from '../types/models';

const catalog = JSON.parse(readFileSync('../Armada.Server/wwwroot/i18n/armada.json', 'utf8')) as I18nCatalog;

describe('Persona priorities across client surfaces', () => {
  it('duplicates routing without aliasing the source list', () => {
    const persona = { name: 'Role', promptTemplateName: 'persona.worker', captainPriorities: [{ runtime: 'Mux', endpoint: 'local' }], priorityAvailability: 'WaitForFirstPriority', priorityExhaustion: 'Wait' } as Persona;
    const copy = buildPersonaDuplicatePayload(persona);
    expect(copy.priorityAvailability).toBe('WaitForFirstPriority');
    copy.captainPriorities![0].endpoint = 'other';
    expect(persona.captainPriorities![0].endpoint).toBe('local');
  });

  for (const locale of Object.keys(catalog.locales)) {
    it(`translates priority controls and interpolation in ${locale}`, () => {
      for (const key of ['Captain priorities', 'Minimum tier', 'Try lower priorities', 'Wait for the first priority', 'Use any eligible captain']) {
        expect(translateTemplate(locale, key, catalog)).not.toBe(key);
      }
      expect(translateTemplate(locale, 'Priority {{number}}', catalog, { number: 3 })).not.toContain('{{');
    });
  }
});
