namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// An excluded vessel's content never leaves the host. Every shipped decision is driven through each of its
    /// seams twice with the provider counting calls: once about an allowed vessel, which must reach the provider,
    /// and once about a vessel on the exclusion list, which must send nothing and record the exclusion. A decision
    /// added later with no row here fails the completeness case until it passes its vessel to the exclusion check
    /// or states why it has none. This is a behavioural check: it runs the seams and counts provider calls.
    /// </summary>
    public sealed class TypedDecisionVesselExclusionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Typed Decision Vessel Exclusion";

        private const string ExcludedVesselId = "vsl_excluded";

        // Decisions whose seams never know a vessel, and why. Every shipped decision knows one today.
        private static readonly Dictionary<string, string> NoVessel = new Dictionary<string, string>(StringComparer.Ordinal);

        private sealed class DecisionRecord
        {
            [JsonPropertyName("decision")]
            public string? Decision { get; set; }

            [JsonPropertyName("unavailable_reason")]
            public string? UnavailableReason { get; set; }
        }

        private sealed class DriveOutcome
        {
            public int Calls { get; init; }
            public List<string> Reasons { get; init; } = new List<string>();
        }

        private static PapercutGroup PapercutOf(string vesselId, string key)
        {
            return new PapercutGroup
            {
                Key = vesselId + "|BriefContradiction|" + key,
                VesselId = vesselId,
                Category = PapercutCategoryEnum.BriefContradiction,
                HighestSeverity = PapercutSeverityEnum.High,
                SampleTitle = "The brief contradicts the decoder layout " + key,
                SampleDetail = "The decoder reads frames in order.",
                Count = 3,
                DistinctCaptainCount = 2,
                LastSeenUtc = DateTime.UtcNow
            };
        }

        // The seams the subject-link table leaves out because their subject is a papercut group, not a mission.
        private static List<TypedDecisionSeams.SeamDriver> PapercutSeams()
        {
            return new List<TypedDecisionSeams.SeamDriver>
            {
                new TypedDecisionSeams.SeamDriver("papercut_merge", "listing", null, async (TypedDecisionSeams.SeamContext ctx) =>
                {
                    PapercutMergeAdapter adapter = new PapercutMergeAdapter(ctx.Typed, ctx.Client, ctx.Recorder, ctx.Database, TypedDecisionSeams.Quiet());
                    await adapter.MergeAsync(new List<PapercutGroup> { PapercutOf(ctx.VesselId, "a"), PapercutOf(ctx.VesselId, "b") }, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),
                new TypedDecisionSeams.SeamDriver("memory_candidate", "sweep", null, async (TypedDecisionSeams.SeamContext ctx) =>
                {
                    MemoryCandidateAdapter adapter = new MemoryCandidateAdapter(ctx.Typed, ctx.Client, ctx.Recorder,
                        new DatabaseMemoryCandidateProposalWriter(ctx.Database, TypedDecisionSeams.Quiet()), TypedDecisionSeams.Quiet());
                    await adapter.NominateAsync(new List<PapercutGroup> { PapercutOf(ctx.VesselId, "a") }, CancellationToken.None).ConfigureAwait(false);
                    return null;
                })
            };
        }

        private static List<TypedDecisionSeams.SeamDriver> Seams()
        {
            return TypedDecisionSeams.All().Concat(PapercutSeams()).ToList();
        }

        private static ArmadaSettings ExcludingSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.TypedDecisions.Mode = TypedDecisionModeEnum.Gate;
            settings.TypedDecisions.CaptainTool.Enabled = true;
            settings.TypedDecisions.EgressExcludedVesselIds = new List<string> { ExcludedVesselId };
            return settings;
        }

        // Drive one seam about one vessel and return the provider calls and the unavailable reasons it recorded.
        private static async Task<DriveOutcome> DriveAsync(TypedDecisionSeams.SeamDriver seam, string vesselId)
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                // The provider is down, so a state that is sent costs nothing downstream; the call count is the
                // measure of what would have left the host.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                ArmadaSettings settings = ExcludingSettings();
                await seam.Drive(new TypedDecisionSeams.SeamContext
                {
                    Database = db.Driver,
                    Settings = settings,
                    Client = client,
                    Recorder = new TypedDecisionRecorder(db.Driver, TypedDecisionSeams.Quiet()),
                    VesselId = vesselId
                }).ConfigureAwait(false);

                List<string> reasons = new List<string>();
                foreach (string type in new[] { TypedDecisionRecorder.EventTypeUnavailable, TypedDecisionRecorder.EventTypeCaptain })
                {
                    foreach (ArmadaEvent evt in await db.Driver.Events.EnumerateByTypeAsync(type, 100).ConfigureAwait(false))
                    {
                        DecisionRecord? record = JsonSerializer.Deserialize<DecisionRecord>(evt.Payload ?? "{}");
                        if (record == null || record.Decision != seam.DecisionPoint) continue;
                        if (!String.IsNullOrWhiteSpace(record.UnavailableReason)) reasons.Add(record.UnavailableReason!);
                    }
                }
                return new DriveOutcome { Calls = client.CallCount, Reasons = reasons };
            }
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("EveryShippedDecision_PassesItsVesselToTheExclusionCheck_OrStatesWhyItHasNone", () =>
            {
                HashSet<string> covered = new HashSet<string>(Seams().Select(seam => seam.DecisionPoint), StringComparer.Ordinal);
                List<string> missing = TypedDecisionSettings.ShippedDecisionNames
                    .Where(name => !covered.Contains(name) && !NoVessel.ContainsKey(name))
                    .ToList();
                AssertEqual(0, missing.Count, "decisions with no vessel-exclusion driver and no stated reason: " + String.Join(", ", missing));
                foreach (string name in NoVessel.Keys)
                    AssertFalse(covered.Contains(name), name + " is listed as having no vessel, so it must not also have a driver");
            });

            foreach (TypedDecisionSeams.SeamDriver seam in Seams())
            {
                string label = seam.DecisionPoint + "/" + seam.Seam;

                await RunTest("Vessel_" + label + "_AnAllowedVesselReachesTheProvider", async () =>
                {
                    DriveOutcome allowed = await DriveAsync(seam, TypedDecisionSeams.AllowedVesselId).ConfigureAwait(false);
                    AssertTrue(allowed.Calls >= 1, label + " sends a state about an allowed vessel, so a refusal below is the guard's (calls=" + allowed.Calls + ")");
                }).ConfigureAwait(false);

                await RunTest("Vessel_" + label + "_AnExcludedVesselSendsNothing_AndRecordsWhy", async () =>
                {
                    DriveOutcome excluded = await DriveAsync(seam, ExcludedVesselId).ConfigureAwait(false);
                    AssertEqual(0, excluded.Calls, label + " sends nothing about a vessel on the exclusion list");
                    AssertTrue(excluded.Reasons.Contains(TypedDecisionEgress.ExcludedVesselReason),
                        label + " records " + TypedDecisionEgress.ExcludedVesselReason + " (recorded: " + String.Join(",", excluded.Reasons) + ")");
                }).ConfigureAwait(false);
            }
        }
    }
}
