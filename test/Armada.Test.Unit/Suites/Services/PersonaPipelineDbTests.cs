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
