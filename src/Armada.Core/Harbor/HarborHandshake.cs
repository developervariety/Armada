namespace Armada.Core.Harbor
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Harbor-to-server opening message: identifies the Harbor and advertises what it can run, so the
    /// Admiral can register it and route work by capability and capacity.
    /// </summary>
    public class HarborHandshake : HarborMessage
    {
        #region Public-Members

        /// <summary>
        /// Harbor identifier (hbr_ prefix).
        /// </summary>
        public string HarborId { get; set; } = string.Empty;

        /// <summary>
        /// Human-facing Harbor name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Protocol version the Harbor speaks.
        /// </summary>
        public string ProtocolVersion { get; set; } = string.Empty;

        /// <summary>
        /// Operating-system platform (for example "Windows", "Linux", "macOS").
        /// </summary>
        public string? OsPlatform { get; set; } = null;

        /// <summary>
        /// Processor architecture (for example "X64", "Arm64").
        /// </summary>
        public string? Architecture { get; set; } = null;

        /// <summary>
        /// Maximum concurrent jobs the Harbor will accept. Zero or less (including an omitted field) means the Harbor
        /// does not advertise a capacity: the Admiral then applies harbor.defaultMaxJobsPerHarbor to a new
        /// registration and keeps the capacity of an existing one.
        /// </summary>
        public int MaxConcurrentJobs { get; set; } = 0;

        /// <summary>
        /// Advertised capabilities (available runtimes and host tools).
        /// </summary>
        public List<HarborCapability> Capabilities { get; set; } = new List<HarborCapability>();

        /// <summary>
        /// Jobs the Harbor is running as it opens this link (started over an earlier link), or null from a Harbor that
        /// predates the field. The Admiral settles jobs it tracks for this Harbor that the list leaves out (they ended
        /// while the link was down) and answers with <see cref="HarborHandshakeAck.OwnedJobIds"/>.
        /// </summary>
        public List<string>? LiveJobIds { get; set; } = null;

        /// <summary>
        /// The Harbor's heartbeat interval in milliseconds, or null when it does not say (an older Harbor). Zero means the
        /// Harbor sends no heartbeats. The Admiral treats a link it has not heard from for about three intervals (at least
        /// 45 seconds) as dead.
        /// </summary>
        public int? HeartbeatIntervalMs { get; set; } = null;

        /// <summary>
        /// Identifies the Harbor's job outbox (one per Harbor process), or null from a Harbor that predates it. The
        /// <see cref="HarborMessage.OutboxSequence"/> of job messages counts within it; a new epoch (the Harbor restarted)
        /// starts the count again.
        /// </summary>
        public string? JobOutboxEpoch { get; set; } = null;

        #endregion
    }
}
