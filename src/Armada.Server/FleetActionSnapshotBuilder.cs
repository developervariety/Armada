namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    internal static class FleetActionSnapshotBuilder
    {
        internal static string Fingerprint(string actionId, List<string> vesselIds)
        {
            byte[] input = JsonSerializer.SerializeToUtf8Bytes(new { ActionId = actionId, VesselIds = vesselIds });
            return Convert.ToHexString(SHA256.HashData(input));
        }

        internal static async Task<FleetActionRun> BuildAsync(
            DatabaseDriver database, IAdmiralService admiral, AuthContext actor,
            FleetAction action, string requestId, List<string> vesselIds, string fingerprint, CancellationToken token)
        {
            FleetActionRun run = new FleetActionRun
            {
                TenantId = actor.TenantId!, UserId = actor.UserId!, ActionId = action.Id,
                RequestFingerprint = fingerprint, RequestId = requestId,
                Snapshot = new FleetActionRunSnapshot
                {
                    ActionName = action.Name, ActionKind = action.Kind, PromptTemplate = action.PromptTemplate,
                    PipelineId = action.PipelineId, Concurrency = action.Concurrency
                },
                State = new FleetActionRunState()
            };
            foreach (string vesselId in vesselIds)
            {
                Vessel? vessel = await CallerScopedRead.ReadVesselAsync(database, actor, vesselId, token).ConfigureAwait(false);
                if (vessel == null) throw new ArgumentException("A selected vessel is not available to this operator.", nameof(vesselIds));
                Pipeline? pipeline = await admiral.ResolvePipelineAsync(action.PipelineId, vessel, token).ConfigureAwait(false);
                if (!String.IsNullOrWhiteSpace(action.PipelineId) && (pipeline == null || pipeline.Id != action.PipelineId))
                    throw new InvalidOperationException("The selected pipeline is not available for a target vessel.");
                FleetActionRunTarget target = new FleetActionRunTarget
                {
                    VesselId = vessel.Id,
                    VesselTenantIdSnapshot = vessel.TenantId ?? Constants.DefaultTenantId,
                    VesselUserIdSnapshot = vessel.UserId ?? Constants.DefaultUserId,
                    VesselNameSnapshot = vessel.Name,
                    RenderedPrompt = Render(action.PromptTemplate, vessel),
                    ResolvedPipelineId = pipeline?.Id,
                    ResolvedPipelineHash = pipeline == null ? null : HashPipeline(pipeline)
                };
                run.Snapshot.Targets.Add(target);
                run.State.TargetStates.Add(new FleetActionRunTargetState { TargetId = target.TargetId });
            }
            byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(run.Snapshot);
            byte[] state = JsonSerializer.SerializeToUtf8Bytes(run.State);
            if (snapshot.Length > 2 * 1024 * 1024 || state.Length > 2 * 1024 * 1024)
                throw new ArgumentException("The saved run snapshot exceeds the 2 MB limit.", nameof(vesselIds));
            return run;
        }

        internal static string HashPipeline(Pipeline pipeline)
        {
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(new
            {
                pipeline.Id,
                pipeline.Name,
                pipeline.Description,
                pipeline.Stages,
                pipeline.Active,
                pipeline.TenantId,
                pipeline.UserId,
                pipeline.OwnershipScope
            });
            return Convert.ToHexString(SHA256.HashData(content));
        }

        private static string Render(string template, Vessel vessel)
        {
            return template.Replace("{vesselId}", vessel.Id, StringComparison.Ordinal)
                .Replace("{vesselName}", vessel.Name, StringComparison.Ordinal);
        }
    }
}
