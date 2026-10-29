namespace Armada.Test.Database
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Provider-backed append-only production fact store tests.</summary>
    internal sealed class ProductionFactDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;

        internal ProductionFactDatabaseTests(DatabaseDriver driver, DatabaseSettings settings)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        internal async Task VerifyMissionAttemptFactsAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string tenant = "ten_fact_" + suffix;
            string otherTenant = "ten_other_" + suffix;
            DateTime baseUtc = new DateTime(2031, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc);

            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(tenant, "msn_a_" + suffix, MissionAttemptFactTypeEnum.AttemptStarted, false, null, baseUtc), token).ConfigureAwait(false);
            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(tenant, "msn_b_" + suffix, MissionAttemptFactTypeEnum.Retried, true, "Transient Captain Failure!", baseUtc.AddMinutes(1)), token).ConfigureAwait(false);
            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(tenant, "msn_c_" + suffix, MissionAttemptFactTypeEnum.Landed, false, "landed", baseUtc.AddMinutes(2)), token).ConfigureAwait(false);
            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(tenant, "msn_late_" + suffix, MissionAttemptFactTypeEnum.Failed, false, null, baseUtc.AddHours(2)), token).ConfigureAwait(false);
            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(otherTenant, "msn_other_" + suffix, MissionAttemptFactTypeEnum.AttemptStarted, false, null, baseUtc.AddMinutes(1)), token).ConfigureAwait(false);

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                ProductionFactPage<MissionAttemptFact> page = await reopened.MissionAttemptFacts.EnumerateAsync(new ProductionFactQuery
                {
                    TenantId = tenant,
                    FromUtc = baseUtc,
                    ToUtc = baseUtc.AddHours(1)
                }, token).ConfigureAwait(false);
                DatabaseAssert.Equal(3, page.Items.Count, "Window and tenant bound the fact scan");
                DatabaseAssert.True(!page.Truncated, "An unbounded page is not truncated");
                DatabaseAssert.Equal("msn_a_" + suffix, page.Items[0].MissionId, "Facts return in creation order");
                MissionAttemptFact retried = page.Items[1];
                DatabaseAssert.Equal(MissionAttemptFactTypeEnum.Retried, retried.FactType, "Fact type survives reopen");
                DatabaseAssert.True(retried.IsRescue, "Rescue marker survives reopen");
                DatabaseAssert.Equal("transient_captain_failure_", retried.ReasonCode, "Reason code is stored in bounded machine form");
                DatabaseAssert.Equal("root_" + suffix, retried.RootMissionId, "Root mission identity survives reopen");
                DatabaseAssert.True(Math.Abs((retried.CreatedUtc - baseUtc.AddMinutes(1)).TotalMilliseconds) < 1.5, "Creation time keeps millisecond precision");
                DatabaseAssert.Equal(DateTimeKind.Utc, retried.CreatedUtc.Kind, "Creation time reads as UTC");

                ProductionFactPage<MissionAttemptFact> bounded = await reopened.MissionAttemptFacts.EnumerateAsync(new ProductionFactQuery
                {
                    TenantId = tenant,
                    FromUtc = baseUtc,
                    ToUtc = baseUtc.AddDays(1),
                    Limit = 2
                }, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2, bounded.Items.Count, "Limit bounds the page");
                DatabaseAssert.True(bounded.Truncated, "A bounded page reports truncation");
                DatabaseAssert.True(bounded.Items.All(item => item.TenantId == tenant), "Tenant scope excludes other tenants");
            }
        }

        internal async Task VerifyMissionAttemptFactsByMissionAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string tenant = "ten_mission_fact_" + suffix;
            string otherTenant = "ten_other_mission_fact_" + suffix;
            string missionId = "msn_exact_" + suffix;
            DateTime startUtc = new DateTime(2034, 6, 7, 8, 9, 10, DateTimeKind.Utc);
            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(tenant, missionId, MissionAttemptFactTypeEnum.AttemptStarted, false, null, startUtc), token).ConfigureAwait(false);
            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(tenant, missionId, MissionAttemptFactTypeEnum.Restarted, false, null, startUtc.AddMinutes(1)), token).ConfigureAwait(false);
            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(tenant, missionId, MissionAttemptFactTypeEnum.Landed, false, null, startUtc.AddMinutes(2)), token).ConfigureAwait(false);
            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(otherTenant, missionId, MissionAttemptFactTypeEnum.Failed, false, null, startUtc.AddMinutes(3)), token).ConfigureAwait(false);
            await _Driver.MissionAttemptFacts.CreateAsync(NewFact(tenant, "msn_unrelated_" + suffix, MissionAttemptFactTypeEnum.Failed, false, null, startUtc.AddMinutes(4)), token).ConfigureAwait(false);

            ProductionFactPage<MissionAttemptFact> page = await _Driver.MissionAttemptFacts
                .EnumerateByMissionAsync(tenant, missionId, 2, token).ConfigureAwait(false);
            DatabaseAssert.Equal(2, page.Items.Count, "Mission query returns its bounded newest window");
            DatabaseAssert.True(page.Truncated, "Mission query reports older matching facts beyond the limit");
            DatabaseAssert.Equal(MissionAttemptFactTypeEnum.Restarted, page.Items[0].FactType, "Newest window returns in ascending creation order");
            DatabaseAssert.Equal(MissionAttemptFactTypeEnum.Landed, page.Items[1].FactType, "Newest matching fact is retained");
            DatabaseAssert.True(page.Items.All(item => item.TenantId == tenant && item.MissionId == missionId), "Mission query applies exact tenant and mission filters");
        }

        internal async Task VerifyMissionAttemptProvenanceAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string tenant = "ten_prov_" + suffix;
            string otherTenant = "ten_other_prov_" + suffix;
            string missionId = "msn_prov_" + suffix;
            DateTime startUtc = new DateTime(2035, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            MissionAttemptProvenance first = await _Driver.MissionAttempts.CreateAsync(NewAttempt(tenant, missionId, 101, startUtc), token).ConfigureAwait(false);
            MissionAttemptProvenance second = await _Driver.MissionAttempts.CreateAsync(NewAttempt(tenant, missionId, 102, startUtc.AddMinutes(10)), token).ConfigureAwait(false);
            MissionAttemptProvenance third = await _Driver.MissionAttempts.CreateAsync(NewAttempt(tenant, missionId, 103, startUtc.AddMinutes(20)), token).ConfigureAwait(false);
            await _Driver.MissionAttempts.CreateAsync(NewAttempt(tenant, "msn_prov_unrelated_" + suffix, 101, startUtc.AddMinutes(40)), token).ConfigureAwait(false);

            DateTime endUtc = startUtc.AddMinutes(5);
            int wrongProcess = await _Driver.MissionAttempts.CloseAsync(missionId, 999, MissionAttemptOutcomeEnum.Completed, 0, null, endUtc, token).ConfigureAwait(false);
            DatabaseAssert.Equal(0, wrongProcess, "Close matches the process id of the running row");
            int closed = await _Driver.MissionAttempts.CloseAsync(missionId, 101, MissionAttemptOutcomeEnum.Failed, 7, "Exit Nonzero", endUtc, token).ConfigureAwait(false);
            DatabaseAssert.Equal(1, closed, "Close ends exactly the matching running row of the mission");
            int closedAgain = await _Driver.MissionAttempts.CloseAsync(missionId, 101, MissionAttemptOutcomeEnum.Completed, 0, null, endUtc.AddMinutes(1), token).ConfigureAwait(false);
            DatabaseAssert.Equal(0, closedAgain, "A closed row is never rewritten");

            int interrupted = await _Driver.MissionAttempts.InterruptRunningAsync(missionId, "superseded_by_relaunch", startUtc.AddMinutes(25), token).ConfigureAwait(false);
            DatabaseAssert.Equal(2, interrupted, "Interrupt ends every row of the mission still running");
            await _Driver.MissionAttempts.CreateAsync(NewAttempt(otherTenant, missionId, 101, startUtc.AddMinutes(30)), token).ConfigureAwait(false);

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                ProductionFactPage<MissionAttemptProvenance> page = await reopened.MissionAttempts.EnumerateByMissionAsync(tenant, missionId, 100, token).ConfigureAwait(false);
                DatabaseAssert.Equal(3, page.Items.Count, "Enumerate returns this tenant's attempts of the mission only");
                DatabaseAssert.True(!page.Truncated, "A page inside the limit is not truncated");
                DatabaseAssert.Equal(first.Id, page.Items[0].Id, "Attempts return oldest first");
                DatabaseAssert.Equal(third.Id, page.Items[2].Id, "Attempts return oldest first");

                MissionAttemptProvenance failed = page.Items[0];
                DatabaseAssert.Equal(MissionAttemptOutcomeEnum.Failed, failed.Outcome, "Outcome survives reopen");
                DatabaseAssert.Equal(7, failed.ExitCode, "Exit code survives reopen");
                DatabaseAssert.Equal("exit_nonzero", failed.ReasonCode, "Reason code is bounded to a machine code");
                DatabaseAssert.Equal(endUtc, failed.EndedUtc, "End time survives reopen");
                DatabaseAssert.Equal("cap_prov", failed.CaptainId, "Captain survives reopen");
                DatabaseAssert.Equal("ClaudeCode", failed.Runtime, "Runtime survives reopen");
                DatabaseAssert.Equal("model-x", failed.Model, "Model survives reopen");
                DatabaseAssert.Equal("1.2.3 (Test CLI)", failed.RuntimeVersion, "Runtime version survives reopen");
                DatabaseAssert.Equal(101, failed.ProcessId, "Process id survives reopen");

                DatabaseAssert.Equal(MissionAttemptOutcomeEnum.Interrupted, page.Items[1].Outcome, "A superseded row reads Interrupted");
                DatabaseAssert.Equal("superseded_by_relaunch", page.Items[1].ReasonCode, "A superseded row names why");
                DatabaseAssert.True(page.Items[1].ExitCode == null, "An interrupted row holds no exit code");

                ProductionFactPage<MissionAttemptProvenance> bounded = await reopened.MissionAttempts.EnumerateByMissionAsync(tenant, missionId, 2, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2, bounded.Items.Count, "Enumerate returns its bounded newest window");
                DatabaseAssert.True(bounded.Truncated, "Enumerate reports older attempts beyond the limit");
                DatabaseAssert.Equal(second.Id, bounded.Items[0].Id, "The window keeps the newest attempts in ascending order");

                ProductionFactPage<MissionAttemptProvenance> foreign = await reopened.MissionAttempts.EnumerateByMissionAsync(otherTenant, missionId, 100, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, foreign.Items.Count, "Another tenant sees only its own attempt of the same mission id");
            }
        }

        internal async Task VerifyMissionAttemptProvenancePurgedWithMissionAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, false);
            try
            {
                DateTime startUtc = new DateTime(2035, 2, 3, 4, 5, 6, DateTimeKind.Utc);
                TenantMetadata tenant = await fixture.CreateTenantAsync("provpurge", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "provpurge", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "provpurge", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "provpurge", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "provpurge", token).ConfigureAwait(false);

                Mission byId = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "prov-by-id", token).ConfigureAwait(false);
                Mission byTenant = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "prov-by-tenant", token).ConfigureAwait(false);
                Mission byOwner = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "prov-by-owner", token).ConfigureAwait(false);
                Mission kept = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "prov-kept", token).ConfigureAwait(false);
                foreach (Mission mission in new[] { byId, byTenant, byOwner, kept })
                    await _Driver.MissionAttempts.CreateAsync(NewAttempt(tenant.Id, mission.Id, 201, startUtc), token).ConfigureAwait(false);

                await _Driver.Missions.DeleteAsync(byId.Id, token).ConfigureAwait(false);
                await _Driver.Missions.DeleteAsync(tenant.Id, byTenant.Id, token).ConfigureAwait(false);
                await _Driver.Missions.DeleteAsync(tenant.Id, user.Id, byOwner.Id, token).ConfigureAwait(false);

                foreach (Mission deleted in new[] { byId, byTenant, byOwner })
                {
                    ProductionFactPage<MissionAttemptProvenance> page = await _Driver.MissionAttempts.EnumerateByMissionAsync(tenant.Id, deleted.Id, 100, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(0, page.Items.Count, "Deleting a mission deletes its attempt rows: " + deleted.Title);
                }
                ProductionFactPage<MissionAttemptProvenance> keptPage = await _Driver.MissionAttempts.EnumerateByMissionAsync(tenant.Id, kept.Id, 100, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, keptPage.Items.Count, "Another mission keeps its attempt rows");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyCheckRegressionLinksAsync(CancellationToken token)
        {
            CheckRun created = await _Driver.CheckRuns.CreateAsync(new CheckRun
            {
                Command = "dotnet build consumer",
                Status = CheckRunStatusEnum.Failed,
                RegressionPurpose = RegressionPurposeEnum.Consumer,
                RegressionObjectiveId = "obj_regression_db",
                RegressionLandedCommit = "0123456789abcdef"
            }, token).ConfigureAwait(false);
            CheckRun plain = await _Driver.CheckRuns.CreateAsync(new CheckRun { Command = "dotnet build" }, token).ConfigureAwait(false);

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                CheckRun? stored = await reopened.CheckRuns.ReadAsync(created.Id, null, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(stored, "Check with regression links survives reopen");
                DatabaseAssert.Equal(RegressionPurposeEnum.Consumer, stored!.RegressionPurpose, "Regression purpose round-trips");
                DatabaseAssert.Equal("obj_regression_db", stored.RegressionObjectiveId, "Regression objective round-trips");
                DatabaseAssert.Equal("0123456789abcdef", stored.RegressionLandedCommit, "Landed commit round-trips");

                stored.RegressionPurpose = RegressionPurposeEnum.Ledger;
                stored.RegressionObjectiveId = null;
                await reopened.CheckRuns.UpdateAsync(stored, token).ConfigureAwait(false);
                CheckRun? updated = await reopened.CheckRuns.ReadAsync(created.Id, null, token).ConfigureAwait(false);
                DatabaseAssert.Equal(RegressionPurposeEnum.Ledger, updated!.RegressionPurpose, "Update changes the purpose");
                DatabaseAssert.True(updated.RegressionObjectiveId == null, "Update clears the objective link");

                CheckRun? unlinked = await reopened.CheckRuns.ReadAsync(plain.Id, null, token).ConfigureAwait(false);
                DatabaseAssert.Equal(RegressionPurposeEnum.None, unlinked!.RegressionPurpose, "A Check without links reads as None");
            }
        }

        internal async Task VerifyPreparationClaimObservationsAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string tenant = "ten_claim_" + suffix;
            DateTime baseUtc = new DateTime(2032, 2, 3, 4, 5, 6, 789, DateTimeKind.Utc);
            string fingerprint = new string('b', 64);
            foreach (PreparationClaimObservationEnum observation in new[] { PreparationClaimObservationEnum.Established, PreparationClaimObservationEnum.Reestablished, PreparationClaimObservationEnum.Reused })
            {
                await _Driver.PreparationClaimObservations.CreateAsync(new PreparationClaimObservation
                {
                    TenantId = tenant,
                    UserId = "usr_claim",
                    ObjectiveId = "obj_claim_" + suffix,
                    ClaimId = "opc_claim",
                    ClaimKind = ObjectivePreparationClaimKindEnum.ResponseRule,
                    SourceFamily = "Port Family",
                    VoyageId = observation == PreparationClaimObservationEnum.Reused ? "vyg_claim" : null,
                    SourceCommit = "0123abcd",
                    TargetCommit = null,
                    EvidenceFingerprint = fingerprint,
                    Observation = observation,
                    CreatedUtc = baseUtc.AddMinutes((int)observation)
                }, token).ConfigureAwait(false);
            }

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                ProductionFactPage<PreparationClaimObservation> page = await reopened.PreparationClaimObservations.EnumerateAsync(new ProductionFactQuery
                {
                    TenantId = tenant,
                    FromUtc = baseUtc,
                    ToUtc = baseUtc.AddHours(1)
                }, token).ConfigureAwait(false);
                DatabaseAssert.Equal(3, page.Items.Count, "Observations are scoped and windowed");
                PreparationClaimObservation reused = page.Items[2];
                DatabaseAssert.Equal(PreparationClaimObservationEnum.Reused, reused.Observation, "Observation type round-trips in creation order");
                DatabaseAssert.Equal(ObjectivePreparationClaimKindEnum.ResponseRule, reused.ClaimKind, "Claim kind round-trips");
                DatabaseAssert.Equal("port_family", reused.SourceFamily, "Source family is stored in bounded form");
                DatabaseAssert.Equal("vyg_claim", reused.VoyageId, "Voyage link round-trips");
                DatabaseAssert.Equal("0123abcd", reused.SourceCommit, "Source anchor round-trips");
                DatabaseAssert.True(reused.TargetCommit == null, "A missing target anchor stays null");
                DatabaseAssert.Equal(fingerprint, reused.EvidenceFingerprint, "Fingerprint round-trips");

                ProductionFactPage<PreparationClaimObservation> bounded = await reopened.PreparationClaimObservations.EnumerateAsync(new ProductionFactQuery
                {
                    TenantId = tenant,
                    FromUtc = baseUtc,
                    ToUtc = baseUtc.AddHours(1),
                    Limit = 1
                }, token).ConfigureAwait(false);
                DatabaseAssert.True(bounded.Truncated && bounded.Items.Count == 1, "A bounded observation page reports truncation");
            }
        }

        internal async Task VerifyLaneStateAndSlotRequestAsync(CancellationToken token)
        {
            DateTime baseUtc = new DateTime(2033, 3, 4, 5, 6, 7, 250, DateTimeKind.Utc);
            string lane = "vsl_lane_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "+vsl_sibling";
            await _Driver.LaneStateTransitions.CreateAsync(new LaneStateTransition
            {
                LaneKey = lane,
                EligibleCount = 2,
                Occupied = 1,
                Capacity = 3,
                BlockReason = LaneBlockReasonEnum.DispatchHold,
                EligibleSourceFamilies = "alpha,ecu",
                Checkpoint = true,
                ValidForSeconds = 900,
                CreatedUtc = baseUtc
            }, token).ConfigureAwait(false);
            CheckRun slotted = await _Driver.CheckRuns.CreateAsync(new CheckRun
            {
                Command = "dotnet test",
                SlotRequestedUtc = baseUtc,
                StartedUtc = baseUtc.AddSeconds(30)
            }, token).ConfigureAwait(false);

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                ProductionFactPage<LaneStateTransition> page = await reopened.LaneStateTransitions.EnumerateAsync(new ProductionFactQuery
                {
                    FromUtc = baseUtc,
                    ToUtc = baseUtc.AddSeconds(1)
                }, token).ConfigureAwait(false);
                LaneStateTransition stored = page.Items.Single(item => item.LaneKey == lane);
                DatabaseAssert.Equal(2, stored.EligibleCount, "Eligible count round-trips");
                DatabaseAssert.Equal(1, stored.Occupied, "Occupancy round-trips");
                DatabaseAssert.Equal(3, stored.Capacity, "Capacity round-trips");
                DatabaseAssert.Equal(LaneBlockReasonEnum.DispatchHold, stored.BlockReason, "Block reason round-trips");
                DatabaseAssert.Equal("alpha,ecu", stored.EligibleSourceFamilies, "Eligible families round-trip");
                DatabaseAssert.True(stored.Checkpoint, "Checkpoint flag round-trips");
                DatabaseAssert.Equal(900, stored.ValidForSeconds, "Trust window round-trips");

                CheckRun? check = await reopened.CheckRuns.ReadAsync(slotted.Id, null, token).ConfigureAwait(false);
                DatabaseAssert.True(check!.SlotRequestedUtc.HasValue, "Slot request time round-trips");
                DatabaseAssert.True(Math.Abs((check.SlotRequestedUtc!.Value - baseUtc).TotalSeconds) < 1.0, "Slot request time keeps its value");
                check.SlotRequestedUtc = null;
                await reopened.CheckRuns.UpdateAsync(check, token).ConfigureAwait(false);
                CheckRun? cleared = await reopened.CheckRuns.ReadAsync(slotted.Id, null, token).ConfigureAwait(false);
                DatabaseAssert.True(!cleared!.SlotRequestedUtc.HasValue, "Update clears the slot request time");
            }
        }

        private static MissionAttemptProvenance NewAttempt(string tenant, string missionId, int processId, DateTime startedUtc)
        {
            return new MissionAttemptProvenance
            {
                TenantId = tenant,
                UserId = "usr_prov",
                MissionId = missionId,
                VoyageId = "vyg_prov",
                VesselId = "vsl_prov",
                CaptainId = "cap_prov",
                Persona = "Worker",
                Runtime = "ClaudeCode",
                Model = "model-x",
                RuntimeVersion = "1.2.3 (Test CLI)",
                ProcessId = processId,
                StartedUtc = startedUtc,
                Outcome = MissionAttemptOutcomeEnum.Running
            };
        }

        private static MissionAttemptFact NewFact(string tenant, string missionId, MissionAttemptFactTypeEnum type, bool rescue, string? reason, DateTime createdUtc)
        {
            return new MissionAttemptFact
            {
                TenantId = tenant,
                UserId = "usr_fact",
                MissionId = missionId,
                VoyageId = "vyg_fact",
                VesselId = "vsl_fact",
                RootMissionId = "root_" + tenant.Substring(tenant.LastIndexOf('_') + 1),
                ParentMissionId = rescue ? "msn_parent" : null,
                FactType = type,
                IsRescue = rescue,
                ReasonCode = reason,
                CreatedUtc = createdUtc
            };
        }
    }
}
