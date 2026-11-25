namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Unit.TestHelpers;

    public partial class MissionRestartParityTests
    {
        private async Task RunCompletedProducerReviewTestsAsync()
        {
            await RunTest("Completed producer review continuation refuses accepted or changed evidence", async () =>
            {
                foreach (string variant in new[] { "producer_landed_fact", "producer_landed_event", "planner_landed_event", "review_completed_event",
                    "producer_source_changed", "unapproved_producer", "wrong_review_role", "changed_dependency",
                    "consume_changed_producer", "consume_landed_producer" })
                {
                    using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                    {
                        HistoricalFixture fixture = await SeedHistoricalPermitAsync(harness, true).ConfigureAwait(false);
                        Mission producer = fixture.Base.Stages[1];
                        Mission review = fixture.Base.Stages[2];
                        string endpoint = "/api/v1/voyages/" + fixture.Base.Voyage.Id + "/administrative-completion-correction";
                        AdministrativeVoyageRecoveryPlan initial = await HistoricalPreviewAsync(harness, fixture).ConfigureAwait(false);
                        fixture.Disposition.GraphFingerprint = initial.Fingerprint;
                        AdministrativeVoyageRecoveryPlan signed = await HistoricalPreviewAsync(harness, fixture).ConfigureAwait(false);
                        AssertEqual(0, signed.Blockers.Count, variant + ": " + String.Join(",", signed.Blockers));
                        if (variant.StartsWith("consume_", StringComparison.Ordinal))
                        {
                            SurfaceReply issue = await harness.RestAsync(HttpMethod.Post, endpoint,
                                new { dryRun = false, sourceCommit = fixture.Base.Source, historicalWork = fixture.Disposition,
                                    expectedFingerprint = signed.Fingerprint,
                                    restartDescription = "Continue the preserved source; retain every role and charge." }).ConfigureAwait(false);
                            AssertEqual(200, issue.Status, issue.Body);
                            AdministrativeVoyageRecoveryPlan issued = JsonSerializer.Deserialize<AdministrativeVoyageRecoveryPlan>(issue.Body, ReplayReplyJson)!;
                            if (variant == "consume_changed_producer")
                            {
                                producer.RecoveryAttempts++;
                                await harness.Driver.Missions.UpdateAsync(producer).ConfigureAwait(false);
                            }
                            else
                            {
                                await harness.Driver.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
                                { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                                    MissionId = producer.Id, RootMissionId = producer.Id, VoyageId = producer.VoyageId,
                                    VesselId = producer.VesselId, FactType = MissionAttemptFactTypeEnum.Landed }).ConfigureAwait(false);
                            }
                            SurfaceReply consume = await harness.RestAsync(HttpMethod.Post,
                                "/api/v1/missions/" + review.Id + "/restart",
                                new { administrativeRestartPermitId = issued.RestartPermitId }).ConfigureAwait(false);
                            AssertEqual(409, consume.Status, variant + ": " + consume.Body);
                            AssertEqual(0, (await harness.Driver.Events.EnumerateByTypeAsync("mission.administrative_restart_consumed").ConfigureAwait(false)).Count);
                            AssertEqual(MissionStatusEnum.Cancelled, (await harness.Driver.Missions.ReadAsync(review.Id).ConfigureAwait(false))!.Status);
                            AssertEqual(producer.RecoveryAttempts, (await harness.Driver.Missions.ReadAsync(producer.Id).ConfigureAwait(false))!.RecoveryAttempts);
                            continue;
                        }

                        if (variant == "producer_landed_fact")
                            await harness.Driver.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
                            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                                MissionId = producer.Id, RootMissionId = producer.Id, VoyageId = producer.VoyageId,
                                VesselId = producer.VesselId, FactType = MissionAttemptFactTypeEnum.Landed }).ConfigureAwait(false);
                        if (variant == "producer_landed_event" || variant == "planner_landed_event" || variant == "review_completed_event")
                            await harness.Driver.Events.CreateAsync(new ArmadaEvent(
                                variant == "review_completed_event" ? "mission.completed" : "mission.landed", "Recorded acceptance fact")
                            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                                MissionId = variant == "planner_landed_event" ? fixture.Base.Stages[0].Id
                                    : variant == "producer_landed_event" ? producer.Id : review.Id,
                                VoyageId = producer.VoyageId }).ConfigureAwait(false);
                        if (variant == "producer_source_changed")
                        {
                            producer.CommitHash = fixture.Base.Source;
                            await harness.Driver.Missions.UpdateAsync(producer).ConfigureAwait(false);
                        }
                        if (variant == "unapproved_producer")
                        {
                            producer.ReviewedUtc = null;
                            await harness.Driver.Missions.UpdateAsync(producer).ConfigureAwait(false);
                        }
                        if (variant == "wrong_review_role")
                        {
                            review.Persona = "Audit";
                            await harness.Driver.Missions.UpdateAsync(review).ConfigureAwait(false);
                        }
                        if (variant == "changed_dependency")
                        {
                            fixture.Base.Stages[5].DependsOnMissionId = producer.Id;
                            await harness.Driver.Missions.UpdateAsync(fixture.Base.Stages[5]).ConfigureAwait(false);
                        }

                        AdministrativeVoyageRecoveryPlan changed = await HistoricalPreviewAsync(harness, fixture).ConfigureAwait(false);
                        fixture.Disposition.GraphFingerprint = changed.Fingerprint;
                        AdministrativeVoyageRecoveryPlan fresh = await HistoricalPreviewAsync(harness, fixture).ConfigureAwait(false);
                        string blocker = variant == "producer_landed_fact" ? "landed_attempt_record:"
                            : variant == "producer_landed_event" || variant == "planner_landed_event" || variant == "review_completed_event" ? "conflicting_accepted_native_outcome:"
                            : variant == "changed_dependency" ? "exact_linear_stage_graph_required:"
                            : "repository_source_evidence_required";
                        AssertTrue(fresh.Blockers.Any(value => value.StartsWith(blocker, StringComparison.Ordinal)),
                            variant + ": a fresh signed graph still refuses its actual evidence conflict: " + String.Join(",", fresh.Blockers));
                        string[] before = (await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Base.Voyage.Id).ConfigureAwait(false))
                            .OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => JsonSerializer.Serialize(m)).ToArray();
                        SurfaceReply apply = await harness.RestAsync(HttpMethod.Post, endpoint,
                            new { dryRun = false, sourceCommit = fixture.Base.Source, historicalWork = fixture.Disposition,
                                expectedFingerprint = fresh.Fingerprint,
                                restartDescription = "Continue the preserved source; retain every role and charge." }).ConfigureAwait(false);
                        AssertEqual(409, apply.Status, variant + ": " + apply.Body);
                        string[] after = (await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Base.Voyage.Id).ConfigureAwait(false))
                            .OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => JsonSerializer.Serialize(m)).ToArray();
                        AssertTrue(before.SequenceEqual(after), variant + ": refusal changes no stage history or counter");
                        AssertEqual(VoyageStatusEnum.Complete, (await harness.Driver.Voyages.ReadAsync(fixture.Base.Voyage.Id).ConfigureAwait(false))!.Status);
                        AssertEqual(0, (await harness.Driver.Events.EnumerateByTypeAsync("mission.administrative_restart_permit_issued").ConfigureAwait(false)).Count);
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
