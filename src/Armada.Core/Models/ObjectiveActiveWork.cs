namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Work that already owns an objective. The objective scheduler and autonomous recovery both
    /// read it, so neither starts a second line of work on an objective the other already started.
    /// </summary>
    public class ObjectiveActiveWork
    {
        /// <summary>Kind of the owning work.</summary>
        public ObjectiveActiveWorkKindEnum Kind { get; set; } = ObjectiveActiveWorkKindEnum.ActiveVoyage;

        /// <summary>The active voyage id, or the Challenger mission id for a pending challenge.</summary>
        public string EntityId { get; set; } = String.Empty;

        /// <summary>The failed Judge mission a pending Challenger rules on. Null for an active voyage.</summary>
        public string? FailedMissionId { get; set; } = null;
    }
}
