namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Seeds built-in personas and pipelines into the database on startup.
    /// </summary>
    public class PersonaSeedService
    {
        #region Private-Members

        private string _Header = "[PersonaSeedService] ";
        private DatabaseDriver _Database;
        private LoggingModule _Logging;
        private readonly IReadOnlyList<AdditionalPersonaSettings> _AdditionalPersonas;
        private readonly IReadOnlyList<AdditionalPipelineSettings> _AdditionalPipelines;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="additionalPersonas">Optional extra personas from settings.</param>
        /// <param name="additionalPipelines">Optional extra pipelines from settings.</param>
        public PersonaSeedService(
            DatabaseDriver database,
            LoggingModule logging,
            IReadOnlyList<AdditionalPersonaSettings>? additionalPersonas = null,
            IReadOnlyList<AdditionalPipelineSettings>? additionalPipelines = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _AdditionalPersonas = additionalPersonas ?? Array.Empty<AdditionalPersonaSettings>();
            _AdditionalPipelines = additionalPipelines ?? Array.Empty<AdditionalPipelineSettings>();
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
            await SeedPersonaAsync(PersonaCatalog.Linter, "Evaluates changed code and documentation for style and correctness, fixes clear in-scope violations, and reports findings.", "persona.linter", token).ConfigureAwait(false);
            await SeedPersonaAsync(PersonaCatalog.Recorder, "Reviews the finished work of a voyage and records what is worth remembering into native captain memory.", "persona.recorder", token).ConfigureAwait(false);
            await SeedPersonaAsync(PersonaCatalog.PriorArtAnalyst, "Read-only research analyst that settles whether an objective's deliverable already exists before a Worker builds it; commits nothing.", "persona.prior_art_analyst", token).ConfigureAwait(false);

            foreach (AdditionalPersonaSettings extra in _AdditionalPersonas)
            {
                if (extra == null || String.IsNullOrWhiteSpace(extra.Name) || String.IsNullOrWhiteSpace(extra.PromptTemplateName))
                    continue;
                await SeedPersonaAsync(extra.Name.Trim(), extra.Description ?? String.Empty, extra.PromptTemplateName.Trim(), token).ConfigureAwait(false);
            }
        }

        private async Task SeedPersonaAsync(string name, string description, string templateName, CancellationToken token)
        {
            Persona? existing = await _Database.Personas.ReadByNameAsync(name, token).ConfigureAwait(false);
            if (existing != null)
            {
                if (IsCanonicalPersona(existing, description, templateName))
                {
                    return;
                }

                existing.TenantId = Constants.DefaultTenantId;
                existing.Description = description;
                existing.PromptTemplateName = templateName;
                existing.IsBuiltIn = true;
                existing.Active = true;

                await _Database.Personas.UpdateAsync(existing, token).ConfigureAwait(false);
                _Logging.Info(_Header + "reconciled built-in persona: " + name);
                return;
            }

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
                new List<PipelineStage> { new PipelineStage(1, "Worker"), new PipelineStage(2, "Judge") },
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "Tested",
                "Worker then TestEngineer then Linter then Judge.",
                new List<PipelineStage> { new PipelineStage(1, "Worker"), new PipelineStage(2, "TestEngineer"), new PipelineStage(3, PersonaCatalog.Linter) { PreferredModel = "mid" }, new PipelineStage(4, "Judge") },
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "FullPipeline",
                "Architect then Worker then TestEngineer then Judge.",
                new List<PipelineStage> { new PipelineStage(1, "Architect"), new PipelineStage(2, "Worker"), new PipelineStage(3, "TestEngineer"), new PipelineStage(4, "Judge") },
                token).ConfigureAwait(false);

            // The Linter tidies and flags the changed code and documentation before the Judge reviews it.
            // It commits only mechanical in-scope fixes, so it runs at the mid tier like the Worker.
            // FullPipeline stays without a Linter: it is the minimal canonical review shape, and the
            // reconcile would force the extra stage onto every deployment still running the plain
            // FullPipeline. Tested and ProductDevelopment carry the Linter because they are the
            // built-in pipelines that produce vessel code; the reference-porting pipeline carries it
            // too, configured as an additional pipeline rather than a core built-in.
            //
            // The final Recorder stage distils the finished product work into durable native memory.
            // It writes memory, not code, so it produces no commit; it runs at the mid tier so it
            // never competes for the scarce high-tier specialist and Judge captains.
            await SeedPipelineAsync(
                "ProductDevelopment",
                "Product Manager then Architect then Worker then Usability Engineer then TestEngineer then Linter then Judge then Recorder.",
                new List<PipelineStage>
                {
                    new PipelineStage(1, "Product Manager") { PreferredModel = "high" },
                    new PipelineStage(2, "Architect") { PreferredModel = "high" },
                    new PipelineStage(3, "Worker"),
                    new PipelineStage(4, "Usability Engineer") { PreferredModel = "high" },
                    new PipelineStage(5, "TestEngineer"),
                    new PipelineStage(6, PersonaCatalog.Linter) { PreferredModel = "mid" },
                    new PipelineStage(7, "Judge") { PreferredModel = "high" },
                    new PipelineStage(8, PersonaCatalog.Recorder) { PreferredModel = "mid" }
                },
                token).ConfigureAwait(false);

            // Seeded only when absent. An operator who already runs a pipeline of this name keeps it,
            // and no existing pipeline gains a Recorder stage: where the Recorder belongs in a pipeline
            // is an owner decision, not a side effect of startup.
            await SeedPipelineIfAbsentAsync(
                "Recorded",
                "Worker then Recorder -- do the work, then record what is worth remembering.",
                new List<PipelineStage> { new PipelineStage(1, "Worker"), new PipelineStage(2, PersonaCatalog.Recorder) },
                token).ConfigureAwait(false);

            foreach (AdditionalPipelineSettings extra in _AdditionalPipelines)
            {
                if (extra == null || String.IsNullOrWhiteSpace(extra.Name) || extra.Stages == null || extra.Stages.Count == 0)
                    continue;

                List<PipelineStage> stages = new List<PipelineStage>();
                foreach (AdditionalPipelineStageSettings stage in extra.Stages)
                {
                    if (stage == null || String.IsNullOrWhiteSpace(stage.PersonaName))
                        continue;
                    PipelineStage built = new PipelineStage(stage.Order < 1 ? 1 : stage.Order, stage.PersonaName.Trim());
                    if (!String.IsNullOrWhiteSpace(stage.PreferredModel))
                        built.PreferredModel = stage.PreferredModel.Trim();
                    stages.Add(built);
                }

                if (stages.Count == 0)
                    continue;

                await SeedPipelineAsync(extra.Name.Trim(), extra.Description ?? String.Empty, stages, token).ConfigureAwait(false);
            }
        }

        private async Task SeedPipelineIfAbsentAsync(string name, string description, List<PipelineStage> stages, CancellationToken token)
        {
            Pipeline? existing = await _Database.Pipelines.ReadByNameAsync(name, token).ConfigureAwait(false);
            if (existing != null) return;
            await SeedPipelineAsync(name, description, stages, token).ConfigureAwait(false);
        }

        private async Task SeedPipelineAsync(string name, string description, List<PipelineStage> stages, CancellationToken token)
        {
            Pipeline? existing = await _Database.Pipelines.ReadByNameAsync(name, token).ConfigureAwait(false);
            if (existing != null)
            {
                if (IsCanonicalPipeline(existing, description, stages))
                {
                    return;
                }

                existing.TenantId = Constants.DefaultTenantId;
                existing.Description = description;
                existing.IsBuiltIn = true;
                existing.Active = true;
                existing.Stages = stages;

                foreach (PipelineStage stage in stages)
                {
                    stage.PipelineId = existing.Id;
                }

                await _Database.Pipelines.UpdateAsync(existing, token).ConfigureAwait(false);
                _Logging.Info(_Header + "reconciled built-in pipeline: " + name);
                return;
            }

            Pipeline pipeline = new Pipeline();
            pipeline.TenantId = Constants.DefaultTenantId;
            pipeline.Name = name;
            pipeline.Description = description;
            pipeline.IsBuiltIn = true;
            pipeline.Stages = stages;

            foreach (PipelineStage stage in stages)
            {
                stage.PipelineId = pipeline.Id;
            }

            await _Database.Pipelines.CreateAsync(pipeline, token).ConfigureAwait(false);
            _Logging.Info(_Header + "seeded built-in pipeline: " + name);
        }

        private static bool IsCanonicalPersona(Persona persona, string description, string templateName)
        {
            return String.Equals(persona.TenantId, Constants.DefaultTenantId, StringComparison.Ordinal) &&
                String.Equals(persona.Description, description, StringComparison.Ordinal) &&
                String.Equals(persona.PromptTemplateName, templateName, StringComparison.Ordinal) &&
                persona.IsBuiltIn &&
                persona.Active;
        }

        private static bool IsCanonicalPipeline(Pipeline pipeline, string description, List<PipelineStage> stages)
        {
            if (!String.Equals(pipeline.TenantId, Constants.DefaultTenantId, StringComparison.Ordinal) ||
                !String.Equals(pipeline.Description, description, StringComparison.Ordinal) ||
                !pipeline.IsBuiltIn ||
                !pipeline.Active ||
                pipeline.Stages.Count != stages.Count)
            {
                return false;
            }

            for (int i = 0; i < stages.Count; i++)
            {
                PipelineStage existing = pipeline.Stages[i];
                PipelineStage expected = stages[i];

                if (existing.Order != expected.Order ||
                    !String.Equals(existing.PersonaName, expected.PersonaName, StringComparison.Ordinal) ||
                    existing.IsOptional != expected.IsOptional ||
                    !String.Equals(existing.Description ?? "", expected.Description ?? "", StringComparison.Ordinal) ||
                    !String.Equals(existing.PreferredModel ?? "", expected.PreferredModel ?? "", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        #endregion
    }
}
