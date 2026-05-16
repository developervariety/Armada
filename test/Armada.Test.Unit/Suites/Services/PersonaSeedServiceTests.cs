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
                        new List<string> { "Product Manager", "Architect", "Worker", "Usability Engineer", "TestEngineer", "Judge" },
                        "ProductDevelopment");

                    List<PipelineStage> ordered = pipeline.Stages.OrderBy(s => s.Order).ToList();
                    AssertEqual("high", ordered[0].PreferredModel, "Product Manager should prefer high tier");
                    AssertEqual("high", ordered[1].PreferredModel, "Architect should prefer high tier");
                    AssertEqual("high", ordered[3].PreferredModel, "Usability Engineer should prefer high tier");
                    AssertEqual("high", ordered[5].PreferredModel, "Judge should prefer high tier");
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
                        new List<string> { "Product Manager", "Architect", "Worker", "Usability Engineer", "TestEngineer", "Judge" },
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

            await RunTest("Seed creates memory consolidator persona", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? persona = await testDb.Driver.Personas.ReadByNameAsync("MemoryConsolidator").ConfigureAwait(false);
                    AssertNotNull(persona, "MemoryConsolidator persona should be seeded");
                    AssertEqual("MemoryConsolidator", persona!.Name, "Persona name");
                    AssertEqual("persona.memory_consolidator", persona.PromptTemplateName, "Persona prompt template");
                    AssertTrue(persona.IsBuiltIn, "Persona should be built in");
                    AssertTrue(persona.Active, "Persona should be active");
                    AssertEqual(Constants.DefaultTenantId, persona.TenantId, "Persona tenant");
                }
            });

            await RunTest("Seed reconciles existing memory consolidator persona", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona existingPersona = new Persona("MemoryConsolidator", "persona.legacy_consolidator");
                    existingPersona.Description = "Legacy consolidator placeholder";
                    existingPersona.IsBuiltIn = false;
                    existingPersona.Active = false;
                    await testDb.Driver.Personas.CreateAsync(existingPersona).ConfigureAwait(false);

                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? persona = await testDb.Driver.Personas.ReadByNameAsync("MemoryConsolidator").ConfigureAwait(false);
                    AssertNotNull(persona, "Persona should still exist after reconciliation");
                    AssertEqual("persona.memory_consolidator", persona!.PromptTemplateName, "Persona template should be canonical after reconcile");
                    AssertContains("learned-facts", persona.Description ?? "", "Persona description should be canonical");
                    AssertTrue(persona.IsBuiltIn, "Persona should be reconciled to built in");
                    AssertTrue(persona.Active, "Persona should be reactivated");
                    AssertEqual(Constants.DefaultTenantId, persona.TenantId, "Persona tenant should be reconciled");
                }
            });

            await RunTest("Seed memory consolidator persona is idempotent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? first = await testDb.Driver.Personas.ReadByNameAsync("MemoryConsolidator").ConfigureAwait(false);
                    AssertNotNull(first, "MemoryConsolidator persona should exist after first seed");

                    await service.SeedAsync().ConfigureAwait(false);

                    Persona? second = await testDb.Driver.Personas.ReadByNameAsync("MemoryConsolidator").ConfigureAwait(false);
                    AssertNotNull(second, "MemoryConsolidator persona should exist after second seed");
                    AssertEqual(first!.Id, second!.Id, "Repeated seeding should update the existing persona instead of replacing it");

                    List<Persona> personas = await testDb.Driver.Personas.EnumerateAsync().ConfigureAwait(false);
                    int matches = personas.Count(p => p.Name == "MemoryConsolidator");
                    AssertEqual(1, matches, "Repeated seeding should not create duplicate MemoryConsolidator personas");
                }
            });

            await RunTest("Seed creates reflections pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("Reflections").ConfigureAwait(false);
                    AssertNotNull(pipeline, "Reflections pipeline should be seeded");
                    AssertEqual("Reflections", pipeline!.Name, "Pipeline name");
                    AssertTrue(pipeline.IsBuiltIn, "Pipeline should be built in");
                    AssertTrue(pipeline.Active, "Pipeline should be active");
                    AssertEqual(Constants.DefaultTenantId, pipeline.TenantId, "Pipeline tenant");
                    AssertEqual(1, pipeline.Stages.Count, "Reflections pipeline should have exactly one stage");

                    PipelineStage stage = pipeline.Stages[0];
                    AssertEqual(1, stage.Order, "Stage order");
                    AssertEqual("MemoryConsolidator", stage.PersonaName, "Stage persona should be MemoryConsolidator");
                    AssertEqual("high", stage.PreferredModel, "Stage preferred model should be high");
                    AssertContains("orchestrator", pipeline.Description ?? "", "Pipeline description should mention orchestrator as reviewer");
                }
            });

            await RunTest("Seed reconciles existing reflections pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline existingPipeline = new Pipeline("Reflections");
                    existingPipeline.Description = "Legacy reflections pipeline";
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

                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("Reflections").ConfigureAwait(false);
                    AssertNotNull(pipeline, "Pipeline should still exist after reconciliation");
                    AssertContains("orchestrator", pipeline!.Description ?? "", "Pipeline description should be canonical");
                    AssertContains("memory consolidation", pipeline.Description ?? "", "Pipeline description should reference memory consolidation");
                    AssertTrue(pipeline.IsBuiltIn, "Pipeline should be upgraded to built in");
                    AssertTrue(pipeline.Active, "Pipeline should be reactivated");
                    AssertEqual(Constants.DefaultTenantId, pipeline.TenantId, "Pipeline tenant should be reconciled");
                    AssertEqual(1, pipeline.Stages.Count, "Pipeline should be reconciled to single stage");
                    AssertEqual("MemoryConsolidator", pipeline.Stages[0].PersonaName, "Stage persona should be MemoryConsolidator after reconcile");
                }
            });

            await RunTest("Seed reflections pipeline is idempotent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? first = await testDb.Driver.Pipelines.ReadByNameAsync("Reflections").ConfigureAwait(false);
                    AssertNotNull(first, "Reflections pipeline should exist after first seed");

                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? second = await testDb.Driver.Pipelines.ReadByNameAsync("Reflections").ConfigureAwait(false);
                    AssertNotNull(second, "Reflections pipeline should exist after second seed");
                    AssertEqual(first!.Id, second!.Id, "Repeated seeding should update the existing pipeline instead of replacing it");

                    List<Pipeline> pipelines = await testDb.Driver.Pipelines.EnumerateAsync().ConfigureAwait(false);
                    int matches = pipelines.Count(p => p.Name == "Reflections");
                    AssertEqual(1, matches, "Repeated seeding should not create duplicate Reflections pipelines");
                }
            });

            await RunTest("Seed reflections pipeline preserves existing built-in pipeline stages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    AssertPipelineStages(
                        await testDb.Driver.Pipelines.ReadByNameAsync("WorkerOnly").ConfigureAwait(false),
                        new List<string> { "Worker" },
                        "WorkerOnly");
                    AssertPipelineStages(
                        await testDb.Driver.Pipelines.ReadByNameAsync("Reviewed").ConfigureAwait(false),
                        new List<string> { "Worker", "Judge" },
                        "Reviewed");
                    AssertPipelineStages(
                        await testDb.Driver.Pipelines.ReadByNameAsync("Tested").ConfigureAwait(false),
                        new List<string> { "Worker", "TestEngineer", "Judge" },
                        "Tested");
                    AssertPipelineStages(
                        await testDb.Driver.Pipelines.ReadByNameAsync("FullPipeline").ConfigureAwait(false),
                        new List<string> { "Architect", "Worker", "TestEngineer", "Judge" },
                        "FullPipeline");
                    AssertPipelineStages(
                        await testDb.Driver.Pipelines.ReadByNameAsync("ProductDevelopment").ConfigureAwait(false),
                        new List<string> { "Product Manager", "Architect", "Worker", "Usability Engineer", "TestEngineer", "Judge" },
                        "ProductDevelopment");

                    Pipeline? reflections = await testDb.Driver.Pipelines.ReadByNameAsync("Reflections").ConfigureAwait(false);
                    AssertNotNull(reflections, "Reflections pipeline should be seeded");
                    AssertPipelineStages(reflections, new List<string> { "MemoryConsolidator" }, "Reflections");
                    AssertContains("No TestEngineer or Judge stage runs", reflections!.Description ?? "", "Reflections description should document reviewer boundary");
                }
            });

            await RunTest("Seed creates ReflectionsDualJudge pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("ReflectionsDualJudge").ConfigureAwait(false);
                    AssertNotNull(pipeline, "ReflectionsDualJudge pipeline should be seeded");
                    AssertEqual("ReflectionsDualJudge", pipeline!.Name, "Pipeline name");
                    AssertTrue(pipeline.IsBuiltIn, "Pipeline should be built in");
                    AssertTrue(pipeline.Active, "Pipeline should be active");
                    AssertEqual(Constants.DefaultTenantId, pipeline.TenantId, "Pipeline tenant");
                    AssertEqual(3, pipeline.Stages.Count, "ReflectionsDualJudge should have three stages");

                    List<PipelineStage> ordered = pipeline.Stages.OrderBy(s => s.Order).ToList();
                    AssertEqual(1, ordered[0].Order, "Stage 1 order");
                    AssertEqual("MemoryConsolidator", ordered[0].PersonaName, "Stage 1 persona");
                    AssertEqual("high", ordered[0].PreferredModel, "Stage 1 preferred model");
                    AssertEqual(2, ordered[1].Order, "Stage 2 order");
                    AssertEqual("Judge", ordered[1].PersonaName, "Stage 2 persona");
                    AssertEqual("high", ordered[1].PreferredModel, "Stage 2 preferred model");
                    AssertEqual(2, ordered[2].Order, "Stage 3 order (sibling Judge)");
                    AssertEqual("Judge", ordered[2].PersonaName, "Stage 3 persona");
                    AssertEqual("high", ordered[2].PreferredModel, "Stage 3 preferred model");
                    AssertContains("dualJudge", pipeline.Description ?? "", "Pipeline description should reference dualJudge");
                }
            });

            await RunTest("Seed reconciles stale ReflectionsDualJudge pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline existingPipeline = new Pipeline("ReflectionsDualJudge");
                    existingPipeline.Description = "Legacy stale dual judge pipeline";
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

                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("ReflectionsDualJudge").ConfigureAwait(false);
                    AssertNotNull(pipeline, "Pipeline should still exist after reconciliation");
                    AssertContains("dualJudge", pipeline!.Description ?? "", "Pipeline description should be canonical after reconcile");
                    AssertTrue(pipeline.IsBuiltIn, "Pipeline should be upgraded to built in");
                    AssertTrue(pipeline.Active, "Pipeline should be reactivated");
                    AssertEqual(Constants.DefaultTenantId, pipeline.TenantId, "Pipeline tenant should be reconciled");
                    AssertEqual(3, pipeline.Stages.Count, "Pipeline should be reconciled to three stages");
                    List<PipelineStage> ordered = pipeline.Stages.OrderBy(s => s.Order).ToList();
                    AssertEqual("MemoryConsolidator", ordered[0].PersonaName, "Stage 1 should be MemoryConsolidator after reconcile");
                    AssertEqual("Judge", ordered[1].PersonaName, "Stage 2 should be Judge after reconcile");
                    AssertEqual("Judge", ordered[2].PersonaName, "Stage 3 should be Judge after reconcile");
                }
            });

            await RunTest("Seed ReflectionsDualJudge pipeline is idempotent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PersonaSeedService service = new PersonaSeedService(testDb.Driver, CreateLogging());
                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? first = await testDb.Driver.Pipelines.ReadByNameAsync("ReflectionsDualJudge").ConfigureAwait(false);
                    AssertNotNull(first, "ReflectionsDualJudge pipeline should exist after first seed");

                    await service.SeedAsync().ConfigureAwait(false);

                    Pipeline? second = await testDb.Driver.Pipelines.ReadByNameAsync("ReflectionsDualJudge").ConfigureAwait(false);
                    AssertNotNull(second, "ReflectionsDualJudge pipeline should exist after second seed");
                    AssertEqual(first!.Id, second!.Id, "Repeated seeding should update the existing pipeline instead of replacing it");

                    List<Pipeline> pipelines = await testDb.Driver.Pipelines.EnumerateAsync().ConfigureAwait(false);
                    int matches = pipelines.Count(p => p.Name == "ReflectionsDualJudge");
                    AssertEqual(1, matches, "Repeated seeding should not create duplicate ReflectionsDualJudge pipelines");
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

        private static Dictionary<string, string> GetSingleStagePipelineStages()
        {
            return new Dictionary<string, string>
            {
                { "Reflections", "MemoryConsolidator" }
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
