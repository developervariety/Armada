---
type: "Reference"
title: "TUI Ask Armada: AskController session, transcript, composer, dock"
description: "The TUI opens into Ask; AskController keeps one session (threads, draft, streaming reply) across screens; transcript renders tool chips, confirm and work cards, Markdown re-rendered per chunk; composer Enter sends, / quick actions, Ctrl+C or Esc twice stops."
timestamp: "2026-10-05T05:39:14.9706430Z"
created: "2026-10-05T05:39:14.3249060Z"
slug: "tui-ask-armada"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-approvals-center", "tui-frame-governor-performance", "pref-ask-armada-home-base", "ask-thread-token-precedence"]
version: "1"
salience: "0.5"
---
Ask/AskController.cs owns one session object (thread list, conversation reducer ported from the dashboard's lib/askConversation.ts, optimistic send, stop with an 8 s local force-end, read marking only while the screen shows and the terminal is focused, refetch on reconnect, ask.* events for every thread). AskConversation.MaxMessages caps memory at 10,000 messages (older ones via "Load earlier messages"). Screen: thread list on the left (server search, pinned first, unread counts, "* Working", "Replying...", [Archived]; Enter open, n new, / search, e rename, p pin, s summarize, Del delete, A archived, . menu; pages of 50; below 100 columns it folds and Ctrl+T shows it), conversation on the right (header: title, captain c, auto-approve Ctrl+Y with confirm and banner, Summarize s, More .; tracked-work strip "N active of M", w next), composer at the bottom (Enter send, Shift+Enter or Ctrl+J newline, Ctrl+E $EDITOR, Up recalls, / quick actions Dispatch, Fleet action, Status, Health, Import; Esc moves to the transcript). Transcript: Markdown replies with tool chips [ok] [..] [x!], collapsed thinking (t), metrics line (TTFT, tokens/s), confirm cards (a approve, r reject, x args, y copy), work cards (Enter mission, o PR, l log, d diff). AskTranscriptBuilder caches laid-out blocks per message. Choosing a captain: Esc to the transcript, then c (c types a letter in the composer), or palette "Choose captain...". Ctrl+J toggles the Ask dock on any screen; Alt+A starts a conversation about the current subject. A note appears when the captain has no Armada MCP access.
