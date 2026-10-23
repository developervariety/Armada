namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="IntegrationBarrier"/> (H10): when a voyage's final integration slice may start, what
    /// stops it, which slices it leaves out, and which commits it starts from.
    /// </summary>
    public sealed class IntegrationBarrierSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the integration barrier suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("settled_slices_release_integration_with_their_commits", "Settled slices release integration, which starts from every slice's distinct commit, oldest first", TestTags.Positive, async () =>
            {
                Plan plan = new Plan();
                Mission aWorker = plan.Slice("A", MissionStatusEnum.WorkProduced, "aaa111", 1);
                plan.Stage(aWorker, "Judge", MissionStatusEnum.WorkProduced, "aaa111", 2);
                Mission bWorker = plan.Slice("B", MissionStatusEnum.WorkProduced, "bbb222", 3);
                plan.Stage(bWorker, "Judge", MissionStatusEnum.Complete, "bbb333", 4);
                Mission integration = plan.Integration();

                IntegrationBarrierState state = await EvaluateAsync(plan, integration).ConfigureAwait(false);
                AssertTrue(state.Applies);
                AssertFalse(state.Waiting, state.WaitingSummary);
                AssertNull(state.FailedMission);
                AssertEqual("aaa111,bbb222,bbb333", String.Join(",", state.SettledCommits.Select(m => m.CommitHash)));
            }));

            cases.Add(CaseAsync("running_sibling_waits", "A sibling stage still running holds integration and names it", TestTags.Positive, async () =>
            {
                Plan plan = new Plan();
                Mission aWorker = plan.Slice("A", MissionStatusEnum.WorkProduced, "aaa111", 1);
                plan.Stage(aWorker, "Judge", MissionStatusEnum.InProgress, null, 2);
                Mission integration = plan.Integration();

                IntegrationBarrierState state = await EvaluateAsync(plan, integration).ConfigureAwait(false);
                AssertTrue(state.Waiting);
                AssertContains("InProgress", state.WaitingSummary ?? "");
            }));

            cases.Add(CaseAsync("landing_in_flight_waits_but_manual_landing_does_not", "A WorkProduced sibling waits while its merge-queue entry or completion is unsettled, and settles without one", TestTags.Positive, async () =>
            {
                Plan plan = new Plan();
                Mission aWorker = plan.Slice("A", MissionStatusEnum.WorkProduced, "aaa111", 1);
                Mission aJudge = plan.Stage(aWorker, "Judge", MissionStatusEnum.WorkProduced, "aaa111", 2);
                Mission integration = plan.Integration();

                IntegrationBarrierState queued = await IntegrationBarrier.EvaluateAsync(integration, plan.Missions, null,
                    () => Task.FromResult(new HashSet<string> { aJudge.Id })).ConfigureAwait(false);
                AssertTrue(queued.Waiting, "an unsettled merge-queue entry means the work is still landing");
                AssertContains("merge queue", queued.WaitingSummary ?? "");

                IntegrationBarrierState deciding = await IntegrationBarrier.EvaluateAsync(integration, plan.Missions, id => id == aJudge.Id,
                    () => Task.FromResult(new HashSet<string>())).ConfigureAwait(false);
                AssertTrue(deciding.Waiting, "a completion still in flight is not settled");

                IntegrationBarrierState manual = await EvaluateAsync(plan, integration).ConfigureAwait(false);
                AssertFalse(manual.Waiting, "with no queue entry (manual landing or landing mode None) WorkProduced is settled");
            }));

            cases.Add(CaseAsync("failed_sibling_stops_integration_even_while_others_run", "A failed or landing-failed sibling stops integration, even while other slices still run", TestTags.Negative, async () =>
            {
                foreach (MissionStatusEnum failure in new[] { MissionStatusEnum.Failed, MissionStatusEnum.LandingFailed })
                {
                    Plan plan = new Plan();
                    Mission aWorker = plan.Slice("A", MissionStatusEnum.WorkProduced, "aaa111", 1);
                    Mission aJudge = plan.Stage(aWorker, "Judge", failure, null, 2);
                    plan.Slice("B", MissionStatusEnum.InProgress, null, 3);
                    Mission integration = plan.Integration();

                    IntegrationBarrierState state = await EvaluateAsync(plan, integration).ConfigureAwait(false);
                    AssertNotNull(state.FailedMission, failure.ToString());
                    AssertEqual(aJudge.Id, state.FailedMission!.Id);
                }
            }));

            cases.Add(CaseAsync("operator_cancelled_slice_is_dropped", "A slice an operator cancelled is dropped and its commits are not merged; a dependency-cancelled stage is not a drop", TestTags.Positive, async () =>
            {
                Plan plan = new Plan();
                Mission aWorker = plan.Slice("A", MissionStatusEnum.WorkProduced, "aaa111", 1);
                Mission aJudge = plan.Stage(aWorker, "Judge", MissionStatusEnum.Cancelled, null, 2);
                aJudge.FailureKind = MissionFailureKindEnum.OperatorAction;
                Mission bWorker = plan.Slice("B", MissionStatusEnum.WorkProduced, "bbb222", 3);
                Mission bRecorder = plan.Stage(bWorker, "Recorder", MissionStatusEnum.Cancelled, null, 4);
                bRecorder.FailureKind = MissionFailureKindEnum.DependencyFailed;
                Mission integration = plan.Integration();

                IntegrationBarrierState state = await EvaluateAsync(plan, integration).ConfigureAwait(false);
                AssertFalse(state.Waiting);
                AssertNull(state.FailedMission);
                AssertEqual(1, state.DroppedSlices.Count);
                AssertEqual(aWorker.Id, state.DroppedSlices[0].Id, "the dropped slice is reported by its first mission");
                AssertEqual("bbb222", String.Join(",", state.SettledCommits.Select(m => m.CommitHash)), "only the kept slice's work is merged");
            }));

            cases.Add(CaseAsync("rework_of_integration_ignores_its_own_slice", "A rework of the integration slice inherits the barrier and never treats its own earlier stages as siblings", TestTags.Positive, async () =>
            {
                Plan plan = new Plan();
                plan.Slice("A", MissionStatusEnum.WorkProduced, "aaa111", 1);
                Mission integration = plan.Integration();
                integration.Status = MissionStatusEnum.WorkProduced;
                integration.CommitHash = "iii000";
                Mission rejected = plan.Stage(integration, "Judge", MissionStatusEnum.WorkProduced, "iii000", 5);
                Mission rework = plan.Stage(rejected, "Worker", MissionStatusEnum.Pending, null, 6);
                rework.Title = IntegrationBarrier.ReworkTitlePrefix + "1: Integrate";

                AssertTrue(IntegrationBarrier.Applies(rework, plan.Missions), "the rework inherits the barrier from its root");
                IntegrationBarrierState state = await EvaluateAsync(plan, rework).ConfigureAwait(false);
                AssertFalse(state.Waiting);
                AssertEqual("aaa111", String.Join(",", state.SettledCommits.Select(m => m.CommitHash)), "the integration's own commit is not a sibling's");
            }));

            cases.Add(CaseAsync("rework_identity_survives_a_retitle", "A rework recognized by its stored round keeps its identity and barrier after an operator renames it; an old rework is still recognized by its title", TestTags.Positive, () =>
            {
                Plan plan = new Plan();
                Mission integration = plan.Integration();
                Mission judge = plan.Stage(integration, "Judge", MissionStatusEnum.WorkProduced, null, 11);
                Mission rework = plan.Stage(judge, "Worker", MissionStatusEnum.Pending, null, 12);
                rework.Title = "Renamed by an operator";
                rework.ReworkRound = 1;
                rework.ReworkEscalated = true;
                AssertTrue(IntegrationBarrier.IsRework(rework));
                AssertTrue(IntegrationBarrier.IsEscalatedRework(rework));
                AssertTrue(IntegrationBarrier.Applies(rework, plan.Missions), "the renamed rework still inherits the barrier");
                Mission legacy = new Mission(IntegrationBarrier.ReworkTitlePrefix + "2" + IntegrationBarrier.EscalatedMarker + ": X", "d");
                AssertTrue(IntegrationBarrier.IsRework(legacy), "a rework stored before the round was persisted is read from its title");
                AssertTrue(IntegrationBarrier.IsEscalatedRework(legacy));
                AssertFalse(IntegrationBarrier.IsRework(new Mission("Plain [Worker]", "d")));
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("ordinary_worker_has_no_barrier", "A Worker that is not the final integration slice has no barrier", TestTags.Negative, async () =>
            {
                Plan plan = new Plan();
                Mission aWorker = plan.Slice("A", MissionStatusEnum.Pending, null, 1);
                plan.Slice("B", MissionStatusEnum.Failed, null, 2);

                AssertFalse(IntegrationBarrier.Applies(aWorker, plan.Missions));
                IntegrationBarrierState state = await EvaluateAsync(plan, aWorker).ConfigureAwait(false);
                AssertFalse(state.Applies);
                AssertNull(state.FailedMission);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.IntegrationBarrier",
                displayName: "Integration Barrier",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static Task<IntegrationBarrierState> EvaluateAsync(Plan plan, Mission mission)
        {
            return IntegrationBarrier.EvaluateAsync(mission, plan.Missions, null, () => Task.FromResult(new HashSet<string>()));
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.IntegrationBarrier",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion

        #region Private-Classes

        private sealed class Plan
        {
            public List<Mission> Missions { get; } = new List<Mission>();

            public Mission Architect { get; }

            private readonly DateTime _Start = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

            public Plan()
            {
                Architect = Add(new Mission("Plan", "Plan") { Persona = "Architect", Status = MissionStatusEnum.WorkProduced }, 0);
            }

            public Mission Slice(string name, MissionStatusEnum status, string? commit, int minute)
            {
                return Add(new Mission(name + " [Worker]", "Implement " + name)
                {
                    Persona = "Worker", Status = status, CommitHash = commit, DependsOnMissionId = Architect.Id, BranchName = "armada/" + name
                }, minute);
            }

            public Mission Stage(Mission after, string persona, MissionStatusEnum status, string? commit, int minute)
            {
                return Add(new Mission(after.Title + " [" + persona + "]", "Stage")
                {
                    Persona = persona, Status = status, CommitHash = commit, DependsOnMissionId = after.Id, BranchName = after.BranchName
                }, minute);
            }

            public Mission Integration()
            {
                return Add(new Mission("Integrate [Worker]", "Integrate")
                {
                    Persona = "Worker", Status = MissionStatusEnum.Pending, DependsOnMissionId = Architect.Id, WaitForVoyageWorkers = true
                }, 10);
            }

            private Mission Add(Mission mission, int minute)
            {
                mission.VoyageId = "vyg_barrier";
                mission.CreatedUtc = _Start.AddMinutes(minute);
                mission.CompletedUtc = _Start.AddMinutes(minute);
                Missions.Add(mission);
                return mission;
            }
        }

        #endregion
    }
}
