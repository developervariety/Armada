namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for built-in persona and pipeline seeding via <see cref="PersonaSeedService"/>:
    /// creating the built-in personas and the four-stage FullPipeline on a fresh store, and keeping an
    /// existing built-in FullPipeline and TestEngineer persona unchanged on an upgraded store. Each case
    /// builds a fresh store.
    /// </summary>
    public sealed class PersonaSeedServiceSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.PersonaSeedService";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Persona Seed Service suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("seed_async_creates_built_in_personas_and_four_stage_full_pipeline", "SeedAsync creates built-in personas and the four-stage FullPipeline", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, logging);

                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? productManager = await testDb.Driver.Personas.ReadByNameAsync("Product Manager").ConfigureAwait(false);
                    Persona? usabilityEngineer = await testDb.Driver.Personas.ReadByNameAsync("Usability Engineer").ConfigureAwait(false);
                    AssertNotNull(productManager, "Product Manager persona should be seeded");
                    AssertNotNull(usabilityEngineer, "Usability Engineer persona should be seeded");
                    AssertEqual("persona.product_manager", productManager!.PromptTemplateName, "Product Manager prompt template");
                    AssertEqual("persona.usability_engineer", usabilityEngineer!.PromptTemplateName, "Usability Engineer prompt template");

                    // The built-in test persona is seeded under the name existing pipelines and prompt
                    // templates reference.
                    Persona? testEngineer = await testDb.Driver.Personas.ReadByNameAsync(PersonaCatalog.LegacyTestEngineer).ConfigureAwait(false);
                    AssertNotNull(testEngineer, "The TestEngineer persona should be seeded");
                    AssertEqual("persona.test_engineer", testEngineer!.PromptTemplateName, "TestEngineer prompt template");
                    AssertTrue(testEngineer.IsBuiltIn, "The TestEngineer persona should be built in");
                    AssertTrue(testEngineer.Active, "The TestEngineer persona should be active");
                    AssertNull(await testDb.Driver.Personas.ReadByNameAsync(PersonaCatalog.TestEngineer).ConfigureAwait(false),
                        "Seeding creates no second test persona under the display name");

                    Pipeline? fullPipeline = await testDb.Driver.Pipelines.ReadByNameAsync("FullPipeline").ConfigureAwait(false);
                    AssertNotNull(fullPipeline, "FullPipeline should be seeded");
                    AssertEqual(
                        "Architect | Worker | TestEngineer | Judge",
                        StageOrder(fullPipeline!),
                        "FullPipeline persona order");
                }
            }));

            cases.Add(CaseAsync("seed_async_keeps_an_existing_built_in_full_pipeline_record_and_order", "SeedAsync keeps an existing built-in FullPipeline record and its order", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();

                    Pipeline existing = new Pipeline("FullPipeline");
                    existing.TenantId = Armada.Core.Constants.DefaultTenantId;
                    existing.Description = "Architect then Worker then TestEngineer then Judge.";
                    existing.IsBuiltIn = true;
                    existing.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Architect") { PipelineId = existing.Id, RequiresReview = true },
                        new PipelineStage(2, "Worker") { PipelineId = existing.Id, RequiresReview = true },
                        new PipelineStage(3, PersonaCatalog.LegacyTestEngineer) { PipelineId = existing.Id, RequiresReview = true },
                        new PipelineStage(4, "Judge") { PipelineId = existing.Id, RequiresReview = true, ReviewDenyAction = ReviewDenyActionEnum.FailPipeline }
                    };

                    await testDb.Driver.Pipelines.CreateAsync(existing).ConfigureAwait(false);

                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, logging);
                    await service.SeedAsync().ConfigureAwait(false);

                    // Operator pipelines and prompt templates reference this record and these stage names,
                    // so seeding an upgraded database leaves the record, its order and its review gates alone.
                    Pipeline? kept = await testDb.Driver.Pipelines.ReadByNameAsync("FullPipeline").ConfigureAwait(false);
                    AssertNotNull(kept, "FullPipeline should still exist");
                    AssertEqual(existing.Id, kept!.Id, "Seeding keeps the existing FullPipeline record");
                    AssertEqual("Architect | Worker | TestEngineer | Judge", StageOrder(kept), "Seeding keeps the FullPipeline order");
                    PipelineStage judge = kept.Stages.OrderBy(s => s.Order).Last();
                    AssertTrue(judge.RequiresReview, "Seeding keeps the Judge stage review gate");
                    AssertEqual(ReviewDenyActionEnum.FailPipeline, judge.ReviewDenyAction, "Seeding keeps the Judge stage deny action");
                }
            }));

            cases.Add(CaseAsync("seed_async_keeps_the_existing_built_in_test_engineer_persona", "SeedAsync keeps the existing built-in TestEngineer persona", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();

                    Persona existing = new Persona(PersonaCatalog.LegacyTestEngineer, "persona.test_engineer");
                    existing.TenantId = Armada.Core.Constants.DefaultTenantId;
                    existing.Description = "Writes and updates tests for mission changes.";
                    existing.IsBuiltIn = true;
                    await testDb.Driver.Personas.CreateAsync(existing).ConfigureAwait(false);

                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, logging);
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? kept = await testDb.Driver.Personas.ReadByNameAsync(PersonaCatalog.LegacyTestEngineer).ConfigureAwait(false);
                    AssertNotNull(kept, "The TestEngineer persona should keep its name");
                    AssertEqual(existing.Id, kept!.Id, "Seeding keeps the existing TestEngineer record");
                    AssertNull(await testDb.Driver.Personas.ReadByNameAsync(PersonaCatalog.TestEngineer).ConfigureAwait(false),
                        "Seeding neither renames the persona nor adds a second one under the display name");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Persona Seed Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static string StageOrder(Pipeline pipeline)
        {
            return String.Join(" | ", pipeline.Stages.OrderBy(s => s.Order).Select(s => s.PersonaName));
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
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
