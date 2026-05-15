namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Seeds built-in personas and pipelines into the database on startup.
    /// </summary>
    public class PersonaSeedService
    {
        #region Private-Members

        private string _Header = "[PersonaSeedService] ";
        private DatabaseDriver _Database;
        private LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        public PersonaSeedService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Seed all built-in personas and pipelines if they don't already exist.
        /// </summary>
        public async Task SeedAsync(CancellationToken token = default)
        {
            await SeedPersonasAsync(token).ConfigureAwait(false);
            await SeedPipelinesAsync(token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private async Task SeedPersonasAsync(CancellationToken token)
        {
            await SeedPersonaAsync("Worker", "Standard mission executor -- writes code, makes changes, commits work.", "persona.worker", token).ConfigureAwait(false);
            await SeedPersonaAsync("Architect", "Plans voyages and decomposes work into right-sized missions.", "persona.architect", token).ConfigureAwait(false);
            await SeedPersonaAsync("Product Manager", "Shapes the whole product picture, clarifies user outcomes, and turns dispatched work into durable requirements.", "persona.product_manager", token).ConfigureAwait(false);
            await SeedPersonaAsync("Usability Engineer", "Improves usability, edge-case experience, and consistency with the surrounding product.", "persona.usability_engineer", token).ConfigureAwait(false);
            await SeedPersonaAsync("Judge", "Reviews completed mission diffs for correctness and completeness.", "persona.judge", token).ConfigureAwait(false);
            await SeedPersonaAsync("TestEngineer", "Writes and updates tests for mission changes.", "persona.test_engineer", token).ConfigureAwait(false);
        }

        private async Task SeedPersonaAsync(string name, string description, string templateName, CancellationToken token)
        {
            bool exists = await _Database.Personas.ExistsByNameAsync(name, token).ConfigureAwait(false);
            if (exists) return;

            Persona persona = new Persona();
            persona.TenantId = Constants.DefaultTenantId;
            persona.Name = name;
            persona.Description = description;
            persona.PromptTemplateName = templateName;
            persona.IsBuiltIn = true;

            await _Database.Personas.CreateAsync(persona, token).ConfigureAwait(false);
            _Logging.Info(_Header + "seeded built-in persona: " + name);
        }

        private async Task SeedPipelinesAsync(CancellationToken token)
        {
            await SeedPipelineAsync(
                "WorkerOnly",
                "Single worker stage -- backward compatible default.",
                new List<PipelineStage> { new PipelineStage(1, "Worker") },
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "Reviewed",
                "Worker then Judge review.",
                new List<PipelineStage>
                {
                    new PipelineStage(1, "Worker") { RequiresReview = true },
                    new PipelineStage(2, "Judge") { RequiresReview = true, ReviewDenyAction = ReviewDenyActionEnum.FailPipeline }
                },
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "Tested",
                "Worker then TestEngineer then Judge.",
                new List<PipelineStage>
                {
                    new PipelineStage(1, "Worker") { RequiresReview = true },
                    new PipelineStage(2, "TestEngineer") { RequiresReview = true },
                    new PipelineStage(3, "Judge") { RequiresReview = true, ReviewDenyAction = ReviewDenyActionEnum.FailPipeline }
                },
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "FullPipeline",
                "Product Manager then Architect then Worker then Usability Engineer then TestEngineer then Judge.",
                new List<PipelineStage>
                {
                    new PipelineStage(1, "Product Manager") { RequiresReview = true },
                    new PipelineStage(2, "Architect") { RequiresReview = true },
                    new PipelineStage(3, "Worker") { RequiresReview = true },
                    new PipelineStage(4, "Usability Engineer") { RequiresReview = true },
                    new PipelineStage(5, "TestEngineer") { RequiresReview = true },
                    new PipelineStage(6, "Judge") { RequiresReview = true, ReviewDenyAction = ReviewDenyActionEnum.FailPipeline }
                },
                token).ConfigureAwait(false);
        }

        private async Task SeedPipelineAsync(string name, string description, List<PipelineStage> stages, CancellationToken token)
        {
            Pipeline? existing = await _Database.Pipelines.ReadByNameAsync(name, token).ConfigureAwait(false);
            if (existing != null)
            {
                if (ShouldUpgradeBuiltInPipeline(existing))
                {
                    existing.Description = description;
                    existing.Stages = CloneStages(existing.Id, stages);
                    existing.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Pipelines.UpdateAsync(existing, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "upgraded built-in pipeline: " + name);
                }
                return;
            }

            Pipeline pipeline = new Pipeline();
            pipeline.TenantId = Constants.DefaultTenantId;
            pipeline.Name = name;
            pipeline.Description = description;
            pipeline.IsBuiltIn = true;
            pipeline.Stages = CloneStages(pipeline.Id, stages);

            await _Database.Pipelines.CreateAsync(pipeline, token).ConfigureAwait(false);
            _Logging.Info(_Header + "seeded built-in pipeline: " + name);
        }

        private static List<PipelineStage> CloneStages(string pipelineId, IEnumerable<PipelineStage> stages)
        {
            List<PipelineStage> clones = new List<PipelineStage>();
            foreach (PipelineStage stage in stages)
            {
                clones.Add(new PipelineStage(stage.Order, stage.PersonaName)
                {
                    PipelineId = pipelineId,
                    IsOptional = stage.IsOptional,
                    Description = stage.Description,
                    RequiresReview = stage.RequiresReview,
                    ReviewDenyAction = stage.ReviewDenyAction
                });
            }

            return clones;
        }

        private static bool ShouldUpgradeBuiltInPipeline(Pipeline existing)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (!existing.IsBuiltIn) return false;
            if (!String.Equals(existing.Name, "FullPipeline", StringComparison.Ordinal)) return false;
            if (existing.Stages == null || existing.Stages.Count == 0) return false;

            List<string> personaOrder = existing.Stages
                .OrderBy(stage => stage.Order)
                .Select(stage => stage.PersonaName)
                .ToList();

            return personaOrder.SequenceEqual(
                new[] { "Architect", "Worker", "TestEngineer", "Judge" },
                StringComparer.Ordinal);
        }

        #endregion
    }
}
