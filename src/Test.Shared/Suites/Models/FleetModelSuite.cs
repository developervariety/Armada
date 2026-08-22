namespace Test.Shared.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="Fleet"/>: id generation and validation, name validation,
    /// default values, JSON serialization, and id uniqueness.
    /// </summary>
    public sealed class FleetModelSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Fleet model suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("default_constructor_generates_id_with_prefix", "Fleet default constructor generates id with prefix", TestTags.Positive, () =>
            {
                Fleet fleet = new Fleet();
                AssertNotNull(fleet.Id);
                AssertStartsWith(Constants.FleetIdPrefix, fleet.Id);
            }));

            cases.Add(Case("name_constructor_sets_name", "Fleet name constructor sets name", TestTags.Positive, () =>
            {
                Fleet fleet = new Fleet("TestFleet");
                AssertEqual("TestFleet", fleet.Name);
            }));

            cases.Add(Case("default_values_are_correct", "Fleet default values are correct", TestTags.Positive, () =>
            {
                Fleet fleet = new Fleet();
                AssertEqual("My Fleet", fleet.Name);
                AssertNull(fleet.Description);
                AssertTrue(fleet.Active);
                AssertTrue(fleet.CreatedUtc <= DateTime.UtcNow);
                AssertTrue(fleet.LastUpdateUtc <= DateTime.UtcNow);
            }));

            cases.Add(Case("set_id_null_throws", "Fleet set id null throws", TestTags.Negative, () =>
            {
                Fleet fleet = new Fleet();
                AssertThrows<ArgumentNullException>(() => fleet.Id = null!);
            }));

            cases.Add(Case("set_id_empty_throws", "Fleet set id empty throws", TestTags.Negative, () =>
            {
                Fleet fleet = new Fleet();
                AssertThrows<ArgumentNullException>(() => fleet.Id = "");
            }));

            cases.Add(Case("set_name_null_throws", "Fleet set name null throws", TestTags.Negative, () =>
            {
                Fleet fleet = new Fleet();
                AssertThrows<ArgumentNullException>(() => fleet.Name = null!);
            }));

            cases.Add(Case("set_name_empty_throws", "Fleet set name empty throws", TestTags.Negative, () =>
            {
                Fleet fleet = new Fleet();
                AssertThrows<ArgumentNullException>(() => fleet.Name = "");
            }));

            cases.Add(Case("serialization_round_trip", "Fleet serialization round trip", TestTags.Positive, () =>
            {
                Fleet fleet = new Fleet("SerializationTest");
                fleet.Description = "Test description";
                fleet.Active = false;

                string json = JsonSerializer.Serialize(fleet);
                Fleet deserialized = JsonSerializer.Deserialize<Fleet>(json)!;

                AssertEqual(fleet.Id, deserialized.Id);
                AssertEqual(fleet.Name, deserialized.Name);
                AssertEqual(fleet.Description, deserialized.Description);
                AssertEqual(fleet.Active, deserialized.Active);
            }));

            cases.Add(Case("unique_ids_across_instances", "Fleet unique ids across instances", TestTags.Positive, () =>
            {
                Fleet fleet1 = new Fleet();
                Fleet fleet2 = new Fleet();
                AssertNotEqual(fleet1.Id, fleet2.Id);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.FleetModel",
                displayName: "Fleet Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.FleetModel",
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
                suiteId: "Models.FleetModel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
