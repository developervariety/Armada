namespace Armada.Server
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// The one definition of the events an operator's dock, event-log, merge-purge and captain batch actions write.
    /// REST and MCP both raise them through this class after the shared service has made the change, so the event type,
    /// message and entity are the same whichever surface the operator used.
    /// </summary>
    public static class AdministrativeEvents
    {
        #region Public-Methods

        /// <summary>A dock was deleted through the guarded delete.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="dockId">Dock identifier.</param>
        public static Task DockDeletedAsync(OperationNotifier? notifier, string dockId)
            => Sink(notifier).EmitAsync("dock.deleted", "Dock " + dockId + " deleted", "dock", dockId);

        /// <summary>A dock was force purged.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="dockId">Dock identifier.</param>
        public static Task DockPurgedAsync(OperationNotifier? notifier, string dockId)
            => Sink(notifier).EmitAsync("dock.purged", "Dock " + dockId + " force purged", "dock", dockId);

        /// <summary>A dock's worktree was repaired.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="dockId">Dock identifier.</param>
        public static Task DockRepairedAsync(OperationNotifier? notifier, string dockId)
            => Sink(notifier).EmitAsync("dock.repaired", "Dock " + dockId + " worktree repaired", "dock", dockId);

        /// <summary>A dock was unstuck.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="dockId">Dock identifier.</param>
        public static Task DockUnstuckAsync(OperationNotifier? notifier, string dockId)
            => Sink(notifier).EmitAsync("dock.unstuck", "Dock " + dockId + " unstuck (held captain released, worktree reclaimed)", "dock", dockId);

        /// <summary>A batch of docks was deleted.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="deleted">Docks deleted.</param>
        public static Task DocksBatchDeletedAsync(OperationNotifier? notifier, int deleted)
            => Sink(notifier).EmitAsync("dock.batch_deleted", "Batch deleted " + deleted + " docks", "dock");

        /// <summary>An event was deleted.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="eventId">Deleted event identifier.</param>
        public static Task EventDeletedAsync(OperationNotifier? notifier, string eventId)
            => Sink(notifier).EmitAsync("event.deleted", "Deleted event " + eventId, "event", eventId);

        /// <summary>A batch of events was deleted.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="deleted">Events deleted.</param>
        public static Task EventsBatchDeletedAsync(OperationNotifier? notifier, int deleted)
            => Sink(notifier).EmitAsync("event.batch_deleted", "Batch deleted " + deleted + " events", "event");

        /// <summary>A finished merge entry was purged.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="entryId">Merge entry identifier.</param>
        public static Task MergePurgedAsync(OperationNotifier? notifier, string entryId)
            => Sink(notifier).EmitAsync("merge.purged", "Merge entry " + entryId + " purged", "merge_entry", entryId);

        /// <summary>A batch of finished merge entries was purged.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="purged">Entries purged.</param>
        public static Task MergesBatchPurgedAsync(OperationNotifier? notifier, int purged)
            => Sink(notifier).EmitAsync("merge.batch_purged", "Batch purged " + purged + " merge entries", "merge_entry");

        /// <summary>A batch of captains was deleted.</summary>
        /// <param name="notifier">Event sink; null writes nothing.</param>
        /// <param name="deleted">Captains deleted.</param>
        public static Task CaptainsBatchDeletedAsync(OperationNotifier? notifier, int deleted)
            => Sink(notifier).EmitAsync("captain.batch_deleted", "Batch deleted " + deleted + " captains", "captain");

        #endregion

        #region Private-Methods

        private static OperationNotifier Sink(OperationNotifier? notifier)
        {
            return notifier ?? OperationNotifier.None;
        }

        #endregion
    }
}
