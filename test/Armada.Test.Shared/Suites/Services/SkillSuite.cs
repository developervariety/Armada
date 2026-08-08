namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the skills directory: the <see cref="Skill"/> model and SQLite persistence
    /// round-trips including category and active filtering. Positive cases assert defaults and
    /// persistence fidelity; negative cases assert rejection of empty name/id.
    /// </summary>
    public sealed class SkillSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.Skill";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Skill suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("model_defaults_are_correct", "Skill Defaults AreCorrect", TestTags.Positive, () =>
            {
                Skill skill = new Skill();
                AssertTrue(skill.Id.StartsWith(Constants.SkillIdPrefix), "Id should use skl_ prefix");
                AssertTrue(skill.Active, "Active should default true");
                AssertFalse(skill.IsBuiltIn, "IsBuiltIn should default false");
                AssertEqual(String.Empty, skill.Content);
            }));

            cases.Add(Case("model_name_empty_throws", "Skill SetName Empty Throws", TestTags.Negative, () =>
            {
                Skill skill = new Skill();
                AssertThrows<ArgumentNullException>(() => skill.Name = "");
            }));

            cases.Add(Case("model_id_empty_throws", "Skill SetId Empty Throws", TestTags.Negative, () =>
            {
                Skill skill = new Skill();
                AssertThrows<ArgumentNullException>(() => skill.Id = "");
            }));

            cases.Add(CaseAsync("persistence_create_read_update_delete", "Skill CreateReadUpdateDelete RoundTrip", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Skill skill = new Skill { Name = "Write ADRs", Category = "engineering", Content = "Always record architecture decisions." };
                    await db.Skills.CreateAsync(skill);

                    Skill? read = await db.Skills.ReadAsync(skill.Id);
                    AssertNotNull(read);
                    AssertEqual("Write ADRs", read!.Name);
                    AssertEqual("engineering", read.Category);
                    AssertContains("architecture decisions", read.Content);

                    read.Content = "Updated content.";
                    await db.Skills.UpdateAsync(read);
                    Skill? reread = await db.Skills.ReadAsync(skill.Id);
                    AssertEqual("Updated content.", reread!.Content);

                    await db.Skills.DeleteAsync(skill.Id);
                    AssertNull(await db.Skills.ReadAsync(skill.Id));
                }
            }));

            cases.Add(CaseAsync("enumerate_filters_by_category", "Skill Enumerate FiltersByCategory", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    await db.Skills.CreateAsync(new Skill { Name = "TDD", Category = "testing", Content = "x" });
                    await db.Skills.CreateAsync(new Skill { Name = "ADR", Category = "engineering", Content = "y" });

                    EnumerationResult<Skill> testing = await db.Skills.EnumerateAsync(new SkillQuery { Category = "testing" });
                    AssertEqual(1, (int)testing.TotalRecords);
                    AssertEqual("TDD", testing.Objects[0].Name);
                }
            }));

            cases.Add(CaseAsync("enumerate_filters_by_active", "Skill Enumerate FiltersByActive", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    await db.Skills.CreateAsync(new Skill { Name = "Active One", Content = "x", Active = true });
                    await db.Skills.CreateAsync(new Skill { Name = "Inactive One", Content = "y", Active = false });

                    EnumerationResult<Skill> activeOnly = await db.Skills.EnumerateAsync(new SkillQuery { Active = true });
                    AssertEqual(1, (int)activeOnly.TotalRecords);
                    AssertEqual("Active One", activeOnly.Objects[0].Name);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Skill",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
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
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
