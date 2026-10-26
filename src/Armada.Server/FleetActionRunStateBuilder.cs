namespace Armada.Server
{
    using System;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal static class FleetActionRunStateBuilder
    {
        internal static FleetActionRunState Clone(FleetActionRunState state)
        {
            return JsonSerializer.Deserialize<FleetActionRunState>(JsonSerializer.SerializeToUtf8Bytes(state))
                ?? throw new InvalidOperationException("Fleet action state could not be copied.");
        }

        internal static void SetTarget(FleetActionRunTargetState target, FleetActionTargetStatusEnum status, string? reason = null)
        {
            target.Status = status;
            target.Reason = reason;
            target.LastUpdateUtc = DateTime.UtcNow;
            if (status is FleetActionTargetStatusEnum.Cancelled or FleetActionTargetStatusEnum.Completed or FleetActionTargetStatusEnum.Delivered
                or FleetActionTargetStatusEnum.Failed or FleetActionTargetStatusEnum.Skipped
                or FleetActionTargetStatusEnum.Uncertain or FleetActionTargetStatusEnum.WorkProduced)
                target.CompletedUtc = DateTime.UtcNow;
        }

        internal static void Recalculate(FleetActionRunState state)
        {
            bool inFlight = state.TargetStates.Exists(target => target.Status is FleetActionTargetStatusEnum.Dispatching or FleetActionTargetStatusEnum.Running);
            bool pending = state.TargetStates.Exists(target => target.Status == FleetActionTargetStatusEnum.Pending);
            if (state.StartedUtc == null && state.TargetStates.Exists(target => target.Status != FleetActionTargetStatusEnum.Pending))
                state.StartedUtc = DateTime.UtcNow;
            if (state.TargetStates.Exists(target => target.Status == FleetActionTargetStatusEnum.Uncertain) && !inFlight)
            {
                state.Status = FleetActionRunStatusEnum.Uncertain;
                state.CompletedUtc = DateTime.UtcNow;
                return;
            }
            if (inFlight || (pending && state.TargetStates.Exists(target => target.Status != FleetActionTargetStatusEnum.Pending)))
            {
                state.Status = FleetActionRunStatusEnum.Running;
                state.CompletedUtc = null;
                return;
            }
            if (pending)
            {
                state.Status = FleetActionRunStatusEnum.Pending;
                state.CompletedUtc = null;
                return;
            }
            state.CompletedUtc ??= DateTime.UtcNow;
            if (state.TargetStates.TrueForAll(target => target.Status == FleetActionTargetStatusEnum.Cancelled))
                state.Status = FleetActionRunStatusEnum.Cancelled;
            else if (state.TargetStates.Exists(target => target.Status is FleetActionTargetStatusEnum.Failed or FleetActionTargetStatusEnum.Skipped or FleetActionTargetStatusEnum.Cancelled))
            {
                bool success = state.TargetStates.Exists(target => target.Status is FleetActionTargetStatusEnum.Completed
                    or FleetActionTargetStatusEnum.WorkProduced or FleetActionTargetStatusEnum.Delivered);
                state.Status = success ? FleetActionRunStatusEnum.PartiallyCompleted : FleetActionRunStatusEnum.Failed;
            }
            else
                state.Status = FleetActionRunStatusEnum.Completed;
        }
    }
}
