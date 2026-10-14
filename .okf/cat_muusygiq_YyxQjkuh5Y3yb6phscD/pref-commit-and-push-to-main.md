---
type: "Feedback"
title: "Owner expects finished work committed and pushed to main"
description: "When a task is done and verified, commit and push to main; agents in worktrees never push, the coordinator merges after a full test run; don't push during a CI run you are waiting on."
timestamp: "2026-10-14T20:59:05.3456410Z"
created: "2026-10-05T05:22:43.9033830Z"
slug: "pref-commit-and-push-to-main"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["pref-worktrees-outside-code", "convention-parallel-agent-worktrees", "lesson-push-cancels-ci-run", "commit-message-format"]
version: "2"
salience: "0.5"
---
The owner's standing pattern (many sessions, confirmed 2026-10-04/05): when a task is done and verified, commit it and push to main. Feature work by parallel agents is done in git worktrees on work/<name> branches, located in the session scratchpad and never under ~/Code ([[pref-worktrees-outside-code]], corrected 2026-10-08), and never pushed by the agents; the coordinating agent merges each branch into main after running the full test suite, then pushes. Delete merged worktrees and branches afterwards. Exception: do not push while a CI run you are waiting on is in progress, because a push cancels the in-progress ci run (concurrency); commit locally and push after it finishes. Commit messages end with the Co-Authored-By trailer given by the session.
