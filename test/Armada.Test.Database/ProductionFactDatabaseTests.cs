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
                EligibleSourceFamilies = "dxp,ecu",
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
                DatabaseAssert.Equal("dxp,ecu", stored.EligibleSourceFamilies, "Eligible families round-trip");
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
