---
type: "Reference"
title: "OS service registration: armada-server --install-service and Harbor --install-startup"
description: "armada-server --install-service [--dry-run] [--no-start] [--service-user u] / --uninstall-service / --run-service; armada-harbor --install-startup / --uninstall-startup; idempotent with exit codes 0-4."
timestamp: "2026-10-05T05:30:40.9743990Z"
created: "2026-10-05T05:30:40.0675350Z"
slug: "service-registration-flags"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["run-on-startup-scripts", "install-channels-1-0", "data-directory-and-env"]
version: "1"
salience: "0.5"
---
Admiral (src/Armada.Server Program, ServiceRegistrar, RegistrationDefaults):
- Linux: user scope ~/.config/systemd/user/armada.service (systemctl --user enable/start; enable linger for boot start); elevated: /etc/systemd/system/armada.service (runs as root unless --service-user <account>).
- macOS: ~/Library/LaunchAgents/com.joelchristner.armada.server.plist (user) or /Library/LaunchAgents/... for all users (what the .pkg does).
- Windows: elevated only (else exit 4); service "armada" ("Armada Admiral"), automatic, restart on failure 5 s/5 s/60 s, LocalSystem by default (data in %ProgramData%\Armada); change account with sc.exe config armada obj= .\<you> password= ...
--run-service is what the service manager passes (no banner, no colors; Windows SCM host). --dry-run prints the definition and commands. Idempotent: unchanged definitions are left alone, changed ones rewritten and restarted; uninstall of nothing succeeds. Exit codes: 0 done/nothing/dry run, 1 write or service command failed, 2 invalid arguments, 3 unsupported OS, 4 wrong privileges.
Harbor: armada-harbor --install-startup [--dry-run] / --uninstall-startup; refuses root on Linux/macOS; starts Harbor --minimized at login. Windows HKCU Run value "Armada Harbor"; macOS ~/Library/LaunchAgents/com.joelchristner.armada.harbor.plist (RunAtLoad, no KeepAlive); Linux ~/.config/autostart/armada-harbor.desktop.
Do not combine with the older RUN_ON_STARTUP scripts (agent com.armada.admiral) or two Admirals race for the ports. Windows paths are tested by command lines only.
