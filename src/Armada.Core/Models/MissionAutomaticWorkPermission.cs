namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>Server-owned opt-in barrier; absent grants hold their respective automatic actions.</summary>
    public sealed class MissionAutomaticWorkPermission
    {
        /// <summary>Stored format version; unsupported formats cannot authorize work.</summary>
        [JsonRequired]
        public int Version { get; set; } = 1;

        /// <summary>Permission to begin verification, independent of downstream admission.</summary>
        public MissionAutomaticWorkGrant? Verification { get; set; }

        /// <summary>Permission to prepare or admit dependent work, without accepting a manual review.</summary>
        public MissionAutomaticWorkGrant? DependentAdmission { get; set; }
    }
}
