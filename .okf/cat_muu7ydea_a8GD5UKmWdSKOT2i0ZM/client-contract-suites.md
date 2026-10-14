---
type: "Reference"
title: "Armada.Client contract and coverage suites"
description: "Client.Contract.* exercise ArmadaClient methods live against E2EServerFixture; Client.Destructive uses a recording stub; Client.Coverage and Client.RouteSurface keep client.ts, ArmadaClient, and api-surface-1.0.json in sync."
timestamp: "2026-10-05T05:29:04.6927020Z"
created: "2026-10-05T05:29:04.4614260Z"
slug: "client-contract-suites"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["api-surface-generator-and-contract-test", "stub-agent-runtimes-in-tests", "tui-parity-generator-and-suite"]
version: "1"
salience: "0.5"
---
src/Test.Shared/Suites/Client: Client.Contract (base), Client.Contract.Config, .Delivery, .Work, .AskPlanning, .Vessels call 314 of the 320 Armada.Client methods against the live in-process server (with StubCaptainRuntime where a captain must answer); Client.Destructive covers the remaining six (stop server, factory reset, and the like) against a recording stub handler; Client.Coverage fails when a client method has no test; Client.RouteSurface compares the routes the client calls with docs/api-surface-1.0.json, which caught client methods copied from api/client.ts that called routes the server does not have. Run with ARMADA_TEST_SUITES=Client. Add or update a contract case whenever you add an ArmadaClient method.
