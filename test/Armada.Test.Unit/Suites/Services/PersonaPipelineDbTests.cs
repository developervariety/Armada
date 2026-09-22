namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for persona and pipeline database CRUD operations including stage management.
    /// </summary>
    public class PersonaPipelineDbTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Persona and Pipeline Database Operations";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Create and read persona", async () =>
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
            });

            await RunTest("Read persona by name", async () =>
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
            });

            await RunTest("Update persona", async () =>
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
            });

            await RunTest("Delete persona", async () =>
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
            });

            await RunTest("ExistsByName returns true for existing", async () =>
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
            });

            await RunTest("Create and read pipeline with stages", async () =>
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
            });

            await RunTest("Update pipeline replaces stages", async () =>
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
            });

            await RunTest("Delete pipeline cascades to stages", async () =>
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
            });

            // A pipeline update rewrites the parent row, deletes the stages and inserts the new ones. A
            // failure part way through must leave the stored pipeline exactly as it was.

            await RunTest("Failed pipeline update leaves the stored pipeline unchanged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("AtomicUpdate");
                    pipeline.Description = "original description";
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Pipeline changed = (await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false))!;
                    changed.Description = "changed description";
                    // The second stage reuses the first stage's id, so its insert fails on the primary key.
                    PipelineStage architect = new PipelineStage(1, "Architect");
                    changed.Stages = new List<PipelineStage>
                    {
                        architect,
                        new PipelineStage(2, "Worker") { Id = architect.Id }
                    };
                    await AssertThrowsAsync<Exception>(
                        () => testDb.Driver.Pipelines.UpdateAsync(changed),
                        "An update whose stage insert fails must throw").ConfigureAwait(false);

                    Pipeline? stored = await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(stored, "The pipeline survives a failed update");
                    AssertEqual("original description", stored!.Description, "The parent row is not rewritten by a failed update");
                    AssertEqual("Worker|Judge", String.Join("|", stored.Stages.Select(stage => stage.PersonaName)), "The stages are not replaced by a failed update");
                }
            });

            await RunTest("Failed pipeline delete leaves the pipeline and its stages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("AtomicDelete");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    // Refuse the parent-row delete so the failure lands after the stages were deleted.
                    using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
                    {
                        connection.Open();
                        using (SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "CREATE TRIGGER block_pipeline_delete BEFORE DELETE ON pipelines WHEN OLD.id = '"
                                + pipeline.Id + "' BEGIN SELECT RAISE(ABORT, 'pipeline delete blocked'); END;";
                            command.ExecuteNonQuery();
                        }
                    }

                    await AssertThrowsAsync<Exception>(
                        () => testDb.Driver.Pipelines.DeleteAsync(pipeline.Id),
                        "A delete whose parent delete fails must throw").ConfigureAwait(false);

                    Pipeline? stored = await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(stored, "The pipeline survives a failed delete");
                    AssertEqual(2, stored!.Stages.Count, "The stages survive a failed delete");
                }
            });

            await RunTest("Pipeline ReadByName includes stages", async () =>
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
            });

            await RunTest("Create and read single-stage pipeline with preferred model", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("Analysis");
                    pipeline.Description = "Single-stage analysis pipeline";
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Analyst") { PreferredModel = "high" }
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Pipeline? read = await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Pipeline should be readable by ID");
                    AssertEqual("Analysis", read!.Name, "Pipeline name");
                    AssertEqual(1, read.Stages.Count, "Pipeline should have 1 stage");

                    PipelineStage stage = read.Stages[0];
                    AssertEqual(1, stage.Order, "Stage order");
                    AssertEqual("Analyst", stage.PersonaName, "Stage persona");
                    AssertEqual("high", stage.PreferredModel, "Stage preferred model");
                }
            });
        }
    }
}
