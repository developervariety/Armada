namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Monotonic transition checks for durable fleet action state.</summary>
    internal static partial class FleetActionStorage
    {
        internal static void ValidateTransition(FleetActionRunState current, FleetActionRunState next)
        {
            if (current.Status != next.Status && !CanAdvance(current.Status, next.Status)) throw new ArgumentException("Fleet action run status cannot move backwards or leave a terminal state.", nameof(next));
            Dictionary<string, FleetActionRunTargetState> previous = new Dictionary<string, FleetActionRunTargetState>(StringComparer.Ordinal);
            foreach (FleetActionRunTargetState item in current.TargetStates) previous.Add(item.TargetId, item);
            foreach (FleetActionRunTargetState item in next.TargetStates)
            {
                FleetActionRunTargetState old = previous[item.TargetId];
                if (old.Status != item.Status && !CanAdvance(old.Status, item.Status)) throw new ArgumentException("Fleet action target status cannot move backwards or leave a terminal state.", nameof(next));
                if (old.VoyageId != null && !String.Equals(old.VoyageId, item.VoyageId, StringComparison.Ordinal)) throw new ArgumentException("An associated voyage identifier cannot change.", nameof(next));
                if (old.CancelRequested && !item.CancelRequested) throw new ArgumentException("A cancellation request cannot be cleared.", nameof(next));
            }
        }

        private static bool CanAdvance(FleetActionRunStatusEnum from, FleetActionRunStatusEnum to)
        {
            if (from == FleetActionRunStatusEnum.Pending) return to == FleetActionRunStatusEnum.Running || to == FleetActionRunStatusEnum.Cancelled || to == FleetActionRunStatusEnum.Failed || to == FleetActionRunStatusEnum.Uncertain;
            return from == FleetActionRunStatusEnum.Running && (to == FleetActionRunStatusEnum.Completed || to == FleetActionRunStatusEnum.PartiallyCompleted || to == FleetActionRunStatusEnum.Failed || to == FleetActionRunStatusEnum.Cancelled || to == FleetActionRunStatusEnum.Uncertain);
        }

        private static bool CanAdvance(FleetActionTargetStatusEnum from, FleetActionTargetStatusEnum to)
        {
            if (from == FleetActionTargetStatusEnum.Pending) return to == FleetActionTargetStatusEnum.Dispatching || to == FleetActionTargetStatusEnum.Skipped || to == FleetActionTargetStatusEnum.Cancelled || to == FleetActionTargetStatusEnum.Failed || to == FleetActionTargetStatusEnum.Uncertain;
            if (from == FleetActionTargetStatusEnum.Dispatching) return to == FleetActionTargetStatusEnum.Running || to == FleetActionTargetStatusEnum.WorkProduced || to == FleetActionTargetStatusEnum.Delivered || to == FleetActionTargetStatusEnum.Completed || to == FleetActionTargetStatusEnum.Uncertain || to == FleetActionTargetStatusEnum.Failed || to == FleetActionTargetStatusEnum.Cancelled;
            if (from == FleetActionTargetStatusEnum.Running) return to == FleetActionTargetStatusEnum.WorkProduced || to == FleetActionTargetStatusEnum.Delivered || to == FleetActionTargetStatusEnum.Completed || to == FleetActionTargetStatusEnum.Failed || to == FleetActionTargetStatusEnum.Cancelled || to == FleetActionTargetStatusEnum.Uncertain;
            return from == FleetActionTargetStatusEnum.WorkProduced && (to == FleetActionTargetStatusEnum.Delivered || to == FleetActionTargetStatusEnum.Failed || to == FleetActionTargetStatusEnum.Cancelled || to == FleetActionTargetStatusEnum.Uncertain);
        }
    }
}
