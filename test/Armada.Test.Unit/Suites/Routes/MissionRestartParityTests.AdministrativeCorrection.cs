namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
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

    public partial class MissionRestartParityTests
    {
        private async Task RunAdministrativeCorrectionTestsAsync()
        {
            await RunTest("Administrative completion correction preserves history and starts no mission", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    AdministrativeFixture fixture = await SeedAdministrativeAsync(harness).ConfigureAwait(false);
                    string path = "/api/v1/voyages/" + fixture.Voyage.Id + "/administrative-completion-correction";
                    SurfaceReply previewReply = await harness.RestAsync(HttpMethod.Post, path,
                        new { dryRun = true, sourceCommit = fixture.Source }).ConfigureAwait(false);
                    AssertEqual(200, previewReply.Status, previewReply.Body);
                    AdministrativeVoyageRecoveryPlan preview = JsonSerializer.Deserialize<AdministrativeVoyageRecoveryPlan>(previewReply.Body, ReplayReplyJson)!;
                    AssertEqual(0, preview.Blockers.Count, String.Join(",", preview.Blockers));
                    AssertFalse(preview.Applied, "Preview makes no state change");
                    string[] priorRows = (await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false))
                        .OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => JsonSerializer.Serialize(m)).ToArray();
                    SurfaceReply wrong = await harness.RestAsync(HttpMethod.Post, path,
                        new { dryRun = false, sourceCommit = fixture.Source, expectedFingerprint = new string('0', 64) }).ConfigureAwait(false);
                    AssertEqual(409, wrong.Status, "Stale expected state never applies");
                    SurfaceReply appliedReply = await harness.RestAsync(HttpMethod.Post, path,
                        new { dryRun = false, sourceCommit = fixture.Source, expectedFingerprint = preview.Fingerprint }).ConfigureAwait(false);
                    AssertEqual(200, appliedReply.Status, appliedReply.Body);
                    AdministrativeVoyageRecoveryPlan applied = JsonSerializer.Deserialize<AdministrativeVoyageRecoveryPlan>(appliedReply.Body, ReplayReplyJson)!;
                    AssertTrue(applied.Applied);
                    Voyage current = (await harness.Driver.Voyages.ReadAsync(fixture.Voyage.Id).ConfigureAwait(false))!;
                    AssertEqual(VoyageStatusEnum.Failed, current.Status, "Correction does not start a recovery");
                    AssertEqual(fixture.Voyage.CompletedUtc, current.CompletedUtc, "Original completion time remains");
                    string[] afterRows = (await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false))
                        .OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => JsonSerializer.Serialize(m)).ToArray();
                    AssertTrue(priorRows.SequenceEqual(afterRows), "Every mission status, source, count and report stays byte-identical");
                    List<ArmadaEvent> events = await harness.Driver.Events.EnumerateByTypeAsync("voyage.administrative_completion_corrected").ConfigureAwait(false);
                    AssertEqual(1, events.Count, "Atomic audit retains the original Complete outcome exactly once");
                    AssertEqual(applied.AuditEventId, events[0].Id);
                    AssertContains("Complete", events[0].Payload!);
                    AssertFalse(events[0].Payload!.Contains("github_token_override", StringComparison.OrdinalIgnoreCase), "Source credentials never enter the audit");
                    SurfaceReply repeated = await harness.RestAsync(HttpMethod.Post, path,
                        new { dryRun = false, sourceCommit = fixture.Source, expectedFingerprint = preview.Fingerprint }).ConfigureAwait(false);
                    AssertEqual(409, repeated.Status, "A spent correction cannot rewrite history again");
                    AssertEqual(1, (await harness.Driver.Events.EnumerateByTypeAsync("voyage.administrative_completion_corrected").ConfigureAwait(false)).Count);
                }
            }).ConfigureAwait(false);

            await RunTest("Administrative completion correction refuses unknown or accepted code evidence", async () =>
            {
                foreach (string variant in new[] { "started_cancelled", "produced_commit", "landed_fact", "merge_lineage", "pending_check", "moved_branch", "prior_attempt", "report_only", "held", "wrong_owner", "unavailable_trailer", "incomplete_trailer_window" })
                {
                    using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                    {
                        AdministrativeFixture fixture = await SeedAdministrativeAsync(harness).ConfigureAwait(false);
                        if (variant == "unavailable_trailer")
                        {
                            global::Test.Shared.Infrastructure.StubGitService unavailable = new global::Test.Shared.Infrastructure.StubGitService
                            { RevisionCommitShaResult = fixture.Source };
                            Armada.Server.AdministrativeVoyageRecoveryService service = new Armada.Server.AdministrativeVoyageRecoveryService(
                                harness.Driver, harness.Admiral.Service, unavailable);
                            AuthContext actor = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "test");
                            AdministrativeVoyageRecoveryPlan unknown = await service.ExecuteAsync(actor, fixture.Voyage.Id,
                                new AdministrativeVoyageRecoveryRequest { DryRun = true, SourceCommit = fixture.Source }).ConfigureAwait(false);
                            AssertTrue(unknown.Blockers.Contains("repository_source_evidence_required"), "An unsupported search is unknown, never an empty proved search");
                            bool refused = false;
                            try { await service.ExecuteAsync(actor, fixture.Voyage.Id, new AdministrativeVoyageRecoveryRequest
                                { DryRun = false, SourceCommit = fixture.Source, ExpectedFingerprint = unknown.Fingerprint }).ConfigureAwait(false); }
                            catch (InvalidOperationException) { refused = true; }
                            AssertTrue(refused); continue;
                        }
                        Mission worker = fixture.Stages[1];
                        if (variant == "started_cancelled") worker.StartedUtc = DateTime.UtcNow.AddHours(-1);
                        if (variant == "produced_commit") worker.CommitHash = new string('f', 40);
                        if (variant == "prior_attempt") worker.PreservedRunOutput = "Earlier produced report retained";
                        if (variant == "report_only") fixture.Stages[0].Mode = MissionModeEnum.Research;
                        if (variant == "held") worker.HeldForOperatorReview = true;
                        if (variant == "wrong_owner") worker.UserId = "another-owner";
                        if (variant != "wrong_owner")
                        {
                            await harness.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                            await harness.Driver.Missions.UpdateAsync(fixture.Stages[0]).ConfigureAwait(false);
                        }
                        else
                        {
                            // Ownership validation belongs to the real graph rule, even without a matching user row.
                            bool refused = false;
                            try { AdministrativeVoyageRecoveryRule.FindBlockers(fixture.Voyage, fixture.Stages,
                                Constants.DefaultTenantId, Constants.DefaultUserId, fixture.Source); }
                            catch (UnauthorizedAccessException) { refused = true; }
                            AssertTrue(refused); continue;
                        }
                        if (variant == "landed_fact")
                            await harness.Driver.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
                            { TenantId = worker.TenantId, UserId = worker.UserId, MissionId = worker.Id, RootMissionId = worker.Id,
                                VoyageId = worker.VoyageId, VesselId = worker.VesselId, FactType = MissionAttemptFactTypeEnum.Landed }).ConfigureAwait(false);
                        if (variant == "merge_lineage")
                            await harness.Driver.MergeEntries.CreateAsync(new MergeEntry("planning", "main")
                            { TenantId = worker.TenantId, UserId = worker.UserId, VesselId = worker.VesselId,
                                MissionId = worker.Id, Status = MergeStatusEnum.Landed }).ConfigureAwait(false);
                        if (variant == "pending_check")
                            await harness.Driver.CheckRuns.CreateAsync(new CheckRun
                            { TenantId = worker.TenantId, UserId = worker.UserId, VoyageId = worker.VoyageId,
                                Status = CheckRunStatusEnum.Pending, Command = "build" }).ConfigureAwait(false);
                        if (variant == "moved_branch")
                        {
                            await RunGitAsync(fixture.Repository, "commit", "--allow-empty", "-m", "Independent source change").ConfigureAwait(false);
                            await RunGitAsync(fixture.Repository, "branch", "-f", "planning", "HEAD").ConfigureAwait(false);
                        }
                        if (variant == "incomplete_trailer_window")
                        {
                            for (int mention = 0; mention < 4; mention++)
                                await RunGitAsync(fixture.Repository, "commit", "--allow-empty", "-m", "Discussion mentions " + worker.Id).ConfigureAwait(false);
                        }
                        string path = "/api/v1/voyages/" + fixture.Voyage.Id + "/administrative-completion-correction";
                        SurfaceReply reply = await harness.RestAsync(HttpMethod.Post, path,
                            new { dryRun = true, sourceCommit = fixture.Source }).ConfigureAwait(false);
                        AssertEqual(200, reply.Status, variant + ": " + reply.Body);
                        AdministrativeVoyageRecoveryPlan preview = JsonSerializer.Deserialize<AdministrativeVoyageRecoveryPlan>(reply.Body, ReplayReplyJson)!;
                        AssertTrue(preview.Blockers.Count > 0, variant + " must name its evidence gap");
                        SurfaceReply apply = await harness.RestAsync(HttpMethod.Post, path,
                            new { dryRun = false, sourceCommit = fixture.Source, expectedFingerprint = preview.Fingerprint }).ConfigureAwait(false);
                        AssertEqual(409, apply.Status, variant + " cannot apply an unproved preview");
                        AssertEqual(VoyageStatusEnum.Complete, (await harness.Driver.Voyages.ReadAsync(fixture.Voyage.Id).ConfigureAwait(false))!.Status);
                        AssertEqual(0, (await harness.Driver.Events.EnumerateByTypeAsync("voyage.administrative_completion_corrected").ConfigureAwait(false)).Count);
                    }
                }
            }).ConfigureAwait(false);
        }

        private sealed class AdministrativeFixture
        {
            public Voyage Voyage { get; set; } = null!;
            public List<Mission> Stages { get; set; } = new List<Mission>();
            public string Source { get; set; } = String.Empty;
            public string Repository { get; set; } = String.Empty;
        }

        private static async Task<AdministrativeFixture> SeedAdministrativeAsync(SurfaceParityHarness harness)
        {
            string source = System.IO.Path.Combine(harness.Settings.ReposDirectory, "administrative-source");
            GitServiceTests.SeedTemplateRepository(source);
            await RunGitAsync(source, "remote", "add", "origin", source).ConfigureAwait(false);
            await RunGitAsync(source, "fetch", "origin").ConfigureAwait(false);
            string commit = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
            await RunGitAsync(source, "branch", "planning", commit).ConfigureAwait(false);
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("planning-evidence", "https://example.invalid/repository.git")
            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, LocalPath = source, DefaultBranch = "main" }).ConfigureAwait(false);
            Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("Planning-only administrative result", "Required work did not run")
            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, Status = VoyageStatusEnum.Complete,
                CompletedUtc = DateTime.UtcNow.AddMinutes(-1) }).ConfigureAwait(false);
            AdministrativeFixture result = new AdministrativeFixture { Voyage = voyage, Source = commit, Repository = source };
            string chain = Mission.NewStageChainId();
            foreach (string role in new[] { "Architect", "Worker", "Judge" })
            {
                bool planner = result.Stages.Count == 0;
                Mission stage = await harness.Driver.Missions.CreateAsync(new Mission(role + " required duty")
                { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, VoyageId = voyage.Id,
                    Persona = role, Mode = MissionModeEnum.Implementation, StageChainId = chain, StageOrder = result.Stages.Count + 1,
                    DependsOnMissionId = result.Stages.LastOrDefault()?.Id, StartFromRef = commit,
                    CommitHash = planner ? commit : null, BranchName = planner ? "planning" : null,
                    Status = planner ? MissionStatusEnum.Complete : MissionStatusEnum.Cancelled,
                    FailureReason = planner ? null : "Mission cancelled by operator.", CompletedUtc = DateTime.UtcNow.AddMinutes(-2) }).ConfigureAwait(false);
                result.Stages.Add(stage);
            }
            return result;
        }
    }
}
