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
    using FleetRoutingSettings = global::Test.Shared.Infrastructure.FleetRoutingSettings;

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
                    PersonaSeedService service = NewFleetService(testDb);
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

            await RunTest("Vanilla seed does not create specialist reviewer personas", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? specialist = await testDb.Driver.Personas.ReadByNameAsync("DiagnosticProtocolReviewer").ConfigureAwait(false);
                    AssertNull(specialist, "product defaults must not seed DiagnosticProtocolReviewer");
                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("DiagnosticProtocolTested").ConfigureAwait(false);
                    AssertNull(pipeline, "product defaults must not seed DiagnosticProtocolTested");
                }
            });

            await RunTest("Seed creates product personas and ProductDevelopment pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? productManager = await testDb.Driver.Personas.ReadByNameAsync("Product Manager").ConfigureAwait(false);
                    AssertProductPersona("Product Manager", "persona.product_manager", productManager);

                    Persona? usabilityEngineer = await testDb.Driver.Personas.ReadByNameAsync("Usability Engineer").ConfigureAwait(false);
                    AssertProductPersona("Usability Engineer", "persona.usability_engineer", usabilityEngineer);

                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("ProductDevelopment").ConfigureAwait(false);
                    AssertNotNull(pipeline, "ProductDevelopment pipeline should be seeded");
                    AssertEqual("ProductDevelopment", pipeline!.Name, "Pipeline name");
                    AssertTrue(pipeline.IsBuiltIn, "Pipeline should be built in");
                    AssertTrue(pipeline.Active, "Pipeline should be active");
                    AssertEqual(Constants.DefaultTenantId, pipeline.TenantId, "Pipeline tenant");

                    AssertPipelineStages(
                        pipeline,
                        new List<string> { "Product Manager", "Architect", "Worker", "Usability Engineer", "TestEngineer", "Judge", "Recorder" },
                        "ProductDevelopment");

                    List<PipelineStage> ordered = pipeline.Stages.OrderBy(s => s.Order).ToList();
                    AssertEqual("high", ordered[0].PreferredModel, "Product Manager should prefer high tier");
                    AssertEqual("high", ordered[1].PreferredModel, "Architect should prefer high tier");
                    AssertEqual("high", ordered[3].PreferredModel, "Usability Engineer should prefer high tier");
                    AssertEqual("high", ordered[5].PreferredModel, "Judge should prefer high tier");
                    AssertEqual("Recorder", ordered[6].PersonaName, "The final stage should be the Recorder");
                    AssertEqual("mid", ordered[6].PreferredModel, "The Recorder should run at mid tier so it never competes for high-tier captains");
                }
            });

            await RunTest("Seed reconciles existing product persona and pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona existingPersona = new Persona("Product Manager", "persona.old_product");
                    existingPersona.Description = "Old product persona";
                    existingPersona.IsBuiltIn = false;
                    existingPersona.Active = false;
                    await testDb.Driver.Personas.CreateAsync(existingPersona).ConfigureAwait(false);

                    Pipeline existingPipeline = new Pipeline("ProductDevelopment");
                    existingPipeline.Description = "Old product pipeline";
                    existingPipeline.IsBuiltIn = false;
                    existingPipeline.Active = false;
                    existingPipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    await testDb.Driver.Pipelines.CreateAsync(existingPipeline).ConfigureAwait(false);

                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? productManager = await testDb.Driver.Personas.ReadByNameAsync("Product Manager").ConfigureAwait(false);
                    AssertProductPersona("Product Manager", "persona.product_manager", productManager);
                    AssertContains("whole product", productManager!.Description ?? "", "Product Manager description should be canonical");

                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("ProductDevelopment").ConfigureAwait(false);
                    AssertNotNull(pipeline, "ProductDevelopment pipeline should still exist after reconciliation");
                    AssertContains("Usability Engineer", pipeline!.Description ?? "", "Pipeline description should be canonical");
                    AssertPipelineStages(
                        pipeline,
                        new List<string> { "Product Manager", "Architect", "Worker", "Usability Engineer", "TestEngineer", "Judge", "Recorder" },
                        "ProductDevelopment");
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

                    PersonaSeedService service = NewFleetService(testDb);
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? persona = await testDb.Driver.Personas.ReadByNameAsync("DiagnosticProtocolReviewer").ConfigureAwait(false);
                    AssertNotNull(persona, "Persona should still exist after reconciliation");
                    AssertEqual("persona.diagnostic_protocol_reviewer", persona!.PromptTemplateName, "Persona template should be canonical");
                    AssertContains("protocol", persona.Description ?? "", "Persona description should be canonical");
                    AssertTrue(persona.IsBuiltIn, "Persona should be upgraded to built in");
                    AssertTrue(persona.Active, "Persona should be reactivated");
                    AssertEqual(Constants.DefaultTenantId, persona.TenantId, "Persona should be reconciled to default tenant");

                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("DiagnosticProtocolTested").ConfigureAwait(false);
                    AssertSpecialistPipeline("DiagnosticProtocolTested", "DiagnosticProtocolReviewer", pipeline);
                    AssertContains("DiagnosticProtocolReviewer", pipeline!.Description ?? "", "Pipeline description should be canonical");
                }
            });

            await RunTest("Seed diagnostic protocol reviewer persona uses domain-neutral description", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = NewFleetService(testDb);
                    await service.SeedAsync().ConfigureAwait(false);

                    // Fresh-seed path: the existing "upgrades existing" test only asserts the
                    // description on the reconcile branch. Guard the freshly-seeded description
                    // against a regression back to domain-specific framing by asserting the
                    // domain-neutral vocabulary that replaced it.
                    Persona? persona = await testDb.Driver.Personas.ReadByNameAsync("DiagnosticProtocolReviewer").ConfigureAwait(false);
                    AssertNotNull(persona, "DiagnosticProtocolReviewer persona should be seeded");
                    string description = persona!.Description ?? "";
                    AssertContains("protocol parsing", description, "Description should describe protocol-parsing scope");
                    AssertContains("security-sensitive access", description, "Description should describe security-sensitive access scope");
                    AssertContains("hardware-affecting operations", description, "Description should describe high-risk hardware-affecting scope");
                }
            });

            await RunTest("Seed creates the Recorder persona", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? recorder = await testDb.Driver.Personas.ReadByNameAsync(PersonaCatalog.Recorder).ConfigureAwait(false);
                    AssertNotNull(recorder, "Recorder persona should be seeded");
                    AssertEqual("Recorder", recorder!.Name, "Persona name");
                    AssertEqual("persona.recorder", recorder.PromptTemplateName, "Persona prompt template");
                    AssertTrue(recorder.IsBuiltIn, "Persona should be built in");
                    AssertTrue(recorder.Active, "Persona should be active");
                    AssertEqual(Constants.DefaultTenantId, recorder.TenantId, "Persona tenant");
                }
            });

            await RunTest("Seed creates the Recorded pipeline as Worker then Recorder", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? recorded = await testDb.Driver.Pipelines.ReadByNameAsync("Recorded").ConfigureAwait(false);
                    AssertPipelineStages(recorded, new List<string> { "Worker", "Recorder" }, "Recorded");
                    AssertTrue(recorded!.IsBuiltIn, "Pipeline should be built in");
                    AssertTrue(recorded.Active, "Pipeline should be active");
                }
            });

            await RunTest("Seed leaves an existing pipeline named Recorded exactly as it is", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline operatorPipeline = new Pipeline("Recorded");
                    operatorPipeline.Description = "An operator pipeline that happens to carry this name";
                    operatorPipeline.IsBuiltIn = false;
                    operatorPipeline.Active = false;
                    operatorPipeline.Stages = new List<PipelineStage> { new PipelineStage(1, "Worker") };
                    await testDb.Driver.Pipelines.CreateAsync(operatorPipeline).ConfigureAwait(false);

                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? stored = await testDb.Driver.Pipelines.ReadByNameAsync("Recorded").ConfigureAwait(false);
                    AssertNotNull(stored, "The operator pipeline should still exist");
                    AssertEqual(operatorPipeline.Id, stored!.Id, "The same pipeline record is kept");
                    AssertEqual("An operator pipeline that happens to carry this name", stored.Description ?? "", "Description is untouched");
                    AssertFalse(stored.IsBuiltIn, "Built-in flag is untouched");
                    AssertFalse(stored.Active, "Active flag is untouched");
                    AssertEqual(1, stored.Stages.Count, "Stages are untouched");
                    AssertEqual("Worker", stored.Stages[0].PersonaName, "Stage persona is untouched");
                }
            });

            await RunTest("Seed adds a Recorder stage only to the Recorded and ProductDevelopment pipelines", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? full = await testDb.Driver.Pipelines.ReadByNameAsync("FullPipeline").ConfigureAwait(false);
                    AssertPipelineStages(full, new List<string> { "Architect", "Worker", "TestEngineer", "Judge" }, "FullPipeline");

                    List<Pipeline> pipelines = await testDb.Driver.Pipelines.EnumerateAsync().ConfigureAwait(false);
                    foreach (Pipeline pipeline in pipelines)
                    {
                        if (String.Equals(pipeline.Name, "Recorded", StringComparison.Ordinal)) continue;
                        if (String.Equals(pipeline.Name, "ProductDevelopment", StringComparison.Ordinal)) continue;
                        foreach (PipelineStage stage in pipeline.Stages)
                            AssertFalse(PersonaCatalog.Matches(stage.PersonaName, PersonaCatalog.Recorder),
                                "Only the Recorded and ProductDevelopment pipelines carry a Recorder stage, not " + pipeline.Name);
                    }

                    Pipeline? beforeSecondSeed = await testDb.Driver.Pipelines.ReadByNameAsync("FullPipeline").ConfigureAwait(false);
                    await service.SeedAsync().ConfigureAwait(false);
                    Pipeline? afterSecondSeed = await testDb.Driver.Pipelines.ReadByNameAsync("FullPipeline").ConfigureAwait(false);
                    AssertEqual(beforeSecondSeed!.Id, afterSecondSeed!.Id, "A repeated seed keeps the same pipeline record");
                    AssertEqual(beforeSecondSeed.LastUpdateUtc.ToString("O"), afterSecondSeed.LastUpdateUtc.ToString("O"), "A repeated seed rewrites nothing");
                }
            });

            await RunTest("Seed preserves unrelated custom persona and pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona customPersona = new Persona("CustomSecurityAuditor", "persona.custom_security_auditor");
                    customPersona.TenantId = Constants.DefaultTenantId;
                    customPersona.Description = "Custom project security auditor";
                    customPersona.IsBuiltIn = false;
                    customPersona.Active = false;
                    await testDb.Driver.Personas.CreateAsync(customPersona).ConfigureAwait(false);

                    Pipeline customPipeline = new Pipeline("CustomSecurityReview");
                    customPipeline.TenantId = Constants.DefaultTenantId;
                    customPipeline.Description = "Custom security review workflow";
                    customPipeline.IsBuiltIn = false;
                    customPipeline.Active = false;
                    customPipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker") { PreferredModel = "medium" },
                        new PipelineStage(2, "CustomSecurityAuditor") { IsOptional = true, PreferredModel = "custom-high" }
                    };
                    await testDb.Driver.Pipelines.CreateAsync(customPipeline).ConfigureAwait(false);

                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? resolvedPersona = await testDb.Driver.Personas.ReadByNameAsync("CustomSecurityAuditor").ConfigureAwait(false);
                    AssertNotNull(resolvedPersona, "Custom persona should still exist");
                    AssertEqual("persona.custom_security_auditor", resolvedPersona!.PromptTemplateName, "Custom persona template should be unchanged");
                    AssertEqual("Custom project security auditor", resolvedPersona.Description, "Custom persona description should be unchanged");
                    AssertFalse(resolvedPersona.IsBuiltIn, "Custom persona should not be upgraded to built in");
                    AssertFalse(resolvedPersona.Active, "Custom persona active flag should be unchanged");

                    Pipeline? resolvedPipeline = await testDb.Driver.Pipelines.ReadByNameAsync("CustomSecurityReview").ConfigureAwait(false);
                    AssertNotNull(resolvedPipeline, "Custom pipeline should still exist");
                    AssertEqual("Custom security review workflow", resolvedPipeline!.Description, "Custom pipeline description should be unchanged");
                    AssertFalse(resolvedPipeline.IsBuiltIn, "Custom pipeline should not be upgraded to built in");
                    AssertFalse(resolvedPipeline.Active, "Custom pipeline active flag should be unchanged");
                    AssertEqual(2, resolvedPipeline.Stages.Count, "Custom pipeline stages should be unchanged");

                    List<PipelineStage> ordered = resolvedPipeline.Stages.OrderBy(s => s.Order).ToList();
                    AssertEqual("Worker", ordered[0].PersonaName, "Custom stage 1 persona");
                    AssertEqual("medium", ordered[0].PreferredModel, "Custom stage 1 preferred model");
                    AssertEqual("CustomSecurityAuditor", ordered[1].PersonaName, "Custom stage 2 persona");
                    AssertTrue(ordered[1].IsOptional, "Custom stage 2 optional flag should be unchanged");
                    AssertEqual("custom-high", ordered[1].PreferredModel, "Custom stage 2 preferred model");
                }
            });

            await RunTest("Seed does not create the retired specialist pipelines", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    // Even with the full fleet fixture supplied, the three retired pipelines are no
                    // longer part of it, so seeding must not create them. The kept specialist
                    // pipelines still seed.
                    PersonaSeedService service = NewFleetService(testDb);
                    await service.SeedAsync().ConfigureAwait(false);

                    foreach (string retired in new[] { "FrontendWorkflowTested", "MigrationDataTested", "PerformanceMemoryTested" })
                    {
                        Pipeline? gone = await testDb.Driver.Pipelines.ReadByNameAsync(retired).ConfigureAwait(false);
                        AssertNull(gone, "Retired pipeline must not be seeded: " + retired);
                    }

                    foreach (string kept in new[] { "DiagnosticProtocolTested", "TenantSecurityTested", "ReferencePortingTested" })
                    {
                        Pipeline? present = await testDb.Driver.Pipelines.ReadByNameAsync(kept).ConfigureAwait(false);
                        AssertNotNull(present, "Kept specialist pipeline must still be seeded: " + kept);
                    }
                }
            });

            await RunTest("Seed does not duplicate the ProductDevelopment Recorder stage on a repeat seed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);
                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("ProductDevelopment").ConfigureAwait(false);
                    AssertNotNull(pipeline, "ProductDevelopment pipeline should exist after a repeat seed");
                    AssertEqual(7, pipeline!.Stages.Count, "A repeat seed must not add a second Recorder stage");

                    int recorderStages = 0;
                    foreach (PipelineStage stage in pipeline.Stages)
                        if (PersonaCatalog.Matches(stage.PersonaName, PersonaCatalog.Recorder)) recorderStages++;
                    AssertEqual(1, recorderStages, "ProductDevelopment carries exactly one Recorder stage");

                    PipelineStage last = pipeline.Stages.OrderBy(s => s.Order).ToList()[6];
                    AssertTrue(PersonaCatalog.Matches(last.PersonaName, PersonaCatalog.Recorder), "The last stage is the Recorder");
                    AssertEqual("mid", last.PreferredModel, "The Recorder stays at mid tier after reconciliation");
                }
            });

        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static PersonaSeedService NewFleetService(TestDatabase testDb)
        {
            return new PersonaSeedService(
                testDb.Driver,
                CreateLogging(),
                FleetRoutingSettings.CreateAdditionalPersonas(),
                FleetRoutingSettings.CreateAdditionalPipelines());
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
                { "ReferencePortingTested", "PortingReferenceAnalyst" }
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

        private void AssertProductPersona(string expectedName, string expectedTemplate, Persona? persona)
        {
            AssertNotNull(persona, "Persona should be seeded: " + expectedName);
            AssertEqual(expectedName, persona!.Name, "Persona name");
            AssertEqual(expectedTemplate, persona.PromptTemplateName, "Persona prompt template");
            AssertTrue(persona.IsBuiltIn, "Persona should be built in");
            AssertTrue(persona.Active, "Persona should be active");
            AssertEqual(Constants.DefaultTenantId, persona.TenantId, "Persona tenant");
        }

        private void AssertPipelineStages(Pipeline? pipeline, List<string> expectedPersonas, string pipelineName)
        {
            AssertNotNull(pipeline, "Pipeline should be seeded: " + pipelineName);
            AssertEqual(expectedPersonas.Count, pipeline!.Stages.Count, "Stage count for " + pipelineName);

            List<PipelineStage> ordered = pipeline.Stages.OrderBy(s => s.Order).ToList();
            for (int i = 0; i < expectedPersonas.Count; i++)
            {
                AssertEqual(i + 1, ordered[i].Order, "Stage order for " + pipelineName);
                AssertEqual(expectedPersonas[i], ordered[i].PersonaName, "Stage persona for " + pipelineName);
            }
        }
    }
}
