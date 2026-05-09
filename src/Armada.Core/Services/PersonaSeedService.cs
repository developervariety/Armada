namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
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
            await SeedPersonaAsync("Judge", "Reviews completed mission diffs for correctness and completeness.", "persona.judge", token).ConfigureAwait(false);
            await SeedPersonaAsync("TestEngineer", "Writes and updates tests for mission changes.", "persona.test_engineer", token).ConfigureAwait(false);
            await SeedPersonaAsync("DiagnosticProtocolReviewer", "Specialist reviewer for J1939, UDS, J1708, K-line, OEM seed-key/security access, diagnostic timing/framing, and banned reflash boundary checks.", "persona.diagnostic_protocol_reviewer", token).ConfigureAwait(false);
            await SeedPersonaAsync("TenantSecurityReviewer", "Specialist reviewer for multi-tenant authz/authn, tenant isolation, secrets, auditability, and cross-tenant leak risk.", "persona.tenant_security_reviewer", token).ConfigureAwait(false);
            await SeedPersonaAsync("MigrationDataReviewer", "Specialist reviewer for migrations, schema/provider parity, indexes, backfills, rollback/restart safety, and data-loss risk.", "persona.migration_data_reviewer", token).ConfigureAwait(false);
            await SeedPersonaAsync("PerformanceMemoryReviewer", "Specialist reviewer for memory/allocations, retained object graphs, process output/log growth, DB materialization, throughput, and resource lifetime.", "persona.performance_memory_reviewer", token).ConfigureAwait(false);
            await SeedPersonaAsync("PortingReferenceAnalyst", "Specialist analyst for approved reference material, decompiler-derived notes, vendor traces, protocol captures, and semantic parity evidence for porting work.", "persona.porting_reference_analyst", token).ConfigureAwait(false);
            await SeedPersonaAsync("FrontendWorkflowReviewer", "Specialist reviewer for frontend UX/workflow, accessibility, responsive states, i18n, errors, and design consistency.", "persona.frontend_workflow_reviewer", token).ConfigureAwait(false);
            await SeedPersonaAsync("MemoryConsolidator", "Curates the per-vessel learned-facts playbook from completed-mission evidence. Read-only on logs/diffs/notes; writes proposals to AgentOutput only.", "persona.memory_consolidator", token).ConfigureAwait(false);
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
                "Worker then TestEngineer then Judge.",
                new List<PipelineStage> { new PipelineStage(1, "Worker"), new PipelineStage(2, "TestEngineer"), new PipelineStage(3, "Judge") },
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "FullPipeline",
                "Architect then Worker then TestEngineer then Judge.",
                new List<PipelineStage> { new PipelineStage(1, "Architect"), new PipelineStage(2, "Worker"), new PipelineStage(3, "TestEngineer"), new PipelineStage(4, "Judge") },
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "DiagnosticProtocolTested",
                "Worker then DiagnosticProtocolReviewer then TestEngineer then Judge.",
                BuildSpecialistTestedStages("DiagnosticProtocolReviewer"),
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "TenantSecurityTested",
                "Worker then TenantSecurityReviewer then TestEngineer then Judge.",
                BuildSpecialistTestedStages("TenantSecurityReviewer"),
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "MigrationDataTested",
                "Worker then MigrationDataReviewer then TestEngineer then Judge.",
                BuildSpecialistTestedStages("MigrationDataReviewer"),
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "PerformanceMemoryTested",
                "Worker then PerformanceMemoryReviewer then TestEngineer then Judge.",
                BuildSpecialistTestedStages("PerformanceMemoryReviewer"),
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "ReferencePortingTested",
                "Worker then PortingReferenceAnalyst then TestEngineer then Judge.",
                BuildSpecialistTestedStages("PortingReferenceAnalyst"),
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "FrontendWorkflowTested",
                "Worker then FrontendWorkflowReviewer then TestEngineer then Judge.",
                BuildSpecialistTestedStages("FrontendWorkflowReviewer"),
                token).ConfigureAwait(false);

            await SeedPipelineAsync(
                "Reflections",
                "Single-stage memory consolidation. Output is the candidate playbook + diff; orchestrator reviews. No TestEngineer or Judge stage runs.",
                new List<PipelineStage> { new PipelineStage(1, "MemoryConsolidator") { PreferredModel = "high" } },
                token).ConfigureAwait(false);
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

        private static List<PipelineStage> BuildSpecialistTestedStages(string specialistPersonaName)
        {
            return new List<PipelineStage>
            {
                new PipelineStage(1, "Worker"),
                new PipelineStage(2, specialistPersonaName) { PreferredModel = "high" },
                new PipelineStage(3, "TestEngineer"),
                new PipelineStage(4, "Judge")
            };
        }

        #endregion
    }
}
