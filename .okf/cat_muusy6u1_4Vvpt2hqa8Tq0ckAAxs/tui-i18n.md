---
type: "Reference"
title: "TUI localization over the dashboard catalog"
description: "LocalizationService fetches /dashboard/i18n/armada.json (same nine languages and keys as the dashboard, ICU plurals via PluralRules), measures widths in terminal cells; screens pass English text through the localizer."
timestamp: "2026-10-05T05:40:31.9522610Z"
created: "2026-10-05T05:40:31.6758970Z"
slug: "tui-i18n"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["dashboard-i18n", "tui-theming-glyphs-and-80x24"]
version: "1"
salience: "0.5"
---
Services/LocalizationService.cs (ITextLocalizer, LocalizationArgs, PluralRules) fetches the dashboard's catalog served at /dashboard/i18n/armada.json (source src/Armada.Server/wwwroot/i18n/armada.json; client call ArmadaClient.GetI18nCatalogAsync, a TUI-only extension in parity.json), applies the same phrase keys and ICU plurals as the dashboard, formats dates and numbers per locale, and persists the chosen locale in tui.json. Languages: English plus Spanish, Simplified and Traditional Chinese (Mandarin), Cantonese, Japanese, German, French, Italian (non-English marked beta). The picker works on the login screen. Requirement (I18N.md): every user-visible string goes through the shared catalog, no hard-coded strings in screens; screens call T(text) / T(text, args) with English source text (OpsScreen helpers). Widths are measured in terminal cells (Text/TextCells) so CJK lines up; Tui.ThemeI18n checks this headlessly.
