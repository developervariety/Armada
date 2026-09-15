# Previously unregistered executable cases

This is the reviewed source inventory for the 20 suites added to the existing
executables. The review preceded repairs. ADD means retain the case body and
register it. REPAIR means repair the stated fixture or contract defect, retaining
the assertion. These are not skipped cases. No folder replacement is used.

There are 42 Unit cases and 43 API declarations. The API declarations include
three setup cases and one cleanup case. Those lifecycle records remain visible;
they are not additional feature assertions. Existing registered suites remain
owned by their original executables.

Later verification found two review-dispatch omissions, terminal review dock
reclamation, health URL resolution, and six preview fields lost in persistence.
These causes are repaired with focused regressions. The review below is source
provenance; current execution results belong in [test discovery](test-discovery.md).

Case names corrected during repair:

- `UpdateAsync clears previous default on same vessel` becomes `CreateAsync clears previous default on same vessel`; the body calls CreateAsync.
- `Single-stage reviewed Worker pipeline retains dock until approval and then lands` becomes `Single-stage review retains dock until approval invokes completion callback`; this fixture does not prove actual landing gates.
- `BacklogRefinementRoutes_CreateSendSummarizeApplyAndDelete` becomes `BacklogRefinementRoutes_RecordRuntimeFailureSummarizeApplyAndDelete`; it verifies the runtime failure transcript and fallback, not live provider refinement.

The initial review overestimated captain model validation: a blank model returns
without a provider probe. The session fixture creates a supported captain with
an explicit blank model, then changes it to Custom before sending a message.
The Custom enum cannot construct a runtime. The linkage-only captain is also
Custom and is stopped and deleted during verified cleanup.

## Unit source review

## RequestHistoryDatabaseTests

Source: `test/Armada.Test.Unit/Suites/Database/RequestHistoryDatabaseTests.cs`. SHA256: `024c2c6c8b71dfa903161778f216a02ce31704ddcdd7013a4db167b5b5626949`.

Dependencies: SQLite TestDatabaseHelper; raw request-history persistence.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| CreateAsync and ReadAsync persist entry detail | 14 | ADD | ADD: round-trip entry/detail IDs and body. Synthetic tenant/user are acceptable for the SQLite request-history schema; this is not ownership authorization proof. |
| EnumerateAsync filters by route and success state | 33 | ADD | ADD: two contrasting rows and exact matching IDs give behavioral filter coverage. |
| DeleteByFilterAsync removes matching request-history rows | 64 | ADD | ADD: old/new dates straddle cutoff and both deletion and retention are read back. |

## ObjectiveModelTests

Source: `test/Armada.Test.Unit/Suites/Models/ObjectiveModelTests.cs`. SHA256: `2c62d89e1cbd255f3c8ea0fbf1ea8ab6782487427d9a2b9162d223c807ec3662`.

Dependencies: Models and System.Text.Json only.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| Objective DefaultConstructor GeneratesIdWithPrefixAndBacklogDefaults | 15 | ADD | ADD: assertions match current defaults. Extend this same case with AutoDispatchEnabled=false and empty Preparation if desired; current checks do not certify those fork additions. |
| Objective IdAndTitle TrimWhitespace | 32 | ADD | ADD: current Objective setters trim both properties. |
| Objective IdAndTitle RejectWhitespace | 44 | ADD | ADD: current setters throw ArgumentNullException for whitespace. Keep exact exception type. |
| Objective Serialization RoundTripsBacklogFields | 51 | ADD | ADD: Scoped and ReadyForPlanning remain valid enum values; no source mismatch. Does not round-trip Preparation/StartFromRef/AutoDispatchEnabled. |

## ObjectiveRefinementModelTests

Source: `test/Armada.Test.Unit/Suites/Models/ObjectiveRefinementModelTests.cs`. SHA256: `91ad5cdda0efd55c4551d871f9b4b6358357a5e8bf8022660a00d66b792d6d78`.

Dependencies: Models and System.Text.Json only.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| ObjectiveRefinementSession DefaultConstructor SetsExpectedDefaults | 14 | ADD | ADD: current constructor defaults and ors_ identity are covered. |
| ObjectiveRefinementMessage DefaultConstructor SetsExpectedDefaults | 28 | ADD | ADD: current role/sequence/content/selection defaults are covered. |
| ObjectiveRefinementModels SerializeAndDeserialize | 41 | ADD | ADD: checks both independent DTO round-trips; not database persistence. |

## DeploymentEnvironmentServiceTests

Source: `test/Armada.Test.Unit/Suites/Services/DeploymentEnvironmentServiceTests.cs`. SHA256: `6a86693aea6ffc259824f75967e9e6ff8ccd325cf0bd7f1c24bc45e6e9efc630`.

Dependencies: SQLite; WorkflowProfileService; temporary work directory; no deploy command execution.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| CreateAsync creates environment for accessible vessel | 25 | ADD | ADD: own-user vessel fixture and auth match current service ownership boundary. |
| UpdateAsync clears previous default on same vessel | 65 | REPAIR | REPAIR: body calls CreateAsync twice and never UpdateAsync. Rename to creation replacing a default, or actually update the second existing environment. Do not claim Update coverage from this body. |
| CreateAsync rejects inaccessible vessel | 111 | ADD | ADD: nonexistent vessel must remain rejected; manual try/catch tests the service exception. |
| SeedDefaultsAsync seeds workflow profile environments without duplication | 141 | ADD | ADD: two seed passes assert count/default and production approval requirement; current service preserves this behavior. |
| SeedDefaultsAsync creates fallback development environment when no profile exists | 196 | ADD | ADD: current fallback is Development/default. This does not assert deployment commands exist. |

## DeploymentServiceTests

Source: `test/Armada.Test.Unit/Suites/Services/DeploymentServiceTests.cs`. SHA256: `d8232b6ea290b211b2f09837bcd885917f4deabf0ce2f65e9f68d8a56bbc2036`.

Dependencies: SQLite; WorkflowProfile/Readiness/CheckRun/Environment/Deployment services; temporary working directory; local shell echo; HostWideCommandLock.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| CreateAsync auto executes deployment and records skipped verification when no checks are configured | 24 | ADD | ADD: local echo deploy command; checks Succeeded plus Skipped verification and persisted deploy Check. IsIsolatedCheckoutType only covers Build/UnitTest, so the temporary non-Git directory is valid for this Deploy case. Do not relabel Skipped as verified. |
| ApproveAsync executes pending deployments and DenyAsync marks them denied | 97 | ADD | ADD: production fixture waits for approval; same-user auth then executes local echo. Retain pending/no-Check assertions and denied completion state. |

## HistoricalTimelineServiceTests

Source: `test/Armada.Test.Unit/Suites/Services/HistoricalTimelineServiceTests.cs`. SHA256: `415528159b21c202f7eb1cd8f436e25c2f5c47fcd6f3e2cb14e4857dc6defd1c`.

Dependencies: SQLite; local entity fixtures; HistoricalTimelineService; IncidentService stores events in the same test database.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| EnumerateAsync aggregates current Armada entities into one timeline | 24 | ADD | ADD: fixture data creates voyage/mission/Check/release/request entries; >=5 allows richer fork entries rather than pinning a global count. |
| EnumerateAsync filters by vessel, actor, source type, and text | 147 | ADD | ADD: positive and contrasting Check rows test combined filtering. |
| EnumerateAsync can narrow to postmortem-linked incident context | 211 | ADD | ADD: current service reads event-backed incidents and linked deployment/release IDs. This is query coverage, not incident closeout operational proof. |
| EnumerateAsync includes backlog refinement sessions and routes them to backlog detail | 371 | ADD | ADD: current HistoricalTimelineService still maps ObjectiveRefinementSession to /backlog/{objectiveId}. SQLite refinement is implemented. |

## ProxyAuthServiceTests

Source: `test/Armada.Test.Unit/Suites/Services/ProxyAuthServiceTests.cs`. SHA256: `a1525bbf6c2c432f68498bd1625ceafdd0ece04e9da1bc95d54845bb94f781b1`.

Dependencies: ProxySettings/ProxyAuthService; nonce and in-memory session; no network.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| TryLogin CreatesSessionAndSupportsInstanceSelection | 14 | ADD | ADD: valid nonce proof, validate session, select/read/clear local instance state. |
| TryLogin RejectsInvalidProof | 40 | ADD | ADD: invalid proof denial with reason. |

## ProxyDashboardRelayIntegrationTests

Source: `test/Armada.Test.Unit/Suites/Services/ProxyDashboardRelayIntegrationTests.cs`. SHA256: `f3cb3f1848f6350842c0b594e29a04a1c7cb19ed4ac9e043543fae0bdc6ee9fa`.

Dependencies: Loopback ArmadaProxyServer; FakeTunnelClient; HTTP/WebSocket; temporary proxy data; shared output assets currently mutated.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| PortalRequiresAuthAndSelectionBeforeDashboard | 24 | REPAIR | REPAIR: shared proxy fixture must isolate/restore static assets and clean up partial startup. Assertions cover minimal synthetic portal/dashboard fixtures, not current production UI content. |
| ProxyRelaysApiWebSocketAndReconnectBehavior | 71 | REPAIR | REPAIR: same fixture isolation/partial-startup cleanup. Fake tunnel handles representative APIs; no real planning/mission/deployment is dispatched. Retain zero forwarded requests for blocked restore. |
| ProxyWebSocketRequiresAuthAndSelection | 156 | REPAIR | REPAIR: same fixture repair. Current test expects an accepted WebSocket followed by close; keep source route behavior aligned and do not weaken authentication if actual handshake policy differs. |
| ProxyRejectsDeploymentsWithoutDashboardApiRelayCapability | 174 | REPAIR | REPAIR: same fixture repair. HTTP relay capability rejection and nonpersistent selection remain valid current contracts. |
| ProxySessionContextReflectsSelectedInstanceRelayCapabilities | 203 | REPAIR | REPAIR: same fixture repair. HTTP-only capability must report no WebSocket and close WebSocket use. |

## ProxyRoutePolicyServiceTests

Source: `test/Armada.Test.Unit/Suites/Services/ProxyRoutePolicyServiceTests.cs`. SHA256: `0f6a33c4113f62826bf79bce7762aee686375577ec0d9a33237358acbb005b7e`.

Dependencies: ProxyRoutePolicyService; request models only; no network.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| TryAuthorize AllowsRegularDashboardReadRoutes | 13 | ADD | ADD: regular fleet GET allowed by current proxy policy. |
| TryAuthorize DeniesHighRiskAdministrativeWrites | 26 | ADD | ADD: PUT settings and POST restore denial are complementary to route authorization configuration tests. |
| TryAuthorize AllowsRemoteLoginBootstrapRoutes | 49 | ADD | ADD: tenant lookup/authentication bootstrap routes remain supported. |

## RemoteDashboardRelayServiceTests

Source: `test/Armada.Test.Unit/Suites/Services/RemoteDashboardRelayServiceTests.cs`. SHA256: `3d810c3328eb59b548899b00efeaac028472ceeb1fb32f8434b71f12e4dcdf0b`.

Dependencies: RemoteDashboardRelayService; reflection for first case; local HttpListener/WebSocket for remaining cases.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| ResolveLoopbackHost MapsLocalhostToIpv4Loopback | 23 | ADD | ADD: private method still exists and maps localhost to IPv4 loopback. Reflection checks one helper; behavioral HTTP cases remain required. |
| HandleAsync RelaysHttpMethodsBodiesAndErrors | 38 | ADD | ADD: local host covers method/query/header/body/binary/error and request/response size limits. No external endpoint; requires loopback bind support. |
| HandleAsync RelaysWebSocketMessagesAndRemoteClose | 164 | ADD | ADD: local host/collector checks echo, close reason and missing closed-session behavior. Require complete host/relay cleanup before another case. |

## RequestHistoryCaptureServiceTests

Source: `test/Armada.Test.Unit/Suites/Services/RequestHistoryCaptureServiceTests.cs`. SHA256: `b68aaedc2ab2bdbfb8cb3862d0dc1a2b68e2a93a05e2c66ae9c7361de355a546`.

Dependencies: RequestHistoryCaptureService; ArmadaSettings; JSON/text only.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| BuildRecord redacts headers, query params, and JSON body secrets | 14 | ADD | ADD: current service uses JsonDefaults.Indented, so the whitespace-sensitive token assertion matches source. Prefer typed dictionary assertion during future edits; do not remove redaction checks. |
| BuildRecord omits binary bodies and truncates oversized text | 64 | ADD | ADD: setting permits 16 bytes; binary omission and truncation suffix match current implementation. |
| ShouldCapture respects API-only and exclusion rules | 88 | ADD | ADD: API-only/excluded health/history routes match current capture contract. |

## ReviewGateTests

Source: `test/Armada.Test.Unit/Suites/Services/ReviewGateTests.cs`. SHA256: `e7e25de4835df598897612ceaee912f8ef7f28929d2678b2b897ff5079d1aecf`.

Dependencies: SQLite; real Mission/Captain/Admiral/Dock services; DirCreatingGitStub; fake launch callback; temporary dock/repo/work directories.

| Case | Line | Disposition | Review |
| --- | ---: | --- | --- |
| Reviewed stage enters Review and blocks downstream dispatch | 49 | REPAIR | REPAIR fixture cleanup: temporary docks/repos/work paths have no disposal cleanup. Retain review wait, captain release, inactive nonterminal dock and unassigned downstream assertions. |
| Approving review completes reviewed stage and assigns downstream mission | 75 | REPAIR | REPAIR fixture cleanup. Current ApproveReviewAsync completes upstream, performs handoff and dispatches pending. Keep branch and prior-output assertions; do not replace approved persona/model/routing rules if fixture needs adjustment. |
| Denying review retries same stage on existing branch with feedback | 105 | REPAIR | REPAIR fixture cleanup. Preserve same-branch retry, reviewer/comment and downstream blocking; allow runtime verification to expose richer recovery requirements rather than changing service to smaller upstream behavior. |
| Denying review can fail pipeline and cancel downstream stages | 134 | REPAIR | REPAIR fixture cleanup. Complementary to WaitingForInputLifecycleTests and terminal branch-reap tests, not an exact duplicate: this case begins with dispatch/completion review transition. |
| Single-stage reviewed Worker pipeline retains dock until approval and then lands | 160 | REPAIR | REPAIR fixture cleanup and claim/name: OnMissionComplete is replaced with a direct Complete database write. Assert callback/reclaim orchestration, not actual immutable landing/Check acceptance; retain independent landing gate suites. |

## Cross-case repair details

- ProxyDashboardRelayIntegrationTests.cs:483-497 writes AppContext.BaseDirectory/wwwroot and dashboard files. DisposeAsync at456 only removes the proxy data directory. Isolate fixture assets through a supported root, or snapshot/restore exact prior files in a finally block and serialize access. StartAsync at333 allocates and starts resources before returning; if startup fails or WaitAsync times out, the caller has no harness to dispose. Add cleanup for partial construction and cancellation-aware startup.
- ReviewGateTests.CreateSettings/CreateScenarioAsync allocate unique temporary directories and services; returned ReviewScenario is not disposable. Add explicit cleanup after each case. Its Git stub returns empty diff/changed-file results and fixed HEAD; it does not prove a real commit or landing. Output length currently exceeds the implementation no-op acknowledgment threshold, so do not assume a no-op failure without running the case.
- DeploymentEnvironmentServiceTests second case tests default replacement through creation, not update. This is a verified coverage-label mismatch.
- Do not replace production contracts to fit fixtures. Keep immutable Check/landing tests, routing constraints, recovery, process ownership and disabled automatic dispatch tests registered.

## Existing coverage retained

- WaitingForInputLifecycleTests: review denial across waiting lifecycle states.
- MissionServiceTerminalBranchReapTests: denial-triggered branch cleanup.
- MissionDatabaseTests and PipelineServiceTests: persisted review gate fields.
- AuthorizationConfigTests: request-history route auth configuration.
- ProxyRegistryTests: proxy instance registry behavior.
- MissionReviewDiffTests and ReviewDiffScopeTests: scoped review diff behavior.

These are adjacent assertions/entry points, not proven exact duplicates of the 42 omitted cases. Test.Shared descriptor copies belong to a different execution surface and are not grounds to silently exclude fork Unit cases.

Totals: {'ADD': 31, 'REPAIR': 11}. No case executions were performed.

## API source review

## DeploymentTests

Source: `test/Armada.Test.Automated/Suites/DeploymentTests.cs`. Constructor: `HttpClient authClient, HttpClient unauthClient, string baseUrl`.

| Case and source line | Disposition | Contract and dependencies |
|---|---|---|
| `Deployments_CreateReadListVerifyRollbackApproveAndDeny` (:50) | add | Add existing assertions. Echo Deploy/Smoke/Verification/Rollback do not require isolated Build/UnitTest checkout. Requires local server health URL accessible without the auth client; keep approval, denial and appended check evidence. |
| `Deployments_CreateWithoutAuthReturns401` (:249) | add | Add unauthenticated HTTP 401 assertion. Must use the unauthenticated client; no successful entity fixture is required. |

## EnvironmentTests

Source: `test/Armada.Test.Automated/Suites/EnvironmentTests.cs`. Constructor: `HttpClient authClient, HttpClient unauthClient`.

| Case and source line | Disposition | Contract and dependencies |
|---|---|---|
| `Environments_CreateListReadUpdateAndDelete` (:39) | add | Add CRUD assertions. C:/temp path is inert metadata here; portable fixture path preferable, but no command executes. |
| `Environments_CreateWithoutAuthReturns401` (:121) | add | Add unauthenticated HTTP 401 assertion. Must use the unauthenticated client; no successful entity fixture is required. |

## GitHubIntegrationTests

Source: `test/Armada.Test.Automated/Suites/GitHubIntegrationTests.cs`. Constructor: `HttpClient authClient, HttpClient unauthClient, string armadaBaseUrl`.

| Case and source line | Disposition | Contract and dependencies |
|---|---|---|
| `GitHubObjectives_ImportAndRefreshFromIssue` (:42) | add | Add with local FakeGitHubServer. Preserve issue identity, refresh-to-Completed and vessel credential behavior; no real GitHub token needed. |
| `GitHubActions_SyncCreatesAndUpdatesDeploymentLinkedChecks` (:105) | add | Add local fake actions import. Preserve created=1 then updated=1 idempotence and deployment links. Imported abc123 is external evidence, not proof of local landing eligibility. |
| `GitHubPullRequestRoutes_ReturnMissionAndReleaseEvidence` (:192) | add | Add local fake PR evidence. Mission fixture must stay undispatched; no captain assignment. Preserve changes-requested review and check details. |
| `GitHubObjectiveImport_WithoutAuthReturns401` (:240) | add | Add unauthenticated HTTP 401 assertion. Must use the unauthenticated client; no successful entity fixture is required. |

## IncidentTests

Source: `test/Armada.Test.Automated/Suites/IncidentTests.cs`. Constructor: `HttpClient authClient, HttpClient unauthClient, string baseUrl`.

| Case and source line | Disposition | Contract and dependencies |
|---|---|---|
| `Incidents_CreateReadUpdateFilterAndDeletePreserveContext` (:49) | add | Add full context assertions. Echo deployment/rollback fixture is valid for non-Build types; keep linked IDs after sparse update. |
| `Incidents_CreateWithoutAuthReturns401` (:207) | add | Add unauthenticated HTTP 401 assertion. Must use the unauthenticated client; no successful entity fixture is required. |

## ObjectiveTests

Source: `test/Armada.Test.Automated/Suites/ObjectiveTests.cs`. Constructor: `HttpClient authClient, HttpClient unauthClient`.

| Case and source line | Disposition | Contract and dependencies |
|---|---|---|
| `Objectives_CreateListReadUpdateDeleteAndHistoryFilter` (:47) | add | Add current CRUD/history contract, including CompletedUtc and evidence URL. |
| `Objectives_CreateWithoutAuthReturns401` (:101) | add | Add unauthenticated HTTP 401 assertion. Must use the unauthenticated client; no successful entity fixture is required. |
| `BacklogAlias_CreateReadReorderAndDelete` (:111) | add | Add alias and dispatch-preview equivalence. brief_method_missing and target_vessel_count are current service issue codes. Set AutoDispatchEnabled=false explicitly on created objective. |
| `BacklogRefinementRoutes_CreateSendSummarizeApplyAndDelete` (:194) | repair | Repair test dependency on real ClaudeCode captain validation and asynchronous coordinator. Custom factory throws by design; current coordinator stores Refinement response failed and fallback copies that text. Thus existing assertions exercise error fallback, not successful AI refinement. Use controlled test runtime or explicit negative-path fixture; do not invoke real provider/planning runtime or weaken validation. |
| `Objectives_VoyageAndReleaseCreationLinkBackToObjective` (:299) | add | Add linkage assertions with all automatic dispatch disabled. Current ObjectiveService promotes Released on release linkage, even this draft release; do not change expected state merely because label says draft. |

## ReleaseTests

Source: `test/Armada.Test.Automated/Suites/ReleaseTests.cs`. Constructor: `HttpClient authClient, HttpClient unauthClient`.

| Case and source line | Disposition | Contract and dependencies |
|---|---|---|
| `Releases_CreateListReadUpdateRefreshAndDelete` (:49) | add | Add existing version/artifact/voyage assertions. ReleaseVersioning is not isolated Build/UnitTest; non-Git directory does not alone break this case. Keep local command output and no scheduler dispatch. |
| `Releases_CreateWithoutAuthReturns401` (:164) | add | Add unauthenticated HTTP 401 assertion. Must use the unauthenticated client; no successful entity fixture is required. |

## RequestHistoryTests

Source: `test/Armada.Test.Automated/Suites/RequestHistoryTests.cs`. Constructor: `HttpClient authClient, HttpClient unauthClient, string baseUrl`.

| Case and source line | Disposition | Contract and dependencies |
|---|---|---|
| `Setup_CreateTenantAAdmin` (:210) | repair | Repair lifecycle classification: setup establishes tenant A and admin credential. Must run before dependent cases; not standalone business coverage. |
| `Setup_CreateTenantAUser` (:221) | repair | Repair lifecycle classification: requires tenant A from prior setup. Must not execute alone under a case filter. |
| `Setup_CreateTenantBAdmin` (:232) | repair | Repair lifecycle classification: establishes independent tenant B credential for negative scope checks. |
| `RequestHistory_ListWithoutAuth_Returns401` (:247) | add | Add unauthenticated HTTP 401 assertion. Must use the unauthenticated client; no successful entity fixture is required. |
| `RequestHistory_CapturesStatusRequest_AndRedactsAuthorizationHeader` (:253) | add | Add capture assertions. Requires capture enabled, body/header detail retained and asynchronous capture completion. Creates _TenantAdminTrace used by later scope cases. |
| `RequestHistory_CapturesAuthenticateFailure_AndRedactsBodySecrets` (:286) | add | Add failed-auth capture and secret redaction; unauthenticated request intentionally has no identity. Preserve both 401 and absence of original secret. |
| `RequestHistory_Summary_ReturnsBucketedCounts` (:308) | add | Add bucket/count assertions with preceding captures. Empty/filtered standalone run cannot establish its >=1 precondition. |
| `RequestHistory_CapturesReleaseAndHistoryRoutes` (:329) | add | Add both route capture assertions with tenant admin fixture. |
| `RequestHistory_CapturesBacklogAndRefinementRoutes` (:357) | repair | Repair real ClaudeCode captain validation dependency; controlled captain setup needed. It creates session but sends no model request. Preserve request-body and auth-redaction assertions. |
| `RequestHistory_RedactsGitHubTokenOverrideInVesselPayload` (:435) | add | Add redaction assertions. Track/delete created vessel and fleet explicitly; current case leaves both for tenant cleanup. |
| `RequestHistory_Scope_RegularUser_OnlySeesOwnEntries` (:478) | repair | Repair capture race: polls tenant B entry, but does not poll tenant A user entry before asserting it in list. Explicitly await both captures. Produces _OtherTenantEntryId and traces for next cases. |
| `RequestHistory_Scope_TenantAdmin_SeesTenantNotOtherTenant` (:509) | add | Add scope assertions, retaining ordered dependencies on status capture and regular-user case. Current route forces tenant and leaves user unconstrained for tenant admin. |
| `RequestHistory_Scope_TenantAdmin_CannotReadOtherTenantEntry` (:526) | add | Add 404 concealment assertion. Requires exact other-tenant ID created in prior case; an unset ID can test wrong route. |
| `RequestHistory_Scope_GlobalAdmin_CanFilterByTenant` (:532) | add | Add tenant filter assertions; admin client must be global admin and prior two tenant A captures must exist. |
| `RequestHistory_DeleteSingle_RemovesEntry` (:549) | add | Add delete/read-404 assertions after capture poll. |
| `RequestHistory_DeleteMultiple_RemovesEntries_AndSkipsUnknown` (:568) | add | Add exact deleted=2/skipped=1 assertions after both capture polls. |
| `RequestHistory_DeleteByFilter_RemovesScopedEntries` (:599) | repair | Repair async capture race: await both newly invoked status entries before delete. Current immediate filter delete can return fewer than 2 or allow queued writes to recreate apparent survivors. |
| `Cleanup_DeleteRequestHistoryTenants` (:640) | repair | Repair lifecycle classification and verification. Put cleanup in guaranteed teardown; current RunTest cleanup has no response assertions and can report pass with failed deletions. Preserve cleanup after failed prior cases. |

## WorkflowProfileCheckRunTests

Source: `test/Armada.Test.Automated/Suites/WorkflowProfileCheckRunTests.cs`. Constructor: `HttpClient authClient, HttpClient unauthClient`.

| Case and source line | Disposition | Contract and dependencies |
|---|---|---|
| `WorkflowProfiles_CreateResolveUpdateAndEnumerate` (:48) | add | Add profile assertions; establishes vessel/profile IDs for later cases. Preserve preview Build and environment command selection. |
| `WorkflowProfiles_ValidateRejectsEmptyProfile` (:151) | add | Add 200 validation response with IsValid=false; validation failure is not HTTP failure. |
| `VesselReadiness_And_CheckRun_Block_When_Required_Input_Is_Missing` (:166) | add | Add missing-input assertions: Build must report unscoped missing variable, not staging-only variable. OnePassword is required-input metadata; do not fetch real secret. |
| `WorkflowProfiles_ValidateRejectsUnknownEnvironmentScopedInputs` (:256) | add | Add unknown environment validation assertions; keep invalid profile response semantics. |
| `VesselReadiness_And_LandingPreview_Surface_Setup_Metadata` (:291) | add | Add setup/readiness assertions. Requires first profile case setup. Passing-check-required issue before a run is supported by current preview service. |
| `CheckRuns_RunReadRetryListAndDelete` (:313) | repair | Repair definite fixture failure: initialize actual local Git repo, commit artifact, create requested branch and send full real commit ID instead of abc123. Existing plain directory cannot produce isolated checkout, so returned check fails. Preserve passing check, retry, artifact and delete assertions. Preview with no source branch accepts any passing vessel run today; add explicit branch context, and do not claim this tests actual immutable landing gate. |
| `CheckRuns_RunParsesStructuredSummaries` (:374) | repair | Repair definite fixture failure: commit summary and coverage files into actual Git fixture before UnitTest check, then request that commit. Files only written into live directory are absent in detached checkout; preserve 5 passed and 80 percent coverage expectations. |
| `CheckRuns_RunWithoutAuthReturns401` (:423) | add | Add unauthenticated HTTP 401 assertion. Must use the unauthenticated client; no successful entity fixture is required. |

## Inventory total

43 executable RunTest declarations across 8 omitted suites. This includes 3 setup declarations and 1 cleanup declaration, which must remain visible as lifecycle work and must not inflate feature coverage. Repair rows: 10. Add rows: 33. No duplicate or blocked disposition is asserted without execution evidence.

## Shared runner failure inventory

Before this change the shared runner reported 2,478 cases: 2,095 passed and 383 failed, with no skips. The reproducible command is in [Testing](../TESTING.md#reproducing-the-inventory). Every failing case is accounted for below; none was deleted.

| Failure family | Failing cases | Classification | Resolution | Passes now | Duplicate record | Awaiting owner |
|---|---:|---|---|---:|---:|---:|
| Fleet capacity admission returned 409, and follow-up reads returned NotFound (`E2E.Mission` 57, `E2E.Voyage` 59, `E2E.Event` 2, `E2E.Status` 1) | 119 | Fixture dependency | The end-to-end server uses the automated runner's scheduler and capacity settings; mission and voyage suites cancel each case's active work | 119 | 0 | 0 |
| MCP requests without the event-stream accept header, unprefixed tool names, unpaginated tool lists, unresolved job handles (`E2E.McpTool`) | 120 | Stale copy | Streamable HTTP accept header and event parsing, served tool names, cursor pagination, job resolution | 116 | 2 | 2 |
| Unauthenticated WebSocket sessions (`E2E.WebSocket` 96, `E2E.PlanningWebSocket` 2) | 98 | Stale copy | Each session authenticates before any other route | 95 | 1 | 2 |
| Review-gate fixture raced dispatch's queued assignment work and emitted no structured result (`Services.ReviewGate`) | 3 | Stale copy | The fixture awaits the admiral's queued-assignment drain and emits a structured result | 3 | 0 | 0 |
| Planning inactivity default asserted from an unused constant (`Services.Settings`) | 2 | Stale copy | Assertion aligned with the executed legacy settings case | 2 | 0 | 0 |
| A REST vessel update that omitted the token override erased the stored override (`E2E.Vessel`) | 1 | Product defect | Omitted means unchanged | 1 | 0 | 0 |
| Request-history buckets wider than an hour floored by minute-of-hour (`Services.RequestHistorySummaryBuilder`) | 1 | Product defect | One epoch-grid rule shared with token-usage summaries | 1 | 0 | 0 |
| Contract drift in copies of executed legacy cases, and behaviour the fork does not implement (remaining suites) | 39 | Duplicate of executed legacy case, or owner decision | Named, counted skip recorded below | 0 | 31 | 8 |
| **Total** | **383** | | | **337** | **34** | **12** |

The review-gate race is timing-dependent: before the fix, between three and all five of its cases failed across runs. After the change the shared runner reports no failures and 46 named skips. It also adds four cases that prove a disposition record cannot hide a case. The owner later decided six of the eight owner-decision cases (persona seeding and the Model Context Updates section) as intentional fork differences, and two more cases prove that kind reports distinctly and still fails discovery when stale. The table below is generated from `src/Test.Shared/Infrastructure/SharedCaseDispositions.cs`; that file is the source of truth.

| Shared case | Disposition | Owner | Reason |
|---|---|---|---|
| `E2E.LandingPipeline.pull_request_open_transitions_to_complete` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/LandingPipelineTests.cs` :: PullRequestOpen_RejectsCompleteWithoutLandingProof | manual completion now requires landing proof, so the copy's unlanded Complete is refused with 409 |
| `E2E.LandingPipeline.manual_complete_no_dock_emits_audit_event` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/LandingPipelineTests.cs` :: ManualComplete_NoDock_RejectsUnlandedCode | manual completion now requires landing proof, so the copy's unlanded Complete is refused with 409 |
| `E2E.McpTool.armada_enumerate_missions_with_status_filter` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/McpToolTests.cs` :: ArmadaEnumerate_Missions_WithStatusFilter | idle captains claim the copy's assignable missions before the Pending filter runs |
| `E2E.McpTool.armada_purge_voyage_deletes_voyage_and_missions` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/McpToolTests.cs` :: ArmadaPurgeVoyage_DeletesVoyageAndMissions | voyage status returns slim mission records whose description is a truncation envelope |
| `E2E.Objective.objectives_voyage_and_release_creation_link_back_to_objective` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/ObjectiveTests.cs` :: Objectives_VoyageAndReleaseCreationLinkBackToObjective | linkage needs a real local Git vessel, a workflow profile and verified cleanup |
| `E2E.Release.create_list_read_update_refresh_and_delete` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/ReleaseTests.cs` :: Releases_CreateListReadUpdateRefreshAndDelete | release creation needs a real local Git repository URL |
| `E2E.WebSocket.transition_mission_status_to_complete_sets_completed_utc` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/WebSocketTests.cs` :: TransitionMissionStatus_ToComplete_SetsCompletedUtc | manual completion now requires landing proof, which a report-only mission satisfies |
| `E2E.Workflow.voyage_workflow_create_and_cancel` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/WorkflowTests.cs` :: VoyageWorkflow_CreateAndCancel | a dispatched voyage runs as Open, not InProgress |
| `E2E.WorkflowProfileCheckRun.check_runs_run_read_retry_list_and_delete` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/WorkflowProfileCheckRunTests.cs` :: CheckRuns_RunReadRetryListAndDelete | check runs use an isolated checkout, so the fixture must commit its files and send a real commit |
| `E2E.WorkflowProfileCheckRun.check_runs_run_parses_structured_summaries` | Duplicate of executed legacy case | `test/Armada.Test.Automated/Suites/WorkflowProfileCheckRunTests.cs` :: CheckRuns_RunParsesStructuredSummaries | check runs use an isolated checkout, so the fixture must commit its files and send a real commit |
| `Runtimes.ClaudeCodeRuntime.delivers_prompt_via_stdin_not_argument` | Duplicate of executed legacy case | `test/Armada.Test.Runtimes/Suites/ClaudeCodeRuntimeTests.cs` :: BuildArguments_PromptContainsRolePreamble | the fork passes the prompt as the final argument instead of stdin |
| `Runtimes.ClaudeCodeRuntime.build_arguments_omits_stream_json_by_default` | Duplicate of executed legacy case | `test/Armada.Test.Runtimes/Suites/ClaudeCodeRuntimeTests.cs` :: BuildArguments Includes Model When Supplied | the fork always requests stream-json output |
| `Runtimes.CodexRuntime.build_arguments_uses_exec_with_platform_appropriate_auto_mode` | Duplicate of executed legacy case | `test/Armada.Test.Runtimes/Suites/CodexRuntimeTests.cs` :: BuildArguments Uses Exec With Platform Appropriate Auto Mode | the sandbox bypass applies on Linux as well as Windows, and the prompt is the final argument |
| `Runtimes.CodexRuntime.build_arguments_dangerous_uses_dangerous_flag` | Duplicate of executed legacy case | `test/Armada.Test.Runtimes/Suites/CodexRuntimeTests.cs` :: BuildArguments Dangerous Uses Dangerous Flag | the fork passes the prompt as the final argument instead of stdin |
| `Runtimes.CodexRuntime.delivers_prompt_via_stdin_not_argument` | Duplicate of executed legacy case | `test/Armada.Test.Runtimes/Suites/CodexRuntimeTests.cs` :: BuildArguments_PromptContainsRolePreamble | the fork passes the prompt as the final argument instead of stdin |
| `Runtimes.CursorRuntime.build_arguments_uses_non_interactive_text_output` | Duplicate of executed legacy case | `test/Armada.Test.Runtimes/Suites/CursorRuntimeTests.cs` :: BuildArguments Uses NonInteractiveStructuredOutput | the fork runs cursor-agent with --print and stream-json output |
| `Runtimes.GeminiRuntime.build_arguments_uses_approval_mode_and_stdin_prompt` | Duplicate of executed legacy case | `test/Armada.Test.Runtimes/Suites/GeminiRuntimeTests.cs` :: BuildArguments Uses Prompt And ApprovalMode | the fork passes the prompt with -p and requests stream-json output |
| `Runtimes.MuxRuntime.build_arguments_includes_endpoint_config_and_final_message_artifact` | Duplicate of executed legacy case | `test/Armada.Test.Runtimes/Suites/MuxRuntimeTests.cs` :: BuildArguments Uses Current Mux Run Contract | the fork targets the current Mux run contract |
| `Services.AdmiralService.handle_process_exit_async_stalls_captain_on_non_retryable_runtime_failure` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/AdmiralServiceTests.cs` :: HandleProcessExitAsync RateLimitFailure RequeuesMissionWithoutHaltingVoyage | a provider limit now requeues the mission and quarantines the captain instead of failing the voyage |
| `Services.AuthenticationService.apikey_valid_key_authenticated` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/AuthenticationServiceTests.cs` :: AuthenticateAsync ApiKey ValidKey ReturnsAuthenticated | API-key authentication resolves to the default tenant, not the system tenant |
| `Services.GitService.create_worktree_async_dirty_tracked_files_throws_and_cleans_up` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/GitServiceTests.cs` :: CreateWorktreeAsync DirtyTrackedFiles Throws And Cleans Up | git runs the fixture hook only when it is executable on Unix |
| `Services.MissionPrompt.template_resolved_claude_md_de_duplicates_shared_context_sections` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/MissionPromptTests.cs` :: Template-resolved CLAUDE.md de-duplicates shared context sections | generated instructions no longer carry a Model Context section in this template |
| `Services.MissionPrompt.template_resolved_persona_prompts_require_structured_test_and_judge_analysis` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/MissionPromptTests.cs` :: Template-resolved persona prompts require structured test and judge analysis | generated instructions are written under .armada/instructions |
| `Services.MissionPrompt.generate_claude_md_async_strips_stale_armada_mission_blocks_from_existing_instructions` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/MissionPromptTests.cs` :: GenerateClaudeMdAsync strips stale Armada mission blocks from existing instructions | generated instructions are written under .armada/instructions |
| `Services.MissionStatusTransition.all_expected_statuses_defined` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/MissionStatusTransitionTests.cs` :: All expected statuses defined | the mission status set includes WaitingForInput |
| `Services.PipelineDispatch.architect_fan_out_clones_full_downstream_chain_and_lands_only_terminal_stage` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/PipelineDispatchTests.cs` :: Architect fan-out clones full downstream chain and lands only terminal stage | Architect-derived briefs carry the plan-block label rule and stages need verified completion |
| `Services.PipelineDispatch.architect_fan_out_honors_explicit_mission_dependencies_across_worker_chains` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/PipelineDispatchTests.cs` :: Architect fan-out honors explicit mission dependencies across worker chains | stage completion now requires a structured result and a green voyage Check |
| `Services.PipelineDispatch.judge_parser_accepts_structured_armada_verdict_signal` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/PipelineDispatchTests.cs` :: Judge parser accepts structured ARMADA verdict signal | stage completion now requires a structured result and a green voyage Check |
| `Services.PipelineDispatch.judge_parser_accepts_markdown_verdict_heading_emitted_by_claude` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/PipelineDispatchTests.cs` :: Judge parser accepts markdown verdict heading emitted by Claude | stage completion now requires a structured result and a green voyage Check |
| `Services.PipelineDispatch.judge_parser_accepts_inline_sentence_verdict_emitted_by_claude` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/PipelineDispatchTests.cs` :: Judge parser accepts inline sentence verdict emitted by Claude | stage completion now requires a structured result and a green voyage Check |
| `Services.ReleaseVersion.source_mcp_helpers_use_net10_framework` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/ReleaseVersionTests.cs` :: Source MCP Helpers Use Net10 Framework | the MCP helper scripts call armada_resolve_framework |
| `Services.WorkflowProfileCheckRunService.run_async_executes_workflow_profile_command_and_collects_artifacts` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/WorkflowProfileCheckRunServiceTests.cs` :: RunAsync executes workflow profile command and collects artifacts | check runs use an isolated checkout, so the fixture must commit its files and send a real commit |
| `Services.WorkflowProfileCheckRunService.retry_async_re_executes_prior_check_run` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/WorkflowProfileCheckRunServiceTests.cs` :: RetryAsync re-executes prior check run | check runs use an isolated checkout, so the fixture must commit its files and send a real commit |
| `Services.WorkflowProfileCheckRunService.run_async_parses_structured_test_and_coverage_summaries` | Duplicate of executed legacy case | `test/Armada.Test.Unit/Suites/Services/WorkflowProfileCheckRunServiceTests.cs` :: RunAsync parses structured test and coverage summaries | check runs use an isolated checkout, so the fixture must commit its files and send a real commit |
| `E2E.McpTool.armada_add_vessel_git_hub_token_override_does_not_leak` | Awaiting owner decision | None | the MCP vessel tools do not accept a GitHub token override |
| `E2E.McpTool.armada_update_vessel_empty_git_hub_token_override_clears_override` | Awaiting owner decision | None | the MCP vessel tools do not accept a GitHub token override |
| `E2E.WebSocket.list_mission_summaries_empty_returns_empty_list` | Awaiting owner decision | None | the WebSocket hub does not serve the list_missions_summary command |
| `E2E.WebSocket.list_mission_summaries_with_pagination_respects_page_size` | Awaiting owner decision | None | the WebSocket hub does not serve the list_missions_summary command |
| `Services.CaptainRouting.preferred_idle_captain_is_assigned` | Awaiting owner decision | None | a mission's requested captain is resolved and stored but assignment does not consult it |
| `Services.CaptainRouting.no_tier_eligible_captain_stays_pending` | Awaiting owner decision | None | a mission's fallback tier is resolved and stored but assignment does not consult it |
| `Services.MissionPrompt.generate_claude_md_async_includes_model_context_when_enabled_and_set` | Intentional fork difference | None | generated instructions are written under .armada/instructions and carry no Model Context Updates section; kept because the fork's generated instruction sections are deliberate |
| `Services.MissionPrompt.generate_claude_md_async_includes_update_instructions_even_when_model_context_is_empty` | Intentional fork difference | None | generated instructions carry no Model Context Updates section; kept because the fork's generated instruction sections are deliberate |
| `Services.MissionPrompt.template_resolved_claude_md_contains_model_context_updates_when_enabled` | Intentional fork difference | None | generated instructions carry no Model Context Updates section; kept because the fork's generated instruction sections are deliberate |
| `Services.PersonaSeedService.seed_async_creates_new_built_in_personas_and_expanded_full_pipeline` | Intentional fork difference | None | the fork seeds FullPipeline as Architect, Worker, TestEngineer, Judge and seeds the persona as TestEngineer; kept because renaming or re-ordering seeded personas and pipelines on upgraded databases would break operator pipelines and prompt templates for no functional gain |
| `Services.PersonaSeedService.seed_async_upgrades_the_legacy_built_in_full_pipeline_order` | Intentional fork difference | None | the fork does not upgrade FullPipeline to the expanded product persona order; kept because renaming or re-ordering seeded personas and pipelines on upgraded databases would break operator pipelines and prompt templates for no functional gain |
| `Services.PersonaSeedService.seed_async_renames_the_legacy_built_in_test_engineer_persona` | Intentional fork difference | None | the fork does not rename the built-in TestEngineer persona to Test Engineer; kept because renaming or re-ordering seeded personas and pipelines on upgraded databases would break operator pipelines and prompt templates for no functional gain |
