---
type: "Project"
title: "Desktop notifications go through an injectable command runner"
description: "NotificationService (armada watch) is an instance class using INotificationCommandRunner; tests record commands instead of running osascript."
timestamp: "2026-10-05T05:24:57.6611250Z"
created: "2026-10-05T05:24:57.3533920Z"
slug: "decision-notification-command-runner"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["lesson-script-editor-toasts"]
version: "1"
salience: "0.5"
---
2026-10-05: Armada.Core NotificationService became an instance class with INotificationCommandRunner (ProcessNotificationCommandRunner in production) and DesktopPlatformEnum; `armada watch` constructs one. The test suite uses RecordingNotificationCommandRunner and asserts the exact command/arguments per platform (osascript display notification with AppleScript escaping, notify-send with separate args, PowerShell toast with XML escaping). Reason: the old tests called the real notifier and every full test run raised macOS notifications attributed to Script Editor.
