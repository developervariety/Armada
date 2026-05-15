namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for built-in persona and pipeline seeding.
    /// </summary>
    public class PersonaSeedServiceTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Persona Seed Service";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("SeedAsync creates new built-in personas and expanded FullPipeline", async () =>
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

                    Pipeline? fullPipeline = await testDb.Driver.Pipelines.ReadByNameAsync("FullPipeline").ConfigureAwait(false);
                    AssertNotNull(fullPipeline, "FullPipeline should be seeded");
                    string seededOrder = String.Join(" | ", fullPipeline!.Stages.OrderBy(s => s.Order).Select(s => s.PersonaName));
                    AssertEqual(
                        "Product Manager | Architect | Worker | Usability Engineer | TestEngineer | Judge",
                        seededOrder,
                        "FullPipeline persona order");
                }
            });

            await RunTest("SeedAsync upgrades the legacy built-in FullPipeline order", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();

                    Pipeline legacy = new Pipeline("FullPipeline");
                    legacy.TenantId = Armada.Core.Constants.DefaultTenantId;
                    legacy.Description = "Architect then Worker then TestEngineer then Judge.";
                    legacy.IsBuiltIn = true;
                    legacy.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Architect") { PipelineId = legacy.Id, RequiresReview = true },
                        new PipelineStage(2, "Worker") { PipelineId = legacy.Id, RequiresReview = true },
                        new PipelineStage(3, "TestEngineer") { PipelineId = legacy.Id, RequiresReview = true },
                        new PipelineStage(4, "Judge") { PipelineId = legacy.Id, RequiresReview = true, ReviewDenyAction = ReviewDenyActionEnum.FailPipeline }
                    };

                    await testDb.Driver.Pipelines.CreateAsync(legacy).ConfigureAwait(false);

                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, logging);
                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? upgraded = await testDb.Driver.Pipelines.ReadByNameAsync("FullPipeline").ConfigureAwait(false);
                    AssertNotNull(upgraded, "FullPipeline should still exist");
                    string upgradedOrder = String.Join(" | ", upgraded!.Stages.OrderBy(s => s.Order).Select(s => s.PersonaName));
                    AssertEqual(
                        "Product Manager | Architect | Worker | Usability Engineer | TestEngineer | Judge",
                        upgradedOrder,
                        "Legacy FullPipeline should be upgraded in place");
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }
    }
}
