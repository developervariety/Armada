---
type: "Project"
title: "\"Script Editor\" notifications came from the test suite calling osascript"
description: "NotificationServiceSuite called the real NotificationService.Send; on macOS osascript notifications show as Script Editor; every full run raised four toasts."
timestamp: "2026-10-05T05:25:18.8757690Z"
created: "2026-10-05T05:25:18.5952620Z"
slug: "lesson-script-editor-toasts"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
links: ["decision-notification-command-runner"]
version: "1"
salience: "0.5"
---
Symptom (2026-10-05): the owner kept getting macOS toast notifications from "Script Editor". Cause: Test.Shared/Suites/Services/NotificationServiceSuite.cs called Armada.Core NotificationService.Send("Test Title", "Test Message") etc., which runs `osascript -e 'display notification ...'`; macOS attributes osascript notifications to Script Editor. With many agents running full suites, toasts were constant. Fix: injectable INotificationCommandRunner and a recording fake in tests (decision-notification-command-runner). General lesson: tests must never invoke real desktop/OS side effects (notifications, keychains, real agent CLIs, the user's config files); inject them. The TUI's own OsNotifier is off by default (OsNotifications Off).
