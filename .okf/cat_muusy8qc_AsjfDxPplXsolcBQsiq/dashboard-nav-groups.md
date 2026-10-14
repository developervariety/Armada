---
type: "Reference"
title: "Dashboard navigation groups (navConfig.tsx)"
description: "Dashboard and Ask Armada top level, then OPERATIONS (Needs You, Planning, Dispatch, Fleet Actions, Missions), DELIVERY, BUILD (Vessels, Captains), CONFIGURATION, ACTIVITY (Activity, Jobs), SYSTEM (API Explorer, Settings /server)."
timestamp: "2026-10-05T05:40:58.2884980Z"
created: "2026-10-05T05:40:57.8197820Z"
slug: "dashboard-nav-groups"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["dashboard-routes-and-hubs", "tui-routing-and-hubs", "tui-parity-generator-and-suite"]
version: "1"
salience: "0.5"
---
src/Armada.Dashboard/src/components/navConfig.tsx defines dashboardItem ("Dashboard", /), askArmadaItem ("Ask Armada", top level, never nested: the primary way to drive Armada), and navSections: OPERATIONS (/inbox Needs You, /planning Planning, /dispatch Dispatch with a Backlog tab, /fleet-actions Fleet Actions, /missions Missions with Voyages and Merge Queue tabs), DELIVERY (/delivery hub: Deployments, Environments, Releases, Incidents, Checks, Runbooks), BUILD (/vessels hub including the workspace and health, /captains), CONFIGURATION (/configuration hub: Workflow Profiles, Project Profiles, Skills, Personas, Pipelines, Prompts, Playbooks, plus Endpoints, Harbors, Memory), ACTIVITY (/activity: requests, events, signals, history, token usage; /jobs), SYSTEM (/api-explorer, /server labeled Settings: server settings, diagnostics, tenants/users/credentials). Each item has to, label, tooltip, icon (navIcons.tsx). flattenNavCommands() feeds the command palette. navConfig.test.ts covers it. The TUI's Routing/NavCatalog.cs mirrors this file exactly, so changes here need the parity manifest regenerated.
