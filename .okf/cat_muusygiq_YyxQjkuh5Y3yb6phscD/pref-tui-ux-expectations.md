---
type: "Feedback"
title: "TUI UX expectations stated by the owner while testing"
description: "No forced password change, localhost prefill, wide editable Server picker, layout-ordered Tab, visible focus border, discoverable page size, logo on login, hub Left/Right."
timestamp: "2026-10-05T05:23:30.4778800Z"
created: "2026-10-05T05:23:30.2836000Z"
slug: "pref-tui-ux-expectations"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["decision-default-password-warn-not-block", "open-tui-focus-border"]
version: "1"
salience: "0.5"
---
Requests made 2026-10-04/05 while testing `armada tui`, each now implemented or in progress: login prefilled for a localhost Admiral (admin@armada, default password until first sign-in, local API key, settings port); the Server picker 50% wider and `e`/`E` edits a server; Tab order follows the visual layout; the TUI must NOT force a password change (warn only); the default server profile follows the local Admiral port; Ask captain selection must be explained step by step (focus the conversation, Esc, then c); page size controls must be obvious (footer spells out z/Z keys, Z picker); hub tab strip keeps focus so Left/Right keep switching tabs; ASCII Armada logo on the login screen; the focused section needs a noticeable border. General expectation: the TUI should be discoverable without reading docs and match the dashboard's capabilities.
