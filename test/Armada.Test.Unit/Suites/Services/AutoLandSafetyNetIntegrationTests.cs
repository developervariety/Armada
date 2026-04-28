namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Integration tests for the auto-land safety-net block in MissionLandingHandler.
    /// Exercises the synchronous Layer 1 evaluation: convention check, critical-trigger
    /// evaluation, calibration-period check, and audit column persistence.
    /// Uses hand-rolled doubles and TestDatabaseHelper for DB round-trips.
    /// </summary>
    public class AutoLandSafetyNetIntegrationTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Auto-Land Safety Net Integration";

        #region Doubles

        private sealed class BenignConventionChecker : IConventionChecker
        {
            public ConventionCheckResult Check(string unifiedDiff)
            {
                return new ConventionCheckResult { Passed = true };
            }
        }

        private sealed class FailingConventionChecker : IConventionChecker
        {
            public ConventionCheckResult Check(string unifiedDiff)
            {
                ConventionCheckResult r = new ConventionCheckResult { Passed = false };
                r.Violations.Add(new ConventionViolation("CORE_RULE_2_mocking_lib", "+using Moq;"));
                return r;
            }
        }

        private sealed class BenignCriticalTriggerEvaluator : ICriticalTriggerEvaluator
        {
            public CriticalTriggerResult Evaluate(string unifiedDiff, ConventionCheckResult conventionResult)
            {
                return new CriticalTriggerResult { Fired = false };
            }
        }

        private sealed class ConventionCriticalTriggerEvaluator : ICriticalTriggerEvaluator
        {
            public CriticalTriggerResult Evaluate(string unifiedDiff, ConventionCheckResult conventionResult)
            {
                CriticalTriggerResult r = new CriticalTriggerResult { Fired = true };
                r.TriggeredCriteria.Add("convention");
                return r;
            }
        }

        private sealed class PathCriticalTriggerEvaluator : ICriticalTriggerEvaluator
        {
            public CriticalTriggerResult Evaluate(string unifiedDiff, ConventionCheckResult conventionResult)
            {
                CriticalTriggerResult r = new CriticalTriggerResult { Fired = true };
                r.TriggeredCriteria.Add("path");
                return r;
            }
        }

        #endregion

        #region Helper

        /// <summary>
        /// Mirrors the safety-net block from MissionLandingHandler.HandleMissionCompleteAsync
        /// for direct unit-level verification of the logic and DB persistence.
        /// </summary>
        private static async Task<MergeEntry?> RunSafetyNetAsync(
            IConventionChecker conventionChecker,
            ICriticalTriggerEvaluator criticalTriggerEvaluator,
            string diff,
            Vessel vessel,
            MergeEntry entry,
            TestDatabase testDb)
        {
            ConventionCheckResult conventionResult = conventionChecker.Check(diff);
            CriticalTriggerResult triggerResult = criticalTriggerEvaluator.Evaluate(diff, conventionResult);

            bool calibrationActive = vessel.AutoLandCalibrationLandedCount < 50;
            bool needsDeepReview = calibrationActive || triggerResult.Fired;

            entry.AuditLane = needsDeepReview ? "Deferred" : "Fast";
            entry.AuditConventionPassed = conventionResult.Passed;
            entry.AuditConventionNotes = conventionResult.Passed
                ? null
                : JsonSerializer.Serialize(conventionResult.Violations);
            entry.AuditCriticalTrigger = string.Join(",", triggerResult.TriggeredCriteria);
            entry.AuditDeepPicked = needsDeepReview;
            entry.AuditDeepVerdict = needsDeepReview ? "Pending" : null;

            await testDb.Driver.MergeEntries.UpdateAsync(entry).ConfigureAwait(false);
            return await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
        }

        #endregion

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("BenignAutoLand_DuringCalibration_QueuesForDeepReview", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("safety-net-1", "https://github.com/test/repo.git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    AssertEqual(0, vessel.AutoLandCalibrationLandedCount, "New vessel starts at 0");

                    MergeEntry entry = new MergeEntry("branch-sn-1", "main");
                    entry.VesselId = vessel.Id;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    string diff = "+++ b/src/Foo.cs\n+public class Foo {}\n";
                    MergeEntry? updated = await RunSafetyNetAsync(
                        new BenignConventionChecker(),
                        new BenignCriticalTriggerEvaluator(),
                        diff, vessel, entry, testDb).ConfigureAwait(false);

                    AssertNotNull(updated, "Updated entry should be readable from DB");
                    AssertEqual("Deferred", updated!.AuditLane, "Calibration active => Deferred lane");
                    AssertTrue(updated.AuditDeepPicked == true, "AuditDeepPicked should be true");
                    AssertEqual("Pending", updated.AuditDeepVerdict, "Verdict should be Pending");
                    AssertTrue(updated.AuditConventionPassed == true, "No convention violations");
                    AssertNull(updated.AuditConventionNotes, "No violations => null notes");
                }
            });

            await RunTest("BenignAutoLand_PostCalibration_AuditFastLane", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("safety-net-2", "https://github.com/test/repo.git");
                    vessel.AutoLandCalibrationLandedCount = 50;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("branch-sn-2", "main");
                    entry.VesselId = vessel.Id;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    string diff = "+++ b/src/Foo.cs\n+public class Foo {}\n";
                    MergeEntry? updated = await RunSafetyNetAsync(
                        new BenignConventionChecker(),
                        new BenignCriticalTriggerEvaluator(),
                        diff, vessel, entry, testDb).ConfigureAwait(false);

                    AssertNotNull(updated);
                    AssertEqual("Fast", updated!.AuditLane, "Post-calibration + benign => Fast lane");
                    AssertTrue(updated.AuditDeepPicked == false, "AuditDeepPicked should be false");
                    AssertNull(updated.AuditDeepVerdict, "Fast lane => no verdict pending");
                }
            });

            await RunTest("ConventionFailure_PostCalibration_QueuesForDeepReview", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("safety-net-3", "https://github.com/test/repo.git");
                    vessel.AutoLandCalibrationLandedCount = 50;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("branch-sn-3", "main");
                    entry.VesselId = vessel.Id;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    string diff = "+using Moq;\n+public class Foo {}\n";
                    MergeEntry? updated = await RunSafetyNetAsync(
                        new FailingConventionChecker(),
                        new ConventionCriticalTriggerEvaluator(),
                        diff, vessel, entry, testDb).ConfigureAwait(false);

                    AssertNotNull(updated);
                    AssertEqual("Deferred", updated!.AuditLane, "Convention failure => Deferred");
                    AssertTrue(updated.AuditConventionPassed == false, "Convention check failed");
                    AssertNotNull(updated.AuditConventionNotes, "Convention notes should be populated");
                    AssertContains("CORE_RULE_2", updated.AuditConventionNotes!, "Notes should contain the rule name");
                    AssertNotNull(updated.AuditCriticalTrigger);
                    AssertContains("convention", updated.AuditCriticalTrigger!, "Trigger should include convention");
                    AssertTrue(updated.AuditDeepPicked == true, "AuditDeepPicked should be true");
                }
            });

            await RunTest("CriticalTriggerPath_PostCalibration_QueuesForDeepReview", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("safety-net-4", "https://github.com/test/repo.git");
                    vessel.AutoLandCalibrationLandedCount = 50;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("branch-sn-4", "main");
                    entry.VesselId = vessel.Id;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    string diff = "+++ b/src/Features/Auth/AuthService.cs\n+public class AuthService {}\n";
                    MergeEntry? updated = await RunSafetyNetAsync(
                        new BenignConventionChecker(),
                        new PathCriticalTriggerEvaluator(),
                        diff, vessel, entry, testDb).ConfigureAwait(false);

                    AssertNotNull(updated);
                    AssertEqual("Deferred", updated!.AuditLane, "Path trigger => Deferred");
                    AssertNotNull(updated.AuditCriticalTrigger);
                    AssertContains("path", updated.AuditCriticalTrigger!, "Trigger should include path");
                    AssertTrue(updated.AuditDeepPicked == true, "AuditDeepPicked should be true");
                    AssertEqual("Pending", updated.AuditDeepVerdict);
                }
            });

            await RunTest("PredicateFailed_NoSafetyNetEvaluation", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("safety-net-5", "https://github.com/test/repo.git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("branch-sn-5", "main");
                    entry.VesselId = vessel.Id;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    // Predicate Fail => safety-net block never runs => audit columns stay null.
                    // Verify DB state without calling RunSafetyNetAsync.
                    MergeEntry? fromDb = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);

                    AssertNotNull(fromDb);
                    AssertNull(fromDb!.AuditLane, "Safety net did not run => AuditLane null");
                    AssertNull(fromDb.AuditConventionPassed, "Safety net did not run => AuditConventionPassed null");
                    AssertNull(fromDb.AuditConventionNotes, "Safety net did not run => AuditConventionNotes null");
                    AssertNull(fromDb.AuditCriticalTrigger, "Safety net did not run => AuditCriticalTrigger null");
                    AssertNull(fromDb.AuditDeepPicked, "Safety net did not run => AuditDeepPicked null");
                    AssertNull(fromDb.AuditDeepVerdict, "Safety net did not run => AuditDeepVerdict null");
                }
            });
        }
    }
}
