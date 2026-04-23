import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  DEFAULT_LOCALES,
  applyDocumentLocale,
  formatAbsoluteDateTime,
  formatDateOnly,
  formatRelativeFromUtc,
  getInitialLocale,
  getSupportedLocales,
  installDialogOverrides,
  installDocumentTranslator,
  installLocaleSensitiveBuiltins,
  loadCatalog,
  normalizeLocale,
  persistLocale,
  translateTemplate,
  type I18nCatalog,
  type LocaleMeta,
} from '../i18n/runtime';

interface LocaleState {
  locale: string;
  setLocale: (locale: string) => void;
  supportedLocales: LocaleMeta[];
  catalog: I18nCatalog | null;
  t: (text: string, params?: Record<string, string | number | null | undefined>) => string;
  formatDateTime: (utc: string | null | undefined) => string;
  formatDate: (utc: string | null | undefined) => string;
  formatRelativeTime: (utc: string | null | undefined) => string;
}

const LocaleContext = createContext<LocaleState | null>(null);

export function LocaleProvider({ children }: { children: ReactNode }) {
  const [catalog, setCatalog] = useState<I18nCatalog | null>(null);
  const [locale, setLocaleState] = useState(() => getInitialLocale(null));

  const catalogRef = useRef<I18nCatalog | null>(catalog);
  const localeRef = useRef(locale);

  useEffect(() => {
    catalogRef.current = catalog;
  }, [catalog]);

  useEffect(() => {
    localeRef.current = locale;
  }, [locale]);

  useEffect(() => {
    loadCatalog().then((loaded) => {
      setCatalog(loaded);
      setLocaleState((current) => normalizeLocale(current, loaded));
    });
  }, []);

  useEffect(() => {
    persistLocale(locale);
    applyDocumentLocale(locale, catalog);
    if (document.body) {
      const normalized = normalizeLocale(locale, catalog);
      if (normalized !== locale) {
        setLocaleState(normalized);
        return;
      }
    }
  }, [locale, catalog]);

  useEffect(() => {
    if (!document.body) return;
    installLocaleSensitiveBuiltins(() => localeRef.current);
    installDialogOverrides(() => localeRef.current, () => catalogRef.current);
    return installDocumentTranslator(document.body, () => localeRef.current, () => catalogRef.current);
  }, []);

  const setLocale = useCallback((nextLocale: string) => {
    const normalized = normalizeLocale(nextLocale, catalogRef.current);
    localeRef.current = normalized;
    setLocaleState(normalized);
    persistLocale(normalized);
    applyDocumentLocale(normalized, catalogRef.current);
  }, []);

  const t = useCallback((text: string, params?: Record<string, string | number | null | undefined>) => {
    return translateTemplate(localeRef.current, text, catalogRef.current, params);
  }, []);

  const value = useMemo<LocaleState>(() => ({
    locale,
    setLocale,
    supportedLocales: getSupportedLocales(catalog).length > 0 ? getSupportedLocales(catalog) : DEFAULT_LOCALES,
    catalog,
    t,
    formatDateTime: (utc) => formatAbsoluteDateTime(locale, utc),
    formatDate: (utc) => formatDateOnly(locale, utc),
    formatRelativeTime: (utc) => formatRelativeFromUtc(locale, utc),
  }), [catalog, locale, setLocale, t]);

  return (
    <LocaleContext.Provider value={value}>
      {children}
    </LocaleContext.Provider>
  );
}

export function useLocale(): LocaleState {
  const ctx = useContext(LocaleContext);
  if (!ctx) throw new Error('useLocale must be used within LocaleProvider');
  return ctx;
}
