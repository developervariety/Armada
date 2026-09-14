namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Tests for durable preparation claim observations.</summary>
    public sealed class PreparationClaimObservationRecorderTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Preparation Claim Observation Recorder";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ClassificationSeparatesRepetitionFromRevalidationAndChange", () =>
            {
                DateTime before = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
                DateTime after = before.AddHours(1);
                Objective prior = Prepared("aaaa1111", "bbbb2222",
                    Claim("opc_same", ObjectivePreparationDependencyEnum.Target, "Decoder lives in the parser", before),
                    Claim("opc_changed", ObjectivePreparationDependencyEnum.Target, "Old statement", before),
                    Claim("opc_stale", ObjectivePreparationDependencyEnum.Target, "Stale statement", before, ObjectivePreparationClaimStateEnum.NeedsRecheck),
                    Claim("opc_idle", ObjectivePreparationDependencyEnum.Target, "Unchanged and not reverified", before),
                    Claim("opc_source", ObjectivePreparationDependencyEnum.Source, "Source-bound statement", before));
                PreparationClaimSnapshot snapshot = PreparationClaimObservationRecorder.Capture(prior);

                Objective current = Prepared("cccc3333", "bbbb2222",
                    Claim("opc_same", ObjectivePreparationDependencyEnum.Target, "Decoder lives in the parser", after),
                    Claim("opc_changed", ObjectivePreparationDependencyEnum.Target, "New statement", after),
                    Claim("opc_stale", ObjectivePreparationDependencyEnum.Target, "Stale statement", after),
                    Claim("opc_idle", ObjectivePreparationDependencyEnum.Target, "Unchanged and not reverified", before),
                    Claim("opc_source", ObjectivePreparationDependencyEnum.Source, "Source-bound statement", after),
                    Claim("opc_new", ObjectivePreparationDependencyEnum.None, "Brand new", after),
                    Claim("opc_pending", ObjectivePreparationDependencyEnum.None, "Needs work", after, ObjectivePreparationClaimStateEnum.NeedsRecheck));
                current.Id = prior.Id;

                Dictionary<string, PreparationClaimObservationEnum> byClaim = PreparationClaimObservationRecorder.Classify(snapshot, current)
                    .ToDictionary(item => item.ClaimId, item => item.Observation);

                AssertEqual(PreparationClaimObservationEnum.Reestablished, byClaim["opc_same"], "unchanged claim verified again on unmoved anchors is repetition");
                AssertEqual(PreparationClaimObservationEnum.Established, byClaim["opc_changed"], "a changed statement is a new claim version");
                AssertEqual(PreparationClaimObservationEnum.Revalidated, byClaim["opc_stale"], "a claim that needed a recheck is revalidated");
                AssertEqual(PreparationClaimObservationEnum.Revalidated, byClaim["opc_source"], "a claim whose own anchor moved is revalidated");
                AssertEqual(PreparationClaimObservationEnum.Established, byClaim["opc_new"]);
                AssertFalse(byClaim.ContainsKey("opc_idle"), "a claim that was not verified again produces nothing");
                AssertFalse(byClaim.ContainsKey("opc_pending"), "an unverified claim produces nothing");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ObjectiveWritesAndDispatchLinksRecordObservationsWithoutClaimText", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "UnitTest");
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                DateTime verifiedUtc = DateTime.UtcNow.AddMinutes(-30);
                DateTime windowStart = DateTime.UtcNow.AddMinutes(-5);
                const string secretText = "grep -r AKIA-private-query in the decoder";

                Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Prepared port slice",
                    Tags = new List<string> { "port:ecu" },
                    Preparation = new ObjectivePreparation
                    {
                        Source = new ObjectivePreparationAnchor { VesselId = "vsl_source", Ref = "main", ResolvedCommit = "aaaa1111" },
                        Target = new ObjectivePreparationAnchor { VesselId = "vsl_target", Ref = "main", ResolvedCommit = "bbbb2222" },
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            Claim("opc_reused", ObjectivePreparationDependencyEnum.Target, secretText, verifiedUtc)
                        }
                    }
                }).ConfigureAwait(false);

                ObjectivePreparation again = created.Preparation!;
                again.Claims[0].VerifiedUtc = DateTime.UtcNow;
                await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest { Preparation = again }).ConfigureAwait(false);

                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("dispatch")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);
                await objectives.LinkVoyageAsync(auth, created.Id, voyage.Id).ConfigureAwait(false);
                await objectives.LinkVoyageAsync(auth, created.Id, voyage.Id).ConfigureAwait(false);

                ProductionFactPage<PreparationClaimObservation> page = await testDb.Driver.PreparationClaimObservations.EnumerateAsync(new ProductionFactQuery
                {
                    FromUtc = windowStart,
                    ToUtc = DateTime.UtcNow.AddMinutes(1)
                }).ConfigureAwait(false);
                List<PreparationClaimObservationEnum> kinds = page.Items.Select(item => item.Observation).ToList();
                AssertEqual(3, kinds.Count, "create, re-verification, and one dispatch link each record once");
                AssertEqual(PreparationClaimObservationEnum.Established, kinds[0]);
                AssertEqual(PreparationClaimObservationEnum.Reestablished, kinds[1]);
                AssertEqual(PreparationClaimObservationEnum.Reused, kinds[2]);
                AssertEqual(voyage.Id, page.Items[2].VoyageId);
                AssertEqual("ecu", page.Items[2].SourceFamily);
                AssertEqual("bbbb2222", page.Items[2].TargetCommit, "the immutable anchor is stored");
                string serialized = JsonSerializer.Serialize(page.Items);
                AssertFalse(serialized.Contains("AKIA", StringComparison.Ordinal), "claim text is never stored");
                AssertEqual(64, page.Items[0].EvidenceFingerprint.Length);
            }).ConfigureAwait(false);
        }

        private static Objective Prepared(string sourceCommit, string targetCommit, params ObjectivePreparationClaim[] claims)
        {
            return new Objective
            {
                Title = "classification",
                Preparation = new ObjectivePreparation
                {
                    Source = new ObjectivePreparationAnchor { VesselId = "vsl_source", Ref = "main", ResolvedCommit = sourceCommit },
                    Target = new ObjectivePreparationAnchor { VesselId = "vsl_target", Ref = "main", ResolvedCommit = targetCommit },
                    Claims = claims.ToList()
                }
            };
        }

        private static ObjectivePreparationClaim Claim(
            string id,
            ObjectivePreparationDependencyEnum dependsOn,
            string text,
            DateTime verifiedUtc,
            ObjectivePreparationClaimStateEnum state = ObjectivePreparationClaimStateEnum.Verified)
        {
            return new ObjectivePreparationClaim
            {
                Id = id,
                Kind = ObjectivePreparationClaimKindEnum.SourcePath,
                Text = text,
                EvidenceLinks = new List<string> { "src/Decoder.cs" },
                DependsOn = dependsOn,
                State = state,
                VerifiedUtc = verifiedUtc
            };
        }
    }
}
