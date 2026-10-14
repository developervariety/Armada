---
type: "Reference"
title: "Dashboard localStorage keys"
description: "armada_session_token, armada_setup_completed, armada_sidebar_collapsed, armada_theme, armada_locale, armada_notifications, armada_table_<id> (useTablePrefs), armada_ask_captain, armada_ask_show_thinking, armada_history_saved_views, armada_workspace_*."
timestamp: "2026-10-05T05:42:06.4774530Z"
created: "2026-10-05T05:42:06.1331900Z"
slug: "dashboard-localstorage-keys"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["isolated-server-for-dashboard-testing", "dashboard-auth-and-security-ui", "tui-preferences-and-profiles"]
version: "1"
salience: "0.5"
---
Keys found in src/Armada.Dashboard/src: armada_session_token (AuthContext; the X-Token value), armada_setup_completed (suppresses the SetupWizard auto-open that happens on an empty deployment), armada_sidebar_collapsed, armada_theme (ThemeContext), armada_locale (i18n runtime / LocaleContext), armada_notifications (NotificationContext history), armada_table_<tableId> (lib/useTablePrefs: hidden columns with defaultHidden and a defaultsVersion migration, page size via usePersistedPageSize), armada_ask_captain and armada_ask_show_thinking (Ask Armada page), armada_history_saved_views (History/Activity), armada_workspace_last_vessel and armada_workspace_recent_vessels (Workspace). For automated visual testing (Playwright) set armada_session_token, armada_setup_completed=true, and armada_sidebar_collapsed=false before loading. The TUI keeps its equivalents in ~/.armada/tui.json, not shared with the browser.
