---
type: "Feedback"
title: "Settings files are rewritten after load with every property, nulls included"
description: "Every settings file should be written back after load so new properties appear with defaults; done for the Admiral (277fd9a0), not yet for Harbor, tui.json, or Proxy."
timestamp: "2026-10-14T20:56:55.6063250Z"
created: "2026-10-14T20:56:55.2283880Z"
slug: "pref-settings-write-back"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["settings-json-loading", "important-settings-keys", "harbor-settings-pascalcase-and-link-port", "tui-preferences-and-profiles"]
version: "1"
salience: "0.5"
---
Every settings file (Admiral settings.json, Harbor settings, ~/.armada/tui.json, Proxy settings) should be rewritten after it is opened, so properties added in newer versions appear in the file with their default values, null ones as explicit nulls.

Why: on 2026-10-08 the owner expected this and found harbor.advertisedMcpBaseUrl missing from a server settings.json.

Status (checked 2026-10-14): the Admiral does this. Commit 277fd9a0 (2026-10-08) added ArmadaSettings.WriteBack (src/Armada.Core/Settings/ArmadaSettings.cs), which serializes with DefaultIgnoreCondition = Never, writes in place via SettingsFileStore.SaveInPlace (keeps a settings.json.bak-... backup, skips the write when nothing changed, works with a single-file Docker bind mount), and ArmadaServer calls it at startup. LoadAsync itself still does not save. No equivalent write-back was found in Armada.Harbor, Armada.Tui (tui.json), or Armada.Proxy.

How to apply: when adding or touching a settings property, make sure its file is written back on load and nullable values are written as explicit nulls; reuse SettingsFileStore.SaveInPlace for other settings files.
