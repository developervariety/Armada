namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Unit.Suites.Services;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    public partial class MissionRestartParityTests
    {
        private async Task RunHistoricalPermitTestsAsync()
        {
            await RunTest("Historical saved-work disposition issues a nonlaunching one-use same-mission restart permit", async () =>
            {
                foreach (bool reviewContinuation in new[] { false, true })
                {
                    using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                    {
                        HistoricalFixture fixture = await SeedHistoricalPermitAsync(harness, reviewContinuation).ConfigureAwait(false);
                        Mission worker = fixture.Base.Stages.Single(m => m.Id == fixture.Disposition.MissionId);
                        string[] originalRows = (await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Base.Voyage.Id).ConfigureAwait(false))
                            .OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => JsonSerializer.Serialize(m)).ToArray();
                        string endpoint = "/api/v1/voyages/" + fixture.Base.Voyage.Id + "/administrative-completion-correction";
                        SurfaceReply noDecision = await harness.RestAsync(HttpMethod.Post, endpoint,
                            new { dryRun = false, sourceCommit = fixture.Base.Source, expectedFingerprint = new string('0', 64) }).ConfigureAwait(false);
                        AssertEqual(409, noDecision.Status, "Executed cancelled work needs an explicit disposition");
                        AdministrativeVoyageRecoveryPlan preview = await HistoricalPreviewAsync(harness, fixture).ConfigureAwait(false);
                        AssertTrue(preview.Blockers.SequenceEqual(new[] { "exact_owner_historical_work_disposition_required" }), String.Join(",", preview.Blockers));
                        fixture.Disposition.GraphFingerprint = preview.Fingerprint;
                        AdministrativeVoyageRecoveryPlan signed = await HistoricalPreviewAsync(harness, fixture).ConfigureAwait(false);
                        AssertEqual(preview.Fingerprint, signed.Fingerprint, "The assertion signs the current graph; it does not alter the machine facts");
                        AssertEqual(0, signed.Blockers.Count, String.Join(",", signed.Blockers));
                        SurfaceReply correctedReply = await harness.RestAsync(HttpMethod.Post, endpoint,
                            new { dryRun = false, sourceCommit = fixture.Base.Source, historicalWork = fixture.Disposition,
                                expectedFingerprint = signed.Fingerprint, restartDescription = "Continue the preserved source; retain every role and charge." }).ConfigureAwait(false);
                        AssertEqual(200, correctedReply.Status, correctedReply.Body);
                        AdministrativeVoyageRecoveryPlan corrected = JsonSerializer.Deserialize<AdministrativeVoyageRecoveryPlan>(correctedReply.Body, ReplayReplyJson)!;
                        AssertNotNull(corrected.RestartPermitId);
                        AssertEqual(VoyageStatusEnum.Failed, (await harness.Driver.Voyages.ReadAsync(fixture.Base.Voyage.Id).ConfigureAwait(false))!.Status);
                        string[] correctedRows = (await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Base.Voyage.Id).ConfigureAwait(false))
                            .OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => JsonSerializer.Serialize(m)).ToArray();
                        AssertTrue(originalRows.SequenceEqual(correctedRows), "Correction and issue change no mission row and start no actor");
                        AssertTrue((await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Base.Voyage.Id).ConfigureAwait(false)).All(m => m.ProcessId == null));
                        SurfaceReply plain = await harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + worker.Id + "/restart", new { }).ConfigureAwait(false);
                        AssertEqual(409, plain.Status, "The legacy reason is not silently reclassified by ordinary restart");
                        SurfaceReply changedInstructions = await harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + worker.Id + "/restart",
                            new { administrativeRestartPermitId = corrected.RestartPermitId, description = "Unbound replacement instructions" }).ConfigureAwait(false);
                        AssertEqual(409, changedInstructions.Status, "Consumption cannot substitute instructions after issue");
                        SurfaceReply wrongMission = await harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + fixture.Base.Stages[0].Id + "/restart",
                            new { administrativeRestartPermitId = corrected.RestartPermitId }).ConfigureAwait(false);
                        AssertEqual(409, wrongMission.Status, "The permit names exactly one mission");
                        SurfaceReply[] contenders = await Task.WhenAll(
                            harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + worker.Id + "/restart", new { administrativeRestartPermitId = corrected.RestartPermitId }),
                            harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + worker.Id + "/restart", new { administrativeRestartPermitId = corrected.RestartPermitId })).ConfigureAwait(false);
                        AssertEqual(1, contenders.Count(reply => reply.Status == 200), String.Join("; ", contenders.Select(reply => reply.Body)));
                        AssertEqual(1, contenders.Count(reply => reply.Status == 409), "Concurrent consumers still reset only once");
                        Mission resumed = (await harness.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Pending, resumed.Status);
                        AssertEqual(worker.Id, resumed.Id); AssertEqual(worker.VoyageId, resumed.VoyageId);
                        AssertEqual(worker.CreatedUtc, resumed.CreatedUtc); AssertEqual(worker.RecoveryAttempts, resumed.RecoveryAttempts);
                        AssertEqual(worker.LandingRetryCount, resumed.LandingRetryCount);
                        AssertEqual(fixture.SavedCommit, resumed.StartFromRef);
                        AssertEqual(worker.DependsOnMissionId, resumed.DependsOnMissionId);
                        AssertEqual("Continue the preserved source; retain every role and charge.", resumed.Description);
                        AssertEqual(VoyageStatusEnum.InProgress, (await harness.Driver.Voyages.ReadAsync(worker.VoyageId!).ConfigureAwait(false))!.Status);
                        foreach (Mission stage in fixture.Base.Stages.Where(m => m.Id != worker.Id))
                            AssertEqual(JsonSerializer.Serialize(stage), JsonSerializer.Serialize((await harness.Driver.Missions.ReadAsync(stage.Id).ConfigureAwait(false))!), "Other required duties are not released");
                        List<ArmadaEvent> receipts = await harness.Driver.Events.EnumerateByTypeAsync("mission.administrative_restart_consumed").ConfigureAwait(false);
                        AssertEqual(1, receipts.Count);
                        AssertEqual(worker.VesselId, receipts[0].VesselId, "The consumed receipt remains in the vessel's event history");
                        EnumerationResult<ArmadaEvent> vesselReceipts = await harness.Driver.Events.EnumerateAsync(new EnumerationQuery
                        { VesselId = worker.VesselId, EventType = "mission.administrative_restart_consumed", PageSize = 10 }).ConfigureAwait(false);
                        AssertEqual(1, vesselReceipts.Objects.Count);
                        AssertEqual(receipts[0].Id, vesselReceipts.Objects[0].Id);
                        AdministrativeRestartReceipt receipt = JsonSerializer.Deserialize<AdministrativeRestartReceipt>(receipts[0].Payload!)!;
                        AssertContains("Mission cancelled by operator.", receipt.PriorSnapshot);
                        AssertContains("Historical saved report and separate charge history", receipt.PriorSnapshot);
                        AssertContains(worker.PreservedRunOutput!, receipt.PriorSnapshot);
                        AssertEqual(fixture.SavedCommit, await AdministrativeRestartSource.ResolveAsync(harness.Driver, resumed, fixture.Base.Stages.Single(m => m.Id == worker.DependsOnMissionId)).ConfigureAwait(false));
                        SurfaceReply replay = await harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + worker.Id + "/restart",
                            new { administrativeRestartPermitId = corrected.RestartPermitId }).ConfigureAwait(false);
                        AssertEqual(409, replay.Status, "The permit is durably one-use");
                        AssertEqual(1, (await harness.Driver.Events.EnumerateByTypeAsync("mission.administrative_restart_consumed").ConfigureAwait(false)).Count);

                        LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                        GitService git = new GitService(logging);
                        DockService docks = new DockService(logging, harness.Driver, harness.Settings, git);
                        CaptainService captains = new CaptainService(logging, harness.Driver, harness.Settings, git, docks);
                        int launches = 0;
                        captains.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(Int32.MaxValue - 50); };
                        MissionService missions = new MissionService(logging, harness.Driver, harness.Settings, docks, captains,
                            git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(harness.Settings));
                        await harness.Driver.Captains.CreateAsync(new Captain("saved-work-source")
                        { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, Model = "claude-opus-5", AllowedPersonas = JsonSerializer.Serialize(new[] { worker.Persona }) }).ConfigureAwait(false);
                        Vessel vessel = (await harness.Driver.Vessels.ReadAsync(worker.VesselId!).ConfigureAwait(false))!;
                        AssertTrue(await missions.TryAssignAsync(resumed, vessel).ConfigureAwait(false), "The normal assignment owner honors the consumed source, not the planner base");
                        Mission assigned = (await harness.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                        Dock dock = (await harness.Driver.Docks.ReadAsync(assigned.DockId!).ConfigureAwait(false))!;
                        AssertEqual(fixture.SavedCommit, (await RunGitAsync(dock.WorktreePath!, "rev-parse", "HEAD").ConfigureAwait(false)).Trim());
                        AssertEqual(1, launches, "Only the explicit consume made the mission eligible; the test uses a nonexecuting launch hook");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Administrative restart permit is enforced on MCP and WebSocket wire paths", async () =>
            {
                foreach (string surface in new[] { "MCP", "WebSocket" })
                {
                    foreach (bool reviewContinuation in new[] { false, true })
                    {
                        using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                        {
                            HistoricalFixture fixture = await SeedHistoricalPermitAsync(harness, reviewContinuation).ConfigureAwait(false);
                            AdministrativeVoyageRecoveryPlan preview = await HistoricalPreviewAsync(harness, fixture).ConfigureAwait(false);
                            fixture.Disposition.GraphFingerprint = preview.Fingerprint;
                            SurfaceReply issuedReply = await harness.RestAsync(HttpMethod.Post,
                                "/api/v1/voyages/" + fixture.Base.Voyage.Id + "/administrative-completion-correction",
                                new { dryRun = false, sourceCommit = fixture.Base.Source, historicalWork = fixture.Disposition,
                                    expectedFingerprint = preview.Fingerprint, restartDescription = "Continue the preserved source; retain every role and charge." }).ConfigureAwait(false);
                            AssertEqual(200, issuedReply.Status, issuedReply.Body);
                            AdministrativeVoyageRecoveryPlan issued = JsonSerializer.Deserialize<AdministrativeVoyageRecoveryPlan>(issuedReply.Body, ReplayReplyJson)!;
                            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                            GitService git = new GitService(logging);
                            AuthContext owner = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "test");
                            if (surface == "MCP")
                            {
                                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                                Armada.Server.Mcp.Tools.McpMissionTools.Register((name, _, _, handler) => handlers[name] = handler,
                                    harness.Driver, harness.Admiral.Service, harness.Settings, git, operations: harness.Operations);
                                UserMaster other = await harness.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId,
                                    "other-admin-" + Guid.NewGuid().ToString("N") + "@example.invalid", "password") { IsAdmin = true, Active = true }).ConfigureAwait(false);
                                using (Armada.Server.Mcp.McpCallerContext.Begin(AuthContext.Authenticated(Constants.DefaultTenantId, other.Id, true, false, "test")))
                                {
                                    object denied = await handlers["armada_restart_mission"](JsonSerializer.SerializeToElement(new
                                    { missionId = fixture.Disposition.MissionId, administrativeRestartPermitId = issued.RestartPermitId })).ConfigureAwait(false);
                                    PermitErrorReply error = JsonSerializer.Deserialize<PermitErrorReply>(JsonSerializer.Serialize(denied), ReplayReplyJson)!;
                                    AssertFalse(String.IsNullOrEmpty(error.Error), "A different active global administrator cannot consume the owned permit");
                                }
                                AssertEqual(MissionStatusEnum.Cancelled, (await harness.Driver.Missions.ReadAsync(fixture.Disposition.MissionId).ConfigureAwait(false))!.Status);
                                using (Armada.Server.Mcp.McpCallerContext.Begin(owner))
                                {
                                    object result = await handlers["armada_restart_mission"](JsonSerializer.SerializeToElement(new
                                    { missionId = fixture.Disposition.MissionId, administrativeRestartPermitId = issued.RestartPermitId })).ConfigureAwait(false);
                                    PermitErrorReply error = JsonSerializer.Deserialize<PermitErrorReply>(JsonSerializer.Serialize(result), ReplayReplyJson)!;
                                    AssertTrue(String.IsNullOrEmpty(error.Error), JsonSerializer.Serialize(result));
                                }
                            }
                            else
                            {
                                Armada.Server.WebSocket.WebSocketCommandHandler websocket = new Armada.Server.WebSocket.WebSocketCommandHandler(
                                    harness.Admiral.Service, harness.Driver, harness.MergeQueue, harness.Settings, git, null,
                                    ReplayReplyJson, _ => { }, _ => { });
                                websocket.Operations = harness.Operations;
                                string raw = JsonSerializer.Serialize(new { action = "restart_mission", id = fixture.Disposition.MissionId,
                                    data = new { administrativeRestartPermitId = issued.RestartPermitId } });
                                object result = await websocket.HandleCommandAsync("restart_mission",
                                    new Armada.Server.WebSocket.WebSocketCommand { Id = fixture.Disposition.MissionId }, raw, owner).ConfigureAwait(false);
                                PermitErrorReply error = JsonSerializer.Deserialize<PermitErrorReply>(JsonSerializer.Serialize(result), ReplayReplyJson)!;
                                AssertTrue(String.IsNullOrEmpty(error.Error), JsonSerializer.Serialize(result));
                            }
                            Mission resumed = (await harness.Driver.Missions.ReadAsync(fixture.Disposition.MissionId).ConfigureAwait(false))!;
                            AssertEqual(MissionStatusEnum.Pending, resumed.Status, surface);
                            AssertEqual(fixture.SavedCommit, resumed.StartFromRef, surface);
                            AssertEqual(fixture.Base.Voyage.Id, resumed.VoyageId, surface);
                            AssertEqual(1, (await harness.Driver.Events.EnumerateByTypeAsync("mission.administrative_restart_consumed").ConfigureAwait(false)).Count, surface);
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Historical disposition refuses stale owner source landing and cancellation evidence", async () =>
            {
                foreach (string variant in new[] { "wrong_owner", "stale_graph", "accepted", "landed", "unknown_ref", "wrong_cancel", "new_cancel", "changed_native", "missing_digest", "consume_stale", "consume_landed", "consume_landing_record", "consume_scope", "consume_capacity_lost", "accepted_native", "landed_fact", "local_landed", "consume_local_landed", "ref_expression" })
                {
                    using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                    {
                        HistoricalFixture fixture = await SeedHistoricalPermitAsync(harness).ConfigureAwait(false);
                        AdministrativeVoyageRecoveryPlan initial = await HistoricalPreviewAsync(harness, fixture).ConfigureAwait(false);
                        fixture.Disposition.GraphFingerprint = initial.Fingerprint;
                        if (variant.StartsWith("consume_", StringComparison.Ordinal))
                        {
                            SurfaceReply issue = await harness.RestAsync(HttpMethod.Post,
                                "/api/v1/voyages/" + fixture.Base.Voyage.Id + "/administrative-completion-correction",
                                new { dryRun = false, sourceCommit = fixture.Base.Source, historicalWork = fixture.Disposition,
                                    expectedFingerprint = initial.Fingerprint, restartDescription = "Continue the preserved source; retain every role and charge." }).ConfigureAwait(false);
                            AssertEqual(200, issue.Status, issue.Body);
                            AdministrativeVoyageRecoveryPlan issued = JsonSerializer.Deserialize<AdministrativeVoyageRecoveryPlan>(issue.Body, ReplayReplyJson)!;
                            if (variant == "consume_stale")
                            {
                                Mission changed = fixture.Base.Stages[1]; changed.Description += " stale";
                                await harness.Driver.Missions.UpdateAsync(changed).ConfigureAwait(false);
                            }
                            if (variant == "consume_local_landed") await LandOnlyLocalTargetAsync(harness, fixture).ConfigureAwait(false);
                            if (variant == "consume_landed") await RunGitAsync(fixture.Base.Repository, "merge", "--ff-only", fixture.SavedCommit).ConfigureAwait(false);
                            if (variant == "consume_landing_record")
                                await harness.Driver.MergeEntries.CreateAsync(new MergeEntry("recover/frozen-work", "main")
                                { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, MissionId = fixture.Disposition.MissionId,
                                    VesselId = fixture.Base.Stages[1].VesselId, Status = MergeStatusEnum.Landed }).ConfigureAwait(false);
                            if (variant == "consume_capacity_lost")
                            {
                                ArmadaEvent issuedEvent = (await harness.Driver.Events.ReadAsync(issued.RestartPermitId!).ConfigureAwait(false))!;
                                AdministrativeRestartPermit permit = JsonSerializer.Deserialize<AdministrativeRestartPermit>(issuedEvent.Payload!)!;
                                Vessel vessel = (await harness.Driver.Vessels.ReadAsync(fixture.Base.Stages[1].VesselId!).ConfigureAwait(false))!;
                                await using (FleetCapacityReservation reservation = await new FleetCapacityAdmission(harness.Driver, harness.Settings)
                                    .AcquireAsync(vessel, fixture.Base.Voyage.Id).ConfigureAwait(false))
                                {
                                    await reservation.VerifyOwnershipAsync().ConfigureAwait(false);
                                    await harness.Driver.CoordinationLeases.ReleaseAsync(reservation.LeaseName, reservation.LeaseHolder).ConfigureAwait(false);
                                    bool stopped = false;
                                    try
                                    {
                                        await harness.Driver.AdministrativeVoyageRecovery.ConsumeRestartPermitAsync(Constants.DefaultTenantId,
                                            Constants.DefaultUserId, permit.MissionId, permit,
                                            new AdministrativeVoyageRecoveryRequest { SourceCommit = permit.PlanningSourceCommit,
                                                HistoricalWork = permit.Disposition, RestartDescription = permit.RestartDescription,
                                                CapturedHistoricalEvidence = true, CapturedSourceEvidence = permit.SourceEvidence }, reservation).ConfigureAwait(false);
                                    }
                                    catch (InvalidOperationException) { stopped = true; }
                                    AssertTrue(stopped, "The atomic reset refuses an actually lost durable capacity lease");
                                }
                                AssertEqual(0, (await harness.Driver.Events.EnumerateByTypeAsync("mission.administrative_restart_consumed").ConfigureAwait(false)).Count);
                                AssertEqual(MissionStatusEnum.Cancelled, (await harness.Driver.Missions.ReadAsync(fixture.Disposition.MissionId).ConfigureAwait(false))!.Status);
                                continue;
                            }
                            string? scopedToken = variant == "consume_scope"
                                ? harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, fixture.Disposition.MissionId) : null;
                            SurfaceReply consume = await harness.RestAsync(HttpMethod.Post,
                                "/api/v1/missions/" + fixture.Disposition.MissionId + "/restart",
                                new { administrativeRestartPermitId = issued.RestartPermitId }, scopedToken).ConfigureAwait(false);
                            AssertTrue(consume.Status == 409 || consume.Status == 403, variant + ": " + consume.Body);
                            AssertEqual(MissionStatusEnum.Cancelled, (await harness.Driver.Missions.ReadAsync(fixture.Disposition.MissionId).ConfigureAwait(false))!.Status);
                            AssertEqual(VoyageStatusEnum.Failed, (await harness.Driver.Voyages.ReadAsync(fixture.Base.Voyage.Id).ConfigureAwait(false))!.Status);
                            AssertEqual(0, (await harness.Driver.Events.EnumerateByTypeAsync("mission.administrative_restart_consumed").ConfigureAwait(false)).Count);
                            continue;
                        }

                        if (variant == "accepted") fixture.Disposition.Kind = HistoricalWorkDispositionKindEnum.Accepted;
                        if (variant == "unknown_ref") fixture.Disposition.PreservedSourceRef = "recover/absent";
                        if (variant == "ref_expression") fixture.Disposition.PreservedSourceRef = "recover/frozen-work~0";
                        if (variant == "wrong_cancel") fixture.Disposition.CancellationEventId = "missing-cancellation";
                        if (variant == "missing_digest") fixture.Disposition.NativeEvidenceSHA256 = String.Empty;
                        if (variant == "changed_native") fixture.Disposition.NativeEvidenceSHA256 = new string('a', 64);
                        if (variant == "stale_graph")
                        {
                            Mission stage = fixture.Base.Stages[1]; stage.Description += " changed";
                            await harness.Driver.Missions.UpdateAsync(stage).ConfigureAwait(false);
                        }
                        if (variant == "new_cancel")
                            await harness.Driver.Events.CreateAsync(new ArmadaEvent("mission.cancelled", "Later operator cancellation")
                            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, EntityType = "mission", EntityId = fixture.Disposition.MissionId,
                                MissionId = fixture.Disposition.MissionId, VoyageId = fixture.Base.Voyage.Id, CreatedUtc = DateTime.UtcNow }).ConfigureAwait(false);
                        if (variant == "accepted_native")
                            await harness.Driver.Events.CreateAsync(new ArmadaEvent("mission.completed", "Recorded completed work")
                            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, MissionId = fixture.Disposition.MissionId,
                                VoyageId = fixture.Base.Voyage.Id }).ConfigureAwait(false);
                        if (variant == "landed_fact")
                            await harness.Driver.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
                            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, MissionId = fixture.Disposition.MissionId,
                                RootMissionId = fixture.Disposition.MissionId, VoyageId = fixture.Base.Voyage.Id,
                                FactType = MissionAttemptFactTypeEnum.Landed }).ConfigureAwait(false);
                        if (variant == "local_landed") await LandOnlyLocalTargetAsync(harness, fixture).ConfigureAwait(false);
                        if (variant == "landed")
                            await RunGitAsync(fixture.Base.Repository, "merge", "--ff-only", fixture.SavedCommit).ConfigureAwait(false);
                        if (variant == "wrong_owner")
                        {
                            UserMaster foreign = await harness.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId,
                                "foreign-" + Guid.NewGuid().ToString("N") + "@example.invalid", "password")).ConfigureAwait(false);
                            fixture.Base.Voyage.UserId = foreign.Id;
                            await harness.Driver.Voyages.UpdateAsync(fixture.Base.Voyage).ConfigureAwait(false);
                            SurfaceReply forbidden = await harness.RestAsync(HttpMethod.Post,
                                "/api/v1/voyages/" + fixture.Base.Voyage.Id + "/administrative-completion-correction",
                                new { dryRun = true, sourceCommit = fixture.Base.Source, historicalWork = fixture.Disposition }).ConfigureAwait(false);
                            AssertEqual(403, forbidden.Status, "Even a global administrator cannot issue another owner's recovery permit");
                            continue;
                        }
                        SurfaceReply apply = await harness.RestAsync(HttpMethod.Post,
                            "/api/v1/voyages/" + fixture.Base.Voyage.Id + "/administrative-completion-correction",
                            new { dryRun = false, sourceCommit = fixture.Base.Source, historicalWork = fixture.Disposition,
                                expectedFingerprint = initial.Fingerprint, restartDescription = "Continue the preserved source; retain every role and charge." }).ConfigureAwait(false);
                        AssertEqual(409, apply.Status, variant + ": " + apply.Body);
                        AssertEqual(VoyageStatusEnum.Complete, (await harness.Driver.Voyages.ReadAsync(fixture.Base.Voyage.Id).ConfigureAwait(false))!.Status);
                        AssertEqual(0, (await harness.Driver.Events.EnumerateByTypeAsync("mission.administrative_restart_permit_issued").ConfigureAwait(false)).Count);
                    }
                }
            }).ConfigureAwait(false);
        }

        private sealed class PermitErrorReply
        {
            public string? Error { get; set; }
        }

        private sealed class HistoricalFixture
        {
            public AdministrativeFixture Base { get; set; } = null!;
            public string SavedCommit { get; set; } = String.Empty;
            public HistoricalWorkDisposition Disposition { get; set; } = null!;
        }

        private static async Task<HistoricalFixture> SeedHistoricalPermitAsync(SurfaceParityHarness harness, bool reviewContinuation = false)
        {
            AdministrativeFixture fixture = await SeedAdministrativeAsync(harness, reviewContinuation).ConfigureAwait(false);
            fixture.Stages[2].Persona = "TestEngineer";
            await harness.Driver.Missions.UpdateAsync(fixture.Stages[2]).ConfigureAwait(false);
            foreach (string role in new[] { "Linter", "Worker", "Judge" })
                fixture.Stages.Add(await harness.Driver.Missions.CreateAsync(new Mission(role + " required review")
                { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = fixture.Stages[0].VesselId,
                    VoyageId = fixture.Voyage.Id, Persona = role, StageChainId = fixture.Stages[0].StageChainId,
                    StageOrder = fixture.Stages.Count + 1, DependsOnMissionId = fixture.Stages.Last().Id,
                    Status = MissionStatusEnum.Cancelled, FailureReason = "Blocked by cancelled dependency " + fixture.Stages[1].Id + ".",
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-2), StartFromRef = fixture.Source }).ConfigureAwait(false));
            await RunGitAsync(fixture.Repository, "checkout", "-b", "historical-work").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "saved-work.txt"), "Unaccepted saved implementation source").ConfigureAwait(false);
            await RunGitAsync(fixture.Repository, "add", "saved-work.txt").ConfigureAwait(false);
            await RunGitAsync(fixture.Repository, "commit", "-m", "Preserve unaccepted implementation").ConfigureAwait(false);
            string saved = (await RunGitAsync(fixture.Repository, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
            await RunGitAsync(fixture.Repository, "branch", "recover/frozen-work", saved).ConfigureAwait(false);
            await RunGitAsync(fixture.Repository, "checkout", "main").ConfigureAwait(false);
            Mission worker = fixture.Stages[1];
            worker.StartedUtc = DateTime.UtcNow.AddMinutes(-10); worker.ProcessStartedUtc = worker.StartedUtc;
            worker.CompletedUtc = DateTime.UtcNow.AddMinutes(-2);
            worker.AgentOutput = "Historical saved report and separate charge history";
            worker.PreservedRunOutput = "Prior work remains unaccepted; retain every source and charge.";
            worker.RecoveryAttempts = 3; worker.LandingRetryCount = 2; worker.RequiresReview = true;
            fixture.Stages[1] = await harness.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
            Mission target = worker;
            if (reviewContinuation)
            {
                worker.Status = MissionStatusEnum.Complete;
                worker.CommitHash = saved;
                worker.BranchName = "recover/frozen-work";
                worker.FailureReason = null;
                worker.ReviewedUtc = worker.CompletedUtc!.Value.AddSeconds(1);
                worker.ReviewedByUserId = Constants.DefaultUserId;
                fixture.Stages[1] = await harness.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                Mission storedProducer = (await harness.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                if (storedProducer.Status != MissionStatusEnum.Complete || storedProducer.CommitHash != saved)
                    throw new InvalidOperationException("The fixture must store genuinely completed producer work before requesting review recovery.");
                await harness.Driver.Events.CreateAsync(new ArmadaEvent("mission.completed", "Producer duty completed; required independent reviews remain")
                { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, MissionId = worker.Id,
                    VoyageId = worker.VoyageId, CreatedUtc = worker.CompletedUtc.Value }).ConfigureAwait(false);
                target = fixture.Stages[2];
                target.StartFromRef = saved;
                target.StartedUtc = DateTime.UtcNow.AddMinutes(-1);
                target.ProcessStartedUtc = target.StartedUtc;
                target.CompletedUtc = DateTime.UtcNow.AddSeconds(-30);
                target.FailureReason = "Mission cancelled by operator.";
                target.AgentOutput = "Historical saved report and separate charge history";
                target.PreservedRunOutput = "Prior review input and charge history retained.";
                target.RecoveryAttempts = 1;
                target.LandingRetryCount = 2;
                target.RequiresReview = true;
                fixture.Stages[2] = await harness.Driver.Missions.UpdateAsync(target).ConfigureAwait(false);
                fixture.Voyage.CompletedUtc = target.CompletedUtc.Value.AddSeconds(10);
                await harness.Driver.Voyages.UpdateAsync(fixture.Voyage).ConfigureAwait(false);
                foreach (Mission later in fixture.Stages.Skip(3))
                {
                    later.FailureReason = "Blocked by cancelled dependency " + target.Id + ".";
                    await harness.Driver.Missions.UpdateAsync(later).ConfigureAwait(false);
                }
            }
            ArmadaEvent cancellation = await harness.Driver.Events.CreateAsync(new ArmadaEvent("mission.cancelled", "Mission cancelled by operator")
            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, EntityType = "mission", EntityId = target.Id,
                MissionId = target.Id, VoyageId = target.VoyageId, CreatedUtc = target.CompletedUtc!.Value.AddSeconds(1) }).ConfigureAwait(false);
            await harness.Driver.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, MissionId = target.Id, RootMissionId = target.Id,
                VoyageId = target.VoyageId, VesselId = target.VesselId, FactType = MissionAttemptFactTypeEnum.Restarted }).ConfigureAwait(false);
            return new HistoricalFixture { Base = fixture, SavedCommit = saved, Disposition = new HistoricalWorkDisposition
            { Kind = HistoricalWorkDispositionKindEnum.Unaccepted, MissionId = target.Id, PreservedSourceRef = "recover/frozen-work",
                PreservedSourceCommit = saved, CancellationEventId = cancellation.Id,
                NativeEvidenceSHA256 = new string('d', 64), CountsEvidenceSHA256 = new string('e', 64),
                Reason = "Saved implementation remains unaccepted; the later cancelled epoch performed startup reads only." } };
        }

        private static async Task LandOnlyLocalTargetAsync(SurfaceParityHarness harness, HistoricalFixture fixture)
        {
            string remote = Path.Combine(harness.Settings.ReposDirectory, "unchanged-remote.git");
            await RunGitAsync(harness.Settings.ReposDirectory, "clone", "--bare", fixture.Base.Repository, remote).ConfigureAwait(false);
            await RunGitAsync(fixture.Base.Repository, "remote", "set-url", "origin", remote).ConfigureAwait(false);
            await RunGitAsync(fixture.Base.Repository, "merge", "--ff-only", fixture.SavedCommit).ConfigureAwait(false);
        }

        private static async Task<AdministrativeVoyageRecoveryPlan> HistoricalPreviewAsync(SurfaceParityHarness harness, HistoricalFixture fixture)
        {
            SurfaceReply reply = await harness.RestAsync(HttpMethod.Post,
                "/api/v1/voyages/" + fixture.Base.Voyage.Id + "/administrative-completion-correction",
                new { dryRun = true, sourceCommit = fixture.Base.Source, historicalWork = fixture.Disposition,
                    restartDescription = "Continue the preserved source; retain every role and charge." }).ConfigureAwait(false);
            if (reply.Status != 200) throw new InvalidOperationException(reply.Body);
            return JsonSerializer.Deserialize<AdministrativeVoyageRecoveryPlan>(reply.Body, ReplayReplyJson)!;
        }
    }
}
