namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for persona and pipeline database CRUD operations including stage management:
    /// persona create/read/read-by-name/update/delete/exists, and pipeline create-with-stages,
    /// update-replaces-stages, delete-cascades-to-stages, and read-by-name-includes-stages. Each
    /// case builds a fresh SQLite store.
    /// </summary>
    public sealed class PersonaPipelineDbSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.PersonaPipelineDb";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Persona and Pipeline Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_and_read_persona", "Create and read persona", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona persona = new Persona("TestWorker", "persona.worker");
                    persona.Description = "A test worker persona";
                    persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    Persona? read = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Persona should be readable by ID");
                    AssertEqual("TestWorker", read!.Name, "Persona name");
                    AssertEqual("persona.worker", read.PromptTemplateName, "Persona prompt template name");
                    AssertEqual("A test worker persona", read.Description, "Persona description");
                }
            }));

            cases.Add(CaseAsync("read_persona_by_name", "Read persona by name", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona persona = new Persona("UniquePersona", "persona.worker");
                    persona.Description = "Unique persona for name lookup";
                    await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    Persona? read = await testDb.Driver.Personas.ReadByNameAsync("UniquePersona").ConfigureAwait(false);
                    AssertNotNull(read, "Persona should be readable by name");
                    AssertEqual("UniquePersona", read!.Name, "Persona name");
                }
            }));

            cases.Add(CaseAsync("update_persona", "Update persona", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona persona = new Persona("UpdateTarget", "persona.worker");
                    persona.Description = "Original description";
                    persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    persona.Description = "Updated description";
                    await testDb.Driver.Personas.UpdateAsync(persona).ConfigureAwait(false);

                    Persona? read = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Updated persona should be readable");
                    AssertEqual("Updated description", read!.Description, "Persona description after update");
                }
            }));

            cases.Add(CaseAsync("delete_persona", "Delete persona", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona persona = new Persona("DeleteTarget", "persona.worker");
                    persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);
                    string personaId = persona.Id;

                    await testDb.Driver.Personas.DeleteAsync(personaId).ConfigureAwait(false);

                    Persona? read = await testDb.Driver.Personas.ReadAsync(personaId).ConfigureAwait(false);
                    AssertNull(read, "Deleted persona should not be found");
                }
            }));

            cases.Add(CaseAsync("exists_by_name_returns_true_for_existing", "ExistsByName returns true for existing", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona persona = new Persona("ExistsCheck", "persona.worker");
                    await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    bool exists = await testDb.Driver.Personas.ExistsByNameAsync("ExistsCheck").ConfigureAwait(false);
                    AssertTrue(exists, "ExistsByName should return true for existing persona");

                    bool notExists = await testDb.Driver.Personas.ExistsByNameAsync("Nonexistent").ConfigureAwait(false);
                    AssertFalse(notExists, "ExistsByName should return false for nonexistent persona");
                }
            }));

            cases.Add(CaseAsync("create_and_read_pipeline_with_stages", "Create and read pipeline with stages", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("ThreeStage");
                    pipeline.Description = "A pipeline with three stages";
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Architect"),
                        new PipelineStage(2, "Worker") { RequiresReview = true },
                        new PipelineStage(3, "Judge") { RequiresReview = true, ReviewDenyAction = Armada.Core.Enums.ReviewDenyActionEnum.FailPipeline }
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Pipeline? read = await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Pipeline should be readable by ID");
                    AssertEqual("ThreeStage", read!.Name, "Pipeline name");
                    AssertEqual(3, read.Stages.Count, "Pipeline should have 3 stages");

                    // Verify stage ordering
                    List<PipelineStage> ordered = read.Stages.OrderBy(s => s.Order).ToList();
                    AssertEqual("Architect", ordered[0].PersonaName, "Stage 1 persona");
                    AssertEqual("Worker", ordered[1].PersonaName, "Stage 2 persona");
                    AssertEqual("Judge", ordered[2].PersonaName, "Stage 3 persona");
                    AssertTrue(ordered[1].RequiresReview, "Stage 2 review gate should persist");
                    AssertEqual(Armada.Core.Enums.ReviewDenyActionEnum.FailPipeline, ordered[2].ReviewDenyAction, "Stage 3 deny action should persist");
                }
            }));

            cases.Add(CaseAsync("update_pipeline_replaces_stages", "Update pipeline replaces stages", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("TwoStage");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Architect"),
                        new PipelineStage(2, "Worker")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    // Update with 3 stages
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Architect"),
                        new PipelineStage(2, "Worker"),
                        new PipelineStage(3, "Judge")
                    };
                    await testDb.Driver.Pipelines.UpdateAsync(pipeline).ConfigureAwait(false);

                    Pipeline? read = await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Updated pipeline should be readable");
                    AssertEqual(3, read!.Stages.Count, "Pipeline should now have 3 stages");
                }
            }));

            cases.Add(CaseAsync("delete_pipeline_cascades_to_stages", "Delete pipeline cascades to stages", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("DeleteCascade");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                    string pipelineId = pipeline.Id;

                    await testDb.Driver.Pipelines.DeleteAsync(pipelineId).ConfigureAwait(false);

                    Pipeline? read = await testDb.Driver.Pipelines.ReadAsync(pipelineId).ConfigureAwait(false);
                    AssertNull(read, "Deleted pipeline should not be found");
                }
            }));

            cases.Add(CaseAsync("pipeline_read_by_name_includes_stages", "Pipeline ReadByName includes stages", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("NameLookup");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Architect"),
                        new PipelineStage(2, "Worker")
                    };
                    await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Pipeline? read = await testDb.Driver.Pipelines.ReadByNameAsync("NameLookup").ConfigureAwait(false);
                    AssertNotNull(read, "Pipeline should be readable by name");
                    AssertEqual("NameLookup", read!.Name, "Pipeline name");
                    AssertEqual(2, read.Stages.Count, "Pipeline read by name should include stages");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Persona and Pipeline Database Operations",
                cases: cases);
        }

        #endregion

        #region Private-Methods

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
