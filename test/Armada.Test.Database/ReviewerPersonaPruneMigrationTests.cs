namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Reviewer persona prune migration proof. A database one version below the prune holds the three named
    /// built-in reviewer personas with their built-in templates: one unreferenced, one named by a pipeline stage and
    /// one named by a captain allow-list, beside an operator persona and template. The migration deletes only the
    /// unreferenced persona and its template, keeps the referenced ones, restarts cleanly after an interruption and
    /// leaves every other row unchanged.
    /// </summary>
    internal sealed class ReviewerPersonaPruneMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal ReviewerPersonaPruneMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 101, DatabaseTypeEnum.Postgresql => 102,
                DatabaseTypeEnum.Mysql => 93, DatabaseTypeEnum.SqlServer => 96,
                _ => throw new NotSupportedException()
            };

            await StopAtAsync(version, -1, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(before.Keys.All(key => key < version), "The prune is still pending and no later version applied");

            using (DatabaseDriver driver = CreateDriver())
            {
                await CreateTemplateAsync(driver, "persona.migration_data_reviewer", true, token).ConfigureAwait(false);
                await CreateTemplateAsync(driver, "persona.performance_memory_reviewer", true, token).ConfigureAwait(false);
                await CreateTemplateAsync(driver, "persona.frontend_workflow_reviewer", true, token).ConfigureAwait(false);
                await CreateTemplateAsync(driver, "persona.operator_reviewer", false, token).ConfigureAwait(false);

                await CreatePersonaAsync(driver, "MigrationDataReviewer", "persona.migration_data_reviewer", true, token).ConfigureAwait(false);
                await CreatePersonaAsync(driver, "PerformanceMemoryReviewer", "persona.performance_memory_reviewer", true, token).ConfigureAwait(false);
                await CreatePersonaAsync(driver, "FrontendWorkflowReviewer", "persona.frontend_workflow_reviewer", true, token).ConfigureAwait(false);
                await CreatePersonaAsync(driver, "OperatorReviewer", "persona.operator_reviewer", false, token).ConfigureAwait(false);

                Pipeline pipeline = new Pipeline("OperatorPerformanceReview");
                pipeline.TenantId = Constants.DefaultTenantId;
                pipeline.Stages = new List<PipelineStage> { new PipelineStage(1, "Worker"), new PipelineStage(2, "PerformanceMemoryReviewer") };
                await driver.Pipelines.CreateAsync(pipeline, token).ConfigureAwait(false);

                Captain captain = new Captain("frontend-review-captain");
                captain.TenantId = Constants.DefaultTenantId;
                captain.AllowedPersonas = "[\"Worker\",\"FrontendWorkflowReviewer\"]";
                await driver.Captains.CreateAsync(captain, token).ConfigureAwait(false);
            }

            await StopAtAsync(version, 1, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "An interrupted prune records no version");
            MigrationScenarioRunner.AssertHistory(before, interrupted);

            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                DatabaseAssert.True(committed.ContainsKey(version), "The restarted run commits the prune version");
                MigrationScenarioRunner.AssertHistory(before, committed);

                await AssertOutcomeAsync(driver, token).ConfigureAwait(false);

                await driver.InitializeAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(committed, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await AssertOutcomeAsync(driver, token).ConfigureAwait(false);
            }

            Console.WriteLine("PASS reviewer persona prune migration: unreferenced persona and template deleted, stage- and captain-referenced personas and their templates kept, operator rows kept, interrupted run restarts");
        }

        private async Task AssertOutcomeAsync(DatabaseDriver driver, CancellationToken token)
        {
            DatabaseAssert.True(await driver.Personas.ReadByNameAsync("MigrationDataReviewer", token).ConfigureAwait(false) == null, "An unreferenced reviewer persona is deleted");
            DatabaseAssert.True(await driver.PromptTemplates.ReadByNameAsync("persona.migration_data_reviewer", token).ConfigureAwait(false) == null, "Its built-in template is deleted");

            DatabaseAssert.NotNull(await driver.Personas.ReadByNameAsync("PerformanceMemoryReviewer", token).ConfigureAwait(false), "A persona named by a pipeline stage is kept");
            DatabaseAssert.NotNull(await driver.PromptTemplates.ReadByNameAsync("persona.performance_memory_reviewer", token).ConfigureAwait(false), "The template of a kept persona is kept");
            DatabaseAssert.NotNull(await driver.Personas.ReadByNameAsync("FrontendWorkflowReviewer", token).ConfigureAwait(false), "A persona named by a captain allow-list is kept");
            DatabaseAssert.NotNull(await driver.PromptTemplates.ReadByNameAsync("persona.frontend_workflow_reviewer", token).ConfigureAwait(false), "The template of a captain-referenced persona is kept");

            DatabaseAssert.NotNull(await driver.Personas.ReadByNameAsync("OperatorReviewer", token).ConfigureAwait(false), "An operator persona is kept");
            DatabaseAssert.NotNull(await driver.PromptTemplates.ReadByNameAsync("persona.operator_reviewer", token).ConfigureAwait(false), "An operator template is kept");
        }

        private static async Task CreateTemplateAsync(DatabaseDriver driver, string name, bool builtIn, CancellationToken token)
        {
            PromptTemplate template = new PromptTemplate(name, "template body for " + name);
            template.TenantId = Constants.DefaultTenantId;
            template.Category = "persona";
            template.IsBuiltIn = builtIn;
            await driver.PromptTemplates.CreateAsync(template, token).ConfigureAwait(false);
        }

        private static async Task CreatePersonaAsync(DatabaseDriver driver, string name, string templateName, bool builtIn, CancellationToken token)
        {
            Persona persona = new Persona(name, templateName);
            persona.TenantId = Constants.DefaultTenantId;
            persona.IsBuiltIn = builtIn;
            await driver.Personas.CreateAsync(persona, token).ConfigureAwait(false);
        }

        private async Task StopAtAsync(int version, int ordinal, CancellationToken token)
        {
            using (DatabaseDriver driver = CreateDriver())
            {
                driver.MigrationCheckpoint = (current, statement) =>
                {
                    if (current == version && statement == ordinal) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Reviewer persona prune checkpoint was not reached"); }
                catch (StopException) { }
            }
        }

        private DatabaseDriver CreateDriver()
        {
            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
            return DatabaseDriverFactory.Create(_Settings, logging);
        }
    }
}
