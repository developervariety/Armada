import { expect, it } from 'vitest';
import { formatStorageBytes } from './storageFormatting';
import { translateTemplate } from '../i18n/catalog';
it('formats known bytes, zero and missing values without inventing zeros', () => {
  expect(formatStorageBytes(null, 'en', 'Unavailable')).toBe('Unavailable');
  expect(formatStorageBytes(0, 'en', 'Unavailable')).toBe('0 B');
  expect(formatStorageBytes(1536, 'en', 'Unavailable')).toBe('1.5 KiB');
  expect(formatStorageBytes(1536, 'de', 'Unavailable')).toBe('1,5 KiB');
  expect(formatStorageBytes(1536, 'ar', 'Unavailable')).toContain('KiB');
});
it('resolves stable storage message keys before and after catalog load', () => {
  expect(translateTemplate('en', 'storage.title', null)).toBe('Storage');
  expect(translateTemplate('es', 'storage.title', { defaultLocale: 'en', supportedLocales: [], locales: { es: { phrases: { 'storage.title': 'Almacenamiento' } } } })).toBe('Almacenamiento');
  expect(translateTemplate('ja', 'storage.measured', null, { time: 'NOW', seconds: 300 })).toContain('NOW');
});
