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
    /// Descriptors for <see cref="TenantMetadata"/>: identity generation, constructor defaults
    /// and timestamps, Id/Name validation, and JSON round-tripping. Ported from the retired unit
    /// suite, which already covered the null/empty rejection paths.
    /// </summary>
    public sealed class TenantMetadataSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the TenantMetadata model suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("default_constructor_generates_id_with_prefix", "TenantMetadata default constructor generates id with prefix", TestTags.Positive, () =>
            {
                TenantMetadata tenant = new TenantMetadata();
                AssertStartsWith(Constants.TenantIdPrefix, tenant.Id);
            }));

            cases.Add(Case("default_constructor_sets_default_name", "TenantMetadata default constructor sets default name", TestTags.Positive, () =>
            {
                TenantMetadata tenant = new TenantMetadata();
                AssertEqual("My Tenant", tenant.Name);
            }));

            cases.Add(Case("default_constructor_is_active", "TenantMetadata default constructor is active", TestTags.Positive, () =>
            {
                TenantMetadata tenant = new TenantMetadata();
                AssertTrue(tenant.Active);
            }));

            cases.Add(Case("default_constructor_sets_timestamps", "TenantMetadata default constructor sets timestamps", TestTags.Positive, () =>
            {
                DateTime before = DateTime.UtcNow.AddSeconds(-1);
                TenantMetadata tenant = new TenantMetadata();
                DateTime after = DateTime.UtcNow.AddSeconds(1);

                AssertTrue(tenant.CreatedUtc >= before && tenant.CreatedUtc <= after, "CreatedUtc should be recent");
                AssertTrue(tenant.LastUpdateUtc >= before && tenant.LastUpdateUtc <= after, "LastUpdateUtc should be recent");
            }));

            cases.Add(Case("name_constructor_sets_name", "TenantMetadata name constructor sets name", TestTags.Positive, () =>
            {
                TenantMetadata tenant = new TenantMetadata("Acme Corp");
                AssertEqual("Acme Corp", tenant.Name);
            }));

            cases.Add(Case("name_constructor_still_generates_id", "TenantMetadata name constructor still generates id", TestTags.Positive, () =>
            {
                TenantMetadata tenant = new TenantMetadata("Test Tenant");
                AssertStartsWith(Constants.TenantIdPrefix, tenant.Id);
            }));

            cases.Add(Case("set_id_null_throws", "TenantMetadata set id null throws", TestTags.Negative, () =>
            {
                TenantMetadata tenant = new TenantMetadata();
                AssertThrows<ArgumentNullException>(() => tenant.Id = null!);
            }));

            cases.Add(Case("set_id_empty_throws", "TenantMetadata set id empty throws", TestTags.Negative, () =>
            {
                TenantMetadata tenant = new TenantMetadata();
                AssertThrows<ArgumentNullException>(() => tenant.Id = "");
            }));

            cases.Add(Case("set_name_null_throws", "TenantMetadata set name null throws", TestTags.Negative, () =>
            {
                TenantMetadata tenant = new TenantMetadata();
                AssertThrows<ArgumentNullException>(() => tenant.Name = null!);
            }));

            cases.Add(Case("set_name_empty_throws", "TenantMetadata set name empty throws", TestTags.Negative, () =>
            {
                TenantMetadata tenant = new TenantMetadata();
                AssertThrows<ArgumentNullException>(() => tenant.Name = "");
            }));

            cases.Add(Case("name_constructor_null_throws", "TenantMetadata name constructor null throws", TestTags.Negative, () =>
            {
                AssertThrows<ArgumentNullException>(() => new TenantMetadata(null!));
            }));

            cases.Add(Case("set_id_valid_value_succeeds", "TenantMetadata set id valid value succeeds", TestTags.Positive, () =>
            {
                TenantMetadata tenant = new TenantMetadata();
                tenant.Id = "ten_custom";
                AssertEqual("ten_custom", tenant.Id);
            }));

            cases.Add(Case("set_active_to_false", "TenantMetadata set active to false", TestTags.Positive, () =>
            {
                TenantMetadata tenant = new TenantMetadata();
                tenant.Active = false;
                AssertFalse(tenant.Active);
            }));

            cases.Add(Case("unique_ids_across_instances", "TenantMetadata unique ids across instances", TestTags.Positive, () =>
            {
                TenantMetadata t1 = new TenantMetadata();
                TenantMetadata t2 = new TenantMetadata();
                AssertNotEqual(t1.Id, t2.Id);
            }));

            cases.Add(Case("serialization_round_trip", "TenantMetadata serialization round trip", TestTags.Positive, () =>
            {
                TenantMetadata tenant = new TenantMetadata("Serialization Test");
                tenant.Active = false;

                string json = JsonSerializer.Serialize(tenant);
                TenantMetadata deserialized = JsonSerializer.Deserialize<TenantMetadata>(json)!;

                AssertEqual(tenant.Id, deserialized.Id);
                AssertEqual(tenant.Name, deserialized.Name);
                AssertEqual(tenant.Active, deserialized.Active);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.TenantMetadata",
                displayName: "TenantMetadata Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.TenantMetadata",
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
                suiteId: "Models.TenantMetadata",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
