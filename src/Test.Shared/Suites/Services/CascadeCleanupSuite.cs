namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="CascadeCleanup"/>. The events and planning_sessions tables carry plain
    /// (non-foreign-key) references to their parent entities, so a hard delete would otherwise leave rows
    /// that dangle and later fail to resolve. These cases prove that removing the dependents of a vessel,
    /// mission, voyage, or captain deletes exactly the rows that referenced that parent and leaves rows
    /// belonging to other parents untouched.
    /// </summary>
    public sealed class CascadeCleanupSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.CascadeCleanup";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Cascade Cleanup suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("removes_events_for_vessel_only", "RemoveEventsForVesselAsync deletes only the target vessel's events", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await CreateEventAsync(db, "vessel.updated", vesselId: "vsl_target").ConfigureAwait(false);
                    await CreateEventAsync(db, "vessel.updated", vesselId: "vsl_target").ConfigureAwait(false);
                    await CreateEventAsync(db, "vessel.updated", vesselId: "vsl_other").ConfigureAwait(false);

                    int removed = (await CascadeCleanup.RemoveEventsForVesselAsync(db, "vsl_target").ConfigureAwait(false)).Removed;

                    AssertEqual(2, removed);
                    AssertEqual(0, (await db.Events.EnumerateByVesselAsync("vsl_target", 500).ConfigureAwait(false)).Count);
                    AssertEqual(1, (await db.Events.EnumerateByVesselAsync("vsl_other", 500).ConfigureAwait(false)).Count);
                }
            }));

            cases.Add(CaseAsync("removes_events_for_mission_only", "RemoveEventsForMissionAsync deletes only the target mission's events", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await CreateEventAsync(db, "mission.progress", missionId: "msn_target").ConfigureAwait(false);
                    await CreateEventAsync(db, "mission.progress", missionId: "msn_other").ConfigureAwait(false);

                    int removed = (await CascadeCleanup.RemoveEventsForMissionAsync(db, "msn_target").ConfigureAwait(false)).Removed;

                    AssertEqual(1, removed);
                    AssertEqual(0, (await db.Events.EnumerateByMissionAsync("msn_target", 500).ConfigureAwait(false)).Count);
                    AssertEqual(1, (await db.Events.EnumerateByMissionAsync("msn_other", 500).ConfigureAwait(false)).Count);
                }
            }));

            cases.Add(CaseAsync("removes_events_for_voyage_only", "RemoveEventsForVoyageAsync deletes only the target voyage's events", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await CreateEventAsync(db, "voyage.updated", voyageId: "vyg_target").ConfigureAwait(false);
                    await CreateEventAsync(db, "voyage.updated", voyageId: "vyg_other").ConfigureAwait(false);

                    int removed = (await CascadeCleanup.RemoveEventsForVoyageAsync(db, "vyg_target").ConfigureAwait(false)).Removed;

                    AssertEqual(1, removed);
                    AssertEqual(0, (await db.Events.EnumerateByVoyageAsync("vyg_target", 500).ConfigureAwait(false)).Count);
                    AssertEqual(1, (await db.Events.EnumerateByVoyageAsync("vyg_other", 500).ConfigureAwait(false)).Count);
                }
            }));

            cases.Add(CaseAsync("removes_captain_events_and_planning_sessions", "RemoveDependentsForCaptainAsync deletes the captain's events and planning sessions", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await CreateEventAsync(db, "captain.assigned", captainId: "cpt_target").ConfigureAwait(false);
                    await CreateEventAsync(db, "captain.assigned", captainId: "cpt_other").ConfigureAwait(false);
                    await CreatePlanningSessionAsync(db, "cpt_target").ConfigureAwait(false);
                    await CreatePlanningSessionAsync(db, "cpt_target").ConfigureAwait(false);
                    await CreatePlanningSessionAsync(db, "cpt_other").ConfigureAwait(false);

                    int removed = (await CascadeCleanup.RemoveDependentsForCaptainAsync(db, "cpt_target").ConfigureAwait(false)).Removed;

                    // 1 event + 2 planning sessions.
                    AssertEqual(3, removed);
                    AssertEqual(0, (await db.Events.EnumerateByCaptainAsync("cpt_target", 500).ConfigureAwait(false)).Count);
                    AssertEqual(0, (await db.PlanningSessions.EnumerateByCaptainAsync("cpt_target").ConfigureAwait(false)).Count);
                    AssertEqual(1, (await db.Events.EnumerateByCaptainAsync("cpt_other", 500).ConfigureAwait(false)).Count);
                    AssertEqual(1, (await db.PlanningSessions.EnumerateByCaptainAsync("cpt_other").ConfigureAwait(false)).Count);
                }
            }));

            cases.Add(CaseAsync("removes_captain_refinement_sessions", "RemoveDependentsForCaptainAsync deletes the captain's objective refinement sessions", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await CreateRefinementSessionAsync(db, "cpt_target").ConfigureAwait(false);
                    await CreateRefinementSessionAsync(db, "cpt_other").ConfigureAwait(false);

                    int removed = (await CascadeCleanup.RemoveDependentsForCaptainAsync(db, "cpt_target").ConfigureAwait(false)).Removed;

                    AssertEqual(1, removed);
                    AssertEqual(0, (await db.ObjectiveRefinementSessions.EnumerateByCaptainAsync("cpt_target").ConfigureAwait(false)).Count);
                    AssertEqual(1, (await db.ObjectiveRefinementSessions.EnumerateByCaptainAsync("cpt_other").ConfigureAwait(false)).Count);
                }
            }));

            cases.Add(CaseAsync("captain_cleanup_logs_a_provider_without_planning_sessions", "RemoveDependentsForCaptainAsync logs the skipped planning-session cleanup on a provider that stores none", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await CreateEventAsync(db, "captain.assigned", captainId: "cpt_target").ConfigureAwait(false);

                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    List<string> messages = new List<string>();
                    object gate = new object();
                    logging.MessageLogged += entry =>
                    {
                        lock (gate) messages.Add(entry.Message ?? String.Empty);
                    };

                    PropertyInfo planningSessions = typeof(DatabaseDriver).GetProperty(nameof(DatabaseDriver.PlanningSessions))!;
                    object? original = planningSessions.GetValue(db);
                    planningSessions.SetValue(db, new Armada.Core.Database.Postgresql.Implementations.PlanningSessionMethods(null!, null!, null!));
                    int removed;
                    try
                    {
                        removed = (await CascadeCleanup.RemoveDependentsForCaptainAsync(db, "cpt_target", logging: logging).ConfigureAwait(false)).Removed;
                    }
                    finally
                    {
                        planningSessions.SetValue(db, original);
                    }

                    AssertEqual(1, removed, "the captain's event is still removed");
                    List<string> logged;
                    lock (gate) logged = new List<string>(messages);
                    AssertTrue(
                        logged.Exists(message => message.Contains("planning-session cleanup skipped for deleted captain cpt_target", StringComparison.Ordinal)),
                        "the skipped planning-session cleanup is logged with the captain: " + String.Join(" | ", logged));
                }
            }));

            cases.Add(CaseAsync("reports_each_dependent_it_could_not_remove", "RemoveDependentsForCaptainAsync counts and names each dependent it skips and still removes the rest", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await CreateEventAsync(db, "captain.assigned", captainId: "cpt_target").ConfigureAwait(false);
                    await CreatePlanningSessionAsync(db, "cpt_target").ConfigureAwait(false);
                    await CreateRefinementSessionAsync(db, "cpt_target").ConfigureAwait(false);
                    List<PlanningSession> planning = await db.PlanningSessions.EnumerateByCaptainAsync("cpt_target").ConfigureAwait(false);

                    PropertyInfo planningProperty = typeof(DatabaseDriver).GetProperty(nameof(DatabaseDriver.PlanningSessions))!;
                    IPlanningSessionMethods original = db.PlanningSessions;
                    planningProperty.SetValue(db, FailingDeleteProxy<IPlanningSessionMethods>.Wrap(original, "planning store is read-only"));
                    CascadeCleanupResult result;
                    try
                    {
                        result = await CascadeCleanup.RemoveDependentsForCaptainAsync(db, "cpt_target").ConfigureAwait(false);
                    }
                    finally
                    {
                        planningProperty.SetValue(db, original);
                    }

                    AssertEqual(2, result.Removed, "The event and the refinement session are still removed");
                    AssertEqual(1, result.Skipped, "The planning session that could not be deleted is counted");
                    AssertEqual("PlanningSession", result.Skips[0].Kind, "The skip names its kind");
                    AssertEqual(planning[0].Id, result.Skips[0].Id, "The skip names the row");
                    AssertTrue(result.Skips[0].Reason.Contains("planning store is read-only", StringComparison.Ordinal), "The skip carries the reason: " + result.Skips[0].Reason);
                    AssertEqual(1, (await db.PlanningSessions.EnumerateByCaptainAsync("cpt_target").ConfigureAwait(false)).Count, "The skipped row is left in place");
                    AssertEqual(0, (await db.Events.EnumerateByCaptainAsync("cpt_target", 500).ConfigureAwait(false)).Count);
                    AssertEqual(0, (await db.ObjectiveRefinementSessions.EnumerateByCaptainAsync("cpt_target").ConfigureAwait(false)).Count);
                }
            }));

            cases.Add(CaseAsync("reports_and_logs_each_event_it_could_not_remove", "RemoveEventsForVesselAsync counts, names and logs each event it could not delete", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await CreateEventAsync(db, "vessel.updated", vesselId: "vsl_target").ConfigureAwait(false);
                    List<ArmadaEvent> events = await db.Events.EnumerateByVesselAsync("vsl_target", 500).ConfigureAwait(false);

                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    List<string> messages = new List<string>();
                    object gate = new object();
                    logging.MessageLogged += entry =>
                    {
                        lock (gate) messages.Add(entry.Message ?? String.Empty);
                    };

                    PropertyInfo eventsProperty = typeof(DatabaseDriver).GetProperty(nameof(DatabaseDriver.Events))!;
                    IEventMethods original = db.Events;
                    eventsProperty.SetValue(db, FailingDeleteProxy<IEventMethods>.Wrap(original, "event store is read-only"));
                    CascadeCleanupResult result;
                    try
                    {
                        result = await CascadeCleanup.RemoveEventsForVesselAsync(db, "vsl_target", logging: logging).ConfigureAwait(false);
                    }
                    finally
                    {
                        eventsProperty.SetValue(db, original);
                    }

                    AssertEqual(0, result.Removed, "Nothing is removed");
                    AssertEqual(1, result.Skipped, "The event that could not be deleted is counted once");
                    AssertEqual("Event", result.Skips[0].Kind, "The skip names its kind");
                    AssertEqual(events[0].Id, result.Skips[0].Id, "The skip names the event");
                    List<string> logged;
                    lock (gate) logged = new List<string>(messages);
                    AssertTrue(
                        logged.Exists(message => message.Contains(events[0].Id, StringComparison.Ordinal) && message.Contains("event store is read-only", StringComparison.Ordinal)),
                        "The skipped event is logged with its reason: " + String.Join(" | ", logged));
                }
            }));

            cases.Add(CaseAsync("empty_parent_id_is_a_safe_no_op", "Cleanup with an empty parent id removes nothing", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    await CreateEventAsync(db, "vessel.updated", vesselId: "vsl_keep").ConfigureAwait(false);

                    int removed = (await CascadeCleanup.RemoveEventsForVesselAsync(db, String.Empty).ConfigureAwait(false)).Removed;

                    AssertEqual(0, removed);
                    AssertEqual(1, (await db.Events.EnumerateByVesselAsync("vsl_keep", 500).ConfigureAwait(false)).Count);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Cascade Cleanup",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task CreateEventAsync(
            DatabaseDriver db,
            string eventType,
            string? captainId = null,
            string? missionId = null,
            string? vesselId = null,
            string? voyageId = null)
        {
            ArmadaEvent armadaEvent = new ArmadaEvent
            {
                EventType = eventType,
                Message = eventType,
                CaptainId = captainId,
                MissionId = missionId,
                VesselId = vesselId,
                VoyageId = voyageId
            };

            await db.Events.CreateAsync(armadaEvent).ConfigureAwait(false);
        }

        private static async Task CreatePlanningSessionAsync(DatabaseDriver db, string captainId)
        {
            PlanningSession session = new PlanningSession
            {
                CaptainId = captainId,
                VesselId = "vsl_planning",
                Title = "Planning for " + captainId
            };

            await db.PlanningSessions.CreateAsync(session).ConfigureAwait(false);
        }

        private static async Task CreateRefinementSessionAsync(DatabaseDriver db, string captainId)
        {
            Objective objective = await db.Objectives.CreateAsync(new Objective { Title = "Objective for " + captainId }).ConfigureAwait(false);
            ObjectiveRefinementSession session = new ObjectiveRefinementSession
            {
                ObjectiveId = objective.Id,
                CaptainId = captainId,
                Title = "Refinement for " + captainId
            };

            await db.ObjectiveRefinementSessions.CreateAsync(session).ConfigureAwait(false);
        }

        /// <summary>
        /// Delegates every call to the wrapped store except <c>DeleteAsync</c>, which fails with a fixed reason.
        /// </summary>
        /// <typeparam name="T">Store interface.</typeparam>
        public class FailingDeleteProxy<T> : DispatchProxy where T : class
        {
            private T _Target = null!;
            private string _Reason = "";

            /// <summary>
            /// Wrap a store.
            /// </summary>
            /// <param name="target">Store to delegate to.</param>
            /// <param name="reason">Message of the exception every delete throws.</param>
            /// <returns>The wrapped store.</returns>
            public static T Wrap(T target, string reason)
            {
                T proxy = DispatchProxy.Create<T, FailingDeleteProxy<T>>();
                FailingDeleteProxy<T> self = (FailingDeleteProxy<T>)(object)proxy;
                self._Target = target;
                self._Reason = reason;
                return proxy;
            }

            /// <inheritdoc />
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));
                if (targetMethod.Name == "DeleteAsync") throw new InvalidOperationException(_Reason);
                return targetMethod.Invoke(_Target, args);
            }
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
