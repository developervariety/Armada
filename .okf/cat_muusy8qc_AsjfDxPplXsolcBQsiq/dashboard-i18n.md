---
type: "Reference"
title: "Dashboard i18n: runtime.ts and the armada.json catalog"
description: "Catalog src/Armada.Server/wwwroot/i18n/armada.json (defaultLocale, supportedLocales, locales.<code>.{terms,phrases,sections}) served at /dashboard/i18n/armada.json; nine locales, all but en beta; locale kept in localStorage armada_locale."
timestamp: "2026-10-05T05:41:27.8384670Z"
created: "2026-10-05T05:41:27.3691610Z"
slug: "dashboard-i18n"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["tui-i18n", "open-o19-security-strings-english-only", "decision-v1-locales-and-tui-d4-d5"]
version: "1"
salience: "0.5"
---
src/Armada.Dashboard/src/i18n/runtime.ts loads the catalog (I18nCatalog: defaultLocale, supportedLocales[] of LocaleMeta {code, label, nativeLabel, dir, aliases, beta, betaLabel}, locales: Record<code, {terms, phrases, sections}>) and translates English source text, including attributes title, placeholder, aria-label, aria-description, alt, and patches dialogs and builtins; falls back to DEFAULT_LOCALES when the catalog is missing. Locales: en, es, zh-Hans, zh-Hant, yue-Hant, ja, de, fr, it; non-English are labeled beta until reviewed by a native speaker (decision D4). The catalog file lives in src/Armada.Server/wwwroot/i18n/armada.json (English has no pack; keys are the English phrases) and is served by the Admiral; LocaleContext holds the choice, stored in localStorage armada_locale; the language selector works from the login screen. plural.test.ts covers ICU plurals. The TUI uses the same catalog. Known gap O-19: W1 security strings are English only. When adding UI text, add phrases for each locale (or at least keep the English key stable).
