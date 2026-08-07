namespace Armada.Test.Shared.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="Dock"/>: id generation and validation, vessel-id validation,
    /// default values, and JSON serialization.
    /// </summary>
    public sealed class DockModelSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Dock model suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("default_constructor_generates_id_with_prefix", "Dock default constructor generates id with prefix", TestTags.Positive, () =>
            {
                Dock dock = new Dock();
                AssertStartsWith(Constants.DockIdPrefix, dock.Id);
            }));

            cases.Add(Case("vessel_id_constructor_sets_vessel_id", "Dock vessel-id constructor sets vessel id", TestTags.Positive, () =>
            {
                Dock dock = new Dock("vsl_test123");
                AssertEqual("vsl_test123", dock.VesselId);
            }));

            cases.Add(Case("default_values_are_correct", "Dock default values are correct", TestTags.Positive, () =>
            {
                Dock dock = new Dock();
                AssertTrue(dock.Active);
                AssertNull(dock.CaptainId);
                AssertNull(dock.WorktreePath);
                AssertNull(dock.BranchName);
            }));

            cases.Add(Case("set_id_null_throws", "Dock set id null throws", TestTags.Negative, () =>
            {
                Dock dock = new Dock();
                AssertThrows<ArgumentNullException>(() => dock.Id = null!);
            }));

            cases.Add(Case("set_id_empty_throws", "Dock set id empty throws", TestTags.Negative, () =>
            {
                Dock dock = new Dock();
                AssertThrows<ArgumentNullException>(() => dock.Id = "");
            }));

            cases.Add(Case("set_vessel_id_null_throws", "Dock set vessel id null throws", TestTags.Negative, () =>
            {
                Dock dock = new Dock();
                AssertThrows<ArgumentNullException>(() => dock.VesselId = null!);
            }));

            cases.Add(Case("set_vessel_id_empty_throws", "Dock set vessel id empty throws", TestTags.Negative, () =>
            {
                Dock dock = new Dock();
                AssertThrows<ArgumentNullException>(() => dock.VesselId = "");
            }));

            cases.Add(Case("serialization_round_trip", "Dock serialization round trip", TestTags.Positive, () =>
            {
                Dock dock = new Dock("vsl_test");
                dock.CaptainId = "cpt_test";
                dock.WorktreePath = "/tmp/worktree";
                dock.BranchName = "armada/test/msn_123";
                dock.Active = false;

                string json = JsonSerializer.Serialize(dock);
                Dock deserialized = JsonSerializer.Deserialize<Dock>(json)!;

                AssertEqual(dock.Id, deserialized.Id);
                AssertEqual(dock.VesselId, deserialized.VesselId);
                AssertEqual(dock.CaptainId, deserialized.CaptainId);
                AssertEqual(dock.WorktreePath, deserialized.WorktreePath);
                AssertEqual(dock.Active, deserialized.Active);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.DockModel",
                displayName: "Dock Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.DockModel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.DockModel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
