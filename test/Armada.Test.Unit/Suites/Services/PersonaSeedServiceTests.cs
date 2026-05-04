namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for startup seeding and reconciliation of built-in personas and pipelines.
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
            await RunTest("Seed creates specialist personas and pipelines", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Dictionary<string, string> expectedPersonas = GetSpecialistPersonaTemplates();
                    foreach (KeyValuePair<string, string> kvp in expectedPersonas)
                    {
                        Persona? persona = await testDb.Driver.Personas.ReadByNameAsync(kvp.Key).ConfigureAwait(false);
                        AssertNotNull(persona, "Persona should be seeded: " + kvp.Key);
                        AssertEqual(kvp.Key, persona!.Name, "Persona name");
                        AssertEqual(kvp.Value, persona.PromptTemplateName, "Prompt template for " + kvp.Key);
                        AssertTrue(persona.IsBuiltIn, "Persona should be built in: " + kvp.Key);
                        AssertTrue(persona.Active, "Persona should be active: " + kvp.Key);
                        AssertEqual(Constants.DefaultTenantId, persona.TenantId, "Persona tenant for " + kvp.Key);
                    }

                    Dictionary<string, string> expectedPipelines = GetSpecialistPipelineStages();
                    foreach (KeyValuePair<string, string> kvp in expectedPipelines)
                    {
                        Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync(kvp.Key).ConfigureAwait(false);
                        AssertSpecialistPipeline(kvp.Key, kvp.Value, pipeline);
                    }
                }
            });

            await RunTest("Seed upgrades existing specialist persona and pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona existingPersona = new Persona("DiagnosticProtocolReviewer", "persona.old");
                    existingPersona.Description = "Runtime-created old persona";
                    existingPersona.IsBuiltIn = false;
                    existingPersona.Active = false;
                    await testDb.Driver.Personas.CreateAsync(existingPersona).ConfigureAwait(false);

                    Pipeline existingPipeline = new Pipeline("DiagnosticProtocolTested");
                    existingPipeline.Description = "Runtime-created old pipeline";
                    existingPipeline.IsBuiltIn = false;
                    existingPipeline.Active = false;
                    existingPipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge") { PreferredModel = "low" }
                    };
                    await testDb.Driver.Pipelines.CreateAsync(existingPipeline).ConfigureAwait(false);

                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? persona = await testDb.Driver.Personas.ReadByNameAsync("DiagnosticProtocolReviewer").ConfigureAwait(false);
                    AssertNotNull(persona, "Persona should still exist after reconciliation");
                    AssertEqual("persona.diagnostic_protocol_reviewer", persona!.PromptTemplateName, "Persona template should be canonical");
                    AssertContains("J1939", persona.Description ?? "", "Persona description should be canonical");
                    AssertTrue(persona.IsBuiltIn, "Persona should be upgraded to built in");
                    AssertTrue(persona.Active, "Persona should be reactivated");
                    AssertEqual(Constants.DefaultTenantId, persona.TenantId, "Persona should be reconciled to default tenant");

                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("DiagnosticProtocolTested").ConfigureAwait(false);
                    AssertSpecialistPipeline("DiagnosticProtocolTested", "DiagnosticProtocolReviewer", pipeline);
                    AssertContains("DiagnosticProtocolReviewer", pipeline!.Description ?? "", "Pipeline description should be canonical");
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static Dictionary<string, string> GetSpecialistPersonaTemplates()
        {
            return new Dictionary<string, string>
            {
                { "DiagnosticProtocolReviewer", "persona.diagnostic_protocol_reviewer" },
                { "TenantSecurityReviewer", "persona.tenant_security_reviewer" },
                { "MigrationDataReviewer", "persona.migration_data_reviewer" },
                { "PerformanceMemoryReviewer", "persona.performance_memory_reviewer" },
                { "PortingReferenceAnalyst", "persona.porting_reference_analyst" },
                { "FrontendWorkflowReviewer", "persona.frontend_workflow_reviewer" }
            };
        }

        private static Dictionary<string, string> GetSpecialistPipelineStages()
        {
            return new Dictionary<string, string>
            {
                { "DiagnosticProtocolTested", "DiagnosticProtocolReviewer" },
                { "TenantSecurityTested", "TenantSecurityReviewer" },
                { "MigrationDataTested", "MigrationDataReviewer" },
                { "PerformanceMemoryTested", "PerformanceMemoryReviewer" },
                { "ReferencePortingTested", "PortingReferenceAnalyst" },
                { "FrontendWorkflowTested", "FrontendWorkflowReviewer" }
            };
        }

        private void AssertSpecialistPipeline(string pipelineName, string specialistPersonaName, Pipeline? pipeline)
        {
            AssertNotNull(pipeline, "Pipeline should be seeded: " + pipelineName);
            AssertEqual(pipelineName, pipeline!.Name, "Pipeline name");
            AssertTrue(pipeline.IsBuiltIn, "Pipeline should be built in: " + pipelineName);
            AssertTrue(pipeline.Active, "Pipeline should be active: " + pipelineName);
            AssertEqual(Constants.DefaultTenantId, pipeline.TenantId, "Pipeline tenant for " + pipelineName);
            AssertEqual(4, pipeline.Stages.Count, "Specialist tested pipeline should have four stages");

            List<PipelineStage> ordered = pipeline.Stages.OrderBy(s => s.Order).ToList();
            AssertEqual("Worker", ordered[0].PersonaName, "Stage 1 persona");
            AssertEqual(specialistPersonaName, ordered[1].PersonaName, "Stage 2 persona");
            AssertEqual("high", ordered[1].PreferredModel, "Specialist stage preferred model");
            AssertEqual("TestEngineer", ordered[2].PersonaName, "Stage 3 persona");
            AssertEqual("Judge", ordered[3].PersonaName, "Stage 4 persona");
        }
    }
}
