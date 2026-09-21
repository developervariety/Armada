namespace Armada.Server
{
    /// <summary>Persisted starting commit returned at dispatch.</summary>
    public sealed class DispatchedMissionStartRef
    {
        /// <summary>Created mission identifier.</summary>
        public string MissionId { get; set; } = string.Empty;

        /// <summary>Resolved commit, or null when the mission uses the default or predecessor branch.</summary>
        public string? StartFromRef { get; set; }
    }
}
