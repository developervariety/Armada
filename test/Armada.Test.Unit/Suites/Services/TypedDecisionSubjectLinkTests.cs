namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Services.TypedDecisions;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Every typed-decision event and retained sample names the objective and mission it judged, wherever the
    /// calling seam knows them, so a retained state can be joined to the records it was about. The census drives
    /// each shipped decision through its real seam with known ids and reads back what was recorded: a behavioural
    /// check, not a source-text guard. A decision added later with no row here fails the completeness case until
    /// it records its subject or states why it has none. The ids are record links only, so the transmitted state,
    /// and therefore its hash, must not change with them.
    /// </summary>
    public sealed class TypedDecisionSubjectLinkTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Typed Decision Subject Links";

        private const string ObjectiveId = TypedDecisionSeams.ObjectiveId;
        private const string MissionId = TypedDecisionSeams.MissionId;
        private const string VesselId = TypedDecisionSeams.AllowedVesselId;

        // The decisions whose subject is not one objective or one mission, and why. Each still records its call.
        private static readonly Dictionary<string, string> NoSubject = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["papercut_merge"] = "it compares two papercut groups, each gathered across many missions",
            ["memory_candidate"] = "it reads one papercut group gathered across many missions"
        };

        #region Records

        // The subject fields of one recorded decision event: the payload as written, plus the mission column.
        private sealed class RecordedSubject
        {
            [JsonPropertyName("decision")]
            public string? Decision { get; set; }

            [JsonPropertyName("objective_id")]
            public string? ObjectiveId { get; set; }

            [JsonPropertyName("mission_id")]
            public string? PayloadMissionId { get; set; }

            [JsonPropertyName("state_sha256")]
            public string? StateSha256 { get; set; }

            [JsonIgnore]
            public string? MissionColumn { get; set; }
        }

        // The subject fields of one retained sample line.
        private sealed class RetainedSubject
        {
            [JsonPropertyName("objective_id")]
            public string? ObjectiveId { get; set; }

            [JsonPropertyName("mission_id")]
            public string? MissionId { get; set; }
        }

        #endregion

        #region Helpers

        // Every shipped decision is on and retains its state, so each call writes an event and a sample.
        private static ArmadaSettings RetainingSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.TypedDecisions.Mode = TypedDecisionModeEnum.Gate;
            settings.TypedDecisions.CaptainTool.Enabled = true;
            settings.TypedDecisions.Retention.Enabled = true;
            foreach (string name in TypedDecisionSettings.ShippedDecisionNames)
                settings.TypedDecisions.Decisions[name].RetainState = true;
            return settings;
        }

        private static readonly string[] _EventTypes =
        {
            TypedDecisionRecorder.EventTypeGated,
            TypedDecisionRecorder.EventTypeShadow,
            TypedDecisionRecorder.EventTypeUnavailable,
            TypedDecisionRecorder.EventTypeCaptain
        };

        private static async Task<List<RecordedSubject>> DecisionEventsAsync(DatabaseDriver database, string decisionPoint)
        {
            List<RecordedSubject> found = new List<RecordedSubject>();
            foreach (string type in _EventTypes)
            {
                foreach (ArmadaEvent evt in await database.Events.EnumerateByTypeAsync(type, 100).ConfigureAwait(false))
                {
                    RecordedSubject? subject = JsonSerializer.Deserialize<RecordedSubject>(evt.Payload ?? "{}");
                    if (subject == null || subject.Decision != decisionPoint) continue;
                    subject.MissionColumn = evt.MissionId;
                    found.Add(subject);
                }
            }
            return found;
        }

        // The retained lines for one decision, read field by field so a field the store never wrote reads as absent.
        private static List<RetainedSubject> RetainedSamples(TypedDecisionSampleStore store, string decisionPoint)
        {
            List<RetainedSubject> samples = new List<RetainedSubject>();
            string folder = Path.Combine(store.RootPath, decisionPoint);
            if (!Directory.Exists(folder)) return samples;
            foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl").OrderBy(x => x, StringComparer.Ordinal))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    RetainedSubject? sample = JsonSerializer.Deserialize<RetainedSubject>(line);
                    if (sample != null) samples.Add(sample);
                }
            }
            return samples;
        }

        private static string NewTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada-subject-link-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // One call through a seam: the state the provider received, and what the event recorded.
        private sealed class CallRecord
        {
            public int Events { get; init; }
            public string StateText { get; init; } = String.Empty;
            public string? StateSha256 { get; init; }
            public string? ObjectiveId { get; init; }
            public string? MissionId { get; init; }
        }

        private static async Task<CallRecord> CallOnceAsync(string decisionPoint, Func<ITypedDecisionClient, TypedDecisionRecorder, TypedDecisionSettings, Task> call)
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                ArmadaSettings settings = RetainingSettings();
                await call(client, new TypedDecisionRecorder(db.Driver, TypedDecisionSeams.Quiet()), settings.TypedDecisions).ConfigureAwait(false);

                List<RecordedSubject> events = await DecisionEventsAsync(db.Driver, decisionPoint).ConfigureAwait(false);
                RecordedSubject? first = events.FirstOrDefault();
                return new CallRecord
                {
                    Events = events.Count,
                    StateText = FakeTypedDecisionClient.StateText(client.LastRequest),
                    StateSha256 = first?.StateSha256,
                    ObjectiveId = first?.ObjectiveId,
                    MissionId = first?.MissionColumn
                };
            }
        }

        #endregion

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("EveryShippedDecision_RecordsItsSubject_OrStatesWhyItHasNone", () =>
            {
                HashSet<string> covered = new HashSet<string>(TypedDecisionSeams.All().Select(driver => driver.DecisionPoint), StringComparer.Ordinal);
                List<string> missing = TypedDecisionSettings.ShippedDecisionNames
                    .Where(name => !covered.Contains(name) && !NoSubject.ContainsKey(name))
                    .ToList();
                AssertEqual(0, missing.Count, "decisions with no subject-link driver and no stated reason: " + String.Join(", ", missing));
                foreach (string name in NoSubject.Keys)
                    AssertFalse(covered.Contains(name), name + " is listed as having no subject, so it must not also have a driver");
            });

            foreach (TypedDecisionSeams.SeamDriver driver in TypedDecisionSeams.All())
            {
                string label = driver.DecisionPoint + "/" + driver.Seam;
                await RunTest("Subject_" + label + "_IsOnTheEventAndTheRetainedSample", async () =>
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        string dataDirectory = NewTempDir();
                        try
                        {
                            ArmadaSettings settings = RetainingSettings();
                            TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, TypedDecisionSeams.Quiet());
                            TypedDecisionRecorder recorder = new TypedDecisionRecorder(db.Driver, TypedDecisionSeams.Quiet(), store, () => settings.TypedDecisions);
                            // The provider is down, so every seam records exactly one unavailable call and keeps its rule.
                            FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));

                            string? expectedMission = await driver.Drive(new TypedDecisionSeams.SeamContext
                            {
                                Database = db.Driver,
                                Settings = settings,
                                Client = client,
                                Recorder = recorder
                            }).ConfigureAwait(false);

                            List<RecordedSubject> events = await DecisionEventsAsync(db.Driver, driver.DecisionPoint).ConfigureAwait(false);
                            AssertTrue(events.Count >= 1, label + " recorded a decision event, so the checks below read a real call");
                            foreach (RecordedSubject subject in events)
                            {
                                AssertEqual(driver.ExpectedObjectiveId, subject.ObjectiveId, label + " event objective_id");
                                AssertEqual(expectedMission, subject.MissionColumn, label + " event mission column");
                                AssertEqual(expectedMission, subject.PayloadMissionId, label + " event payload mission_id");
                            }

                            List<RetainedSubject> samples = RetainedSamples(store, driver.DecisionPoint);
                            AssertTrue(samples.Count >= 1, label + " retained its state");
                            foreach (RetainedSubject sample in samples)
                            {
                                AssertEqual(driver.ExpectedObjectiveId, sample.ObjectiveId, label + " sample objective_id");
                                AssertEqual(expectedMission, sample.MissionId, label + " sample mission_id");
                            }
                        }
                        finally
                        {
                            SafeDelete(dataDirectory);
                        }
                    }
                }).ConfigureAwait(false);
            }

            await RunTest("TheSubjectIds_NeverChangeTheTransmittedState_OrItsHash", async () =>
            {
                // An objective-scoped seam: the same objective content under two ids sends the same state.
                Func<string, Func<ITypedDecisionClient, TypedDecisionRecorder, TypedDecisionSettings, Task>> preflight = id => (client, recorder, settings) =>
                {
                    Objective objective = TypedDecisionSeams.LinkObjective(VesselId);
                    objective.Id = id;
                    return new PreflightTextAdapter(settings, client, recorder, new FakeOwnerDecisionNotePoster(), TypedDecisionSeams.Quiet())
                        .EvaluateAsync(objective, TypedDecisionSeams.LinkVessel(VesselId), null, new ObjectiveDispatchPreview { VesselId = VesselId }, CancellationToken.None);
                };
                CallRecord first = await CallOnceAsync("preflight", preflight("obj_subject_one")).ConfigureAwait(false);
                CallRecord second = await CallOnceAsync("preflight", preflight("obj_subject_two")).ConfigureAwait(false);
                AssertTrue(first.Events >= 1 && second.Events >= 1, "both preflight calls were recorded");
                AssertEqual("obj_subject_one", first.ObjectiveId, "the first call names its objective");
                AssertEqual("obj_subject_two", second.ObjectiveId, "the second call names its own");
                AssertTrue(first.StateText.Length > 0, "the provider received a state");
                AssertEqual(first.StateText, second.StateText, "preflight sends the same state whatever the objective id");
                AssertEqual(first.StateSha256, second.StateSha256, "so its recorded state hash is the same");

                // An adapter-skeleton seam, with and without the objective id.
                CallRecord withObjective = await CallOnceAsync("stage_necessity", (client, recorder, settings) =>
                {
                    StageNecessityDecisionInput input = TypedDecisionSeams.StageInput(ObjectiveId, VesselId);
                    return new TypedStageNecessityAdapter(client, recorder, settings, TypedDecisionSeams.Quiet()).DecideAsync(input, StageNecessityVerdict.Rule(input.Stages), CancellationToken.None);
                }).ConfigureAwait(false);
                CallRecord withoutObjective = await CallOnceAsync("stage_necessity", (client, recorder, settings) =>
                {
                    StageNecessityDecisionInput input = TypedDecisionSeams.StageInput(null, VesselId);
                    return new TypedStageNecessityAdapter(client, recorder, settings, TypedDecisionSeams.Quiet()).DecideAsync(input, StageNecessityVerdict.Rule(input.Stages), CancellationToken.None);
                }).ConfigureAwait(false);
                AssertTrue(withObjective.Events >= 1 && withoutObjective.Events >= 1, "both stage_necessity calls were recorded");
                AssertEqual(ObjectiveId, withObjective.ObjectiveId, "the id is recorded when known");
                AssertNull(withoutObjective.ObjectiveId, "and absent when not");
                AssertEqual(withObjective.StateText, withoutObjective.StateText, "stage_necessity sends the same state with and without the objective id");
                AssertEqual(withObjective.StateSha256, withoutObjective.StateSha256, "so the state hash is identical");
                AssertFalse(withObjective.StateText.Contains(ObjectiveId, StringComparison.Ordinal), "the objective id never enters the state");

                // A seam that holds only the mission id, with and without it.
                CallRecord withMission = await CallOnceAsync("log_watch", (client, recorder, settings) =>
                    new TypedLogWatchAdapter(client, recorder, settings, TypedDecisionSeams.Quiet()).DecideAsync(TypedDecisionSeams.LogWatchInput(MissionId), LogWatchVerdict.OnTrack(), CancellationToken.None)).ConfigureAwait(false);
                CallRecord withoutMission = await CallOnceAsync("log_watch", (client, recorder, settings) =>
                    new TypedLogWatchAdapter(client, recorder, settings, TypedDecisionSeams.Quiet()).DecideAsync(TypedDecisionSeams.LogWatchInput(String.Empty), LogWatchVerdict.OnTrack(), CancellationToken.None)).ConfigureAwait(false);
                AssertTrue(withMission.Events >= 1 && withoutMission.Events >= 1, "both log_watch calls were recorded");
                AssertEqual(MissionId, withMission.MissionId, "the mission id is recorded when known");
                AssertNull(withoutMission.MissionId, "and absent when not");
                AssertEqual(withMission.StateText, withoutMission.StateText, "log_watch sends the same state with and without the mission id");
                AssertEqual(withMission.StateSha256, withoutMission.StateSha256, "so the state hash is identical");
                AssertFalse(withMission.StateText.Contains(MissionId, StringComparison.Ordinal), "the mission id never enters the state");
            }).ConfigureAwait(false);
        }
    }
}
