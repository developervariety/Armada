---
type: "Reference"
title: "Legacy run-on-startup scripts (systemd --user, launchd, HKCU Run)"
description: "install-systemd-user.sh / install-launchd-agent.sh (com.armada.admiral) / install-windows-task.bat publish to ~/.armada/bin, deploy the dashboard, register startup, and health-check; update-* and remove-* siblings."
timestamp: "2026-10-05T05:30:47.5946260Z"
created: "2026-10-05T05:30:47.2805150Z"
slug: "run-on-startup-scripts"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["service-registration-flags", "server-rebuild-and-rollback", "source-install-scripts"]
version: "1"
salience: "0.5"
---
docs/RUN_ON_STARTUP.md. Scripts publish Armada.Server (Release, net10.0 default) into ~/.armada/bin, deploy the dashboard into ~/.armada/dashboard, register a user-scoped startup entry, and run healthcheck-server (polls /api/v1/status/health for about 30 s; set ARMADA_BASE_URL for a non-default port).
- Linux: scripts/linux/install-systemd-user.sh, update-systemd-user.sh, remove-systemd-user.sh; writes ~/.config/systemd/user/armada.service; `sudo loginctl enable-linger $USER` to start before login.
- macOS: scripts/macos/install-launchd-agent.sh, update-launchd-agent.sh, remove-launchd-agent.sh; writes ~/Library/LaunchAgents/com.armada.admiral.plist.
- Windows: scripts/windows/install-windows-task.bat, update-windows-task.bat, remove-windows-task.bat; HKCU\Software\Microsoft\Windows\CurrentVersion\Run entry (names say task but Task Scheduler is no longer used); start-armada-server.ps1 is slot-aware for rebuilds.
Remove scripts unregister but do not delete ~/.armada files. These predate --install-service; never use both for one install.
