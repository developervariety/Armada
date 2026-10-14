---
type: "Reference"
title: "Harbor link wire protocol (HarborProtocol v1.0)"
description: "One JSON HarborMessage per WebSocket text frame, camelCase, polymorphic by \"type\": server sends handshakeAck, launch, stdin, kill, git, deferredLaunch; Harbor sends handshake, started, output, exited, gitResult, deferredLaunchAck, heartbeat, error; jobs keyed by jobId."
timestamp: "2026-10-05T05:41:42.8718300Z"
created: "2026-10-05T05:41:42.2357660Z"
slug: "arch-harbor-link-protocol"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-harbor-split-mode", "arch-server-rebuild", "term-harbor"]
version: "1"
salience: "0.5"
---
Contract: docs/HARBOR_PROTOCOL.md; classes src/Armada.Core/Harbor/*.cs; serializer Armada.Core.Harbor.HarborProtocol (Version "1.0"; bump on breaking change). camelCase, enums as strings, nulls omitted, "type" written first; optional correlationId and traceParent (W3C) on every message.

Server -> Harbor: handshakeAck (HarborHandshakeAck: accepted, mcpBaseUrl), launch (HarborLaunchRequest: jobId, runtime, workingDirectory, prompt, arguments, environment, optional HarborInferenceEndpoint), stdin (HarborStdinRequest), kill (HarborKillRequest: gracefulTimeoutMs, then kill tree), git (HarborGitRequest: requestId, executable git/gh, workingDirectory, arguments), deferredLaunch (HarborDeferredLaunchRequest: launchExePath, waitForPid, healthUrl, healthTimeoutSeconds, fallbackExePath, fallbackSlot, currentPointerPath).
Harbor -> server: handshake (harborId, name, protocolVersion, capabilities, maxConcurrentJobs), started (jobId, host processId; informational), output (jobId, stream stdout/stderr, data), exited (jobId, exitCode), gitResult (requestId, exitCode, standardOutput, standardError), deferredLaunchAck (armed), heartbeat (liveJobIds), error (HarborError).
Sequences: connect -> handshake/ack -> periodic heartbeat; run -> launch/started/output*/exited; git delegate -> git/gitResult; rebuild cutover -> deferredLaunch acked while the Admiral is alive, then the Harbor launches the new slot with ARMADA_RESTART_WAIT_PID, polls healthUrl, and on failure rewrites the current pointer to fallbackSlot. Reconnect: processes keep running; liveJobIds in the next heartbeat rebind them. Experimental for 1.0.
