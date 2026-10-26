namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class FleetActionServiceSuite
    {
        private static async Task PipelineHashIgnoresMetadataAndRejectsStageDriftAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                Pipeline pipeline = new Pipeline("Snapshot pipeline")
                {
                    TenantId = fixture.Tenant.Id,
                    UserId = fixture.User.Id,
                    Stages = new List<PipelineStage> { new PipelineStage(1, "Worker") { Description = "Original stage" } }
                };
                pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                string hash = FleetActionSnapshotBuilder.HashPipeline(pipeline);
                pipeline.CreatedUtc = pipeline.CreatedUtc.AddDays(-1);
                pipeline.LastUpdateUtc = pipeline.LastUpdateUtc.AddDays(1);
                pipeline.RunningVoyageCount = 42;
                AssertEqual(hash, FleetActionSnapshotBuilder.HashPipeline(pipeline), "timestamps and observation counts are not pipeline content");

                TestAdmiral admiral = (TestAdmiral)(object)fixture.Admiral;
                admiral.ResolvedPipeline = pipeline;
                Vessel vessel = await CreateVesselAsync(fixture, "Pipeline target").ConfigureAwait(false);
                FleetAction action = await fixture.Service.CreateAsync(fixture.Owner, new FleetActionUpsertRequest
                {
                    Name = "Pipeline action", Active = true, PromptTemplate = "Inspect {vesselName}.", PipelineId = pipeline.Id, Concurrency = 1
                }).ConfigureAwait(false);
                FleetActionRun run = await fixture.Service.StartRunAsync(fixture.Owner, action.Id,
                    new FleetActionRunRequest { RequestId = "request_" + Guid.NewGuid().ToString("N"), VesselIds = new List<string> { vessel.Id } }).ConfigureAwait(false);
                pipeline.Stages[0].Description = "Changed stage content";
                admiral.ResolvedPipeline = pipeline;
                await CreateRunner(fixture).TickAsync().ConfigureAwait(false);
                FleetActionRun refreshed = await fixture.Service.GetRunAsync(fixture.Owner, run.Id).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The pipeline drift run disappeared.");
                AssertEqual(FleetActionTargetStatusEnum.Skipped, refreshed.State.TargetStates[0].Status);
                AssertContains("pipeline changed", refreshed.State.TargetStates[0].Reason ?? String.Empty);
                AssertEqual(0, admiral.DispatchCalls, "changed stage content is refused before the shared dispatcher");
            }
        }
    }
}
