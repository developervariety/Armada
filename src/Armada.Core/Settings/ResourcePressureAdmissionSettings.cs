namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Resource-pressure admission policy applied before a captain is launched.
    /// Combines a host/container memory probe with the active captain/build
    /// pressure count to decide whether a new captain may start now or must be
    /// deferred safely until resource capacity returns. Also tracks a cooldown
    /// window imposed after a kernel OOM (exit 137) classification so retries
    /// only proceed once capacity has recovered.
    /// </summary>
    public sealed class ResourcePressureAdmissionSettings
    {
        /// <summary>
        /// Whether resource-pressure admission gating is active.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Minimum available (host/container) memory, in megabytes, required
        /// before a new captain launch is admitted. Zero disables the memory gate.
        /// </summary>
        public int MinAvailableMemoryMb
        {
            get => _MinAvailableMemoryMb;
            set => _MinAvailableMemoryMb = Math.Max(0, value);
        }

        /// <summary>
        /// Maximum number of concurrent active captain/build workloads allowed
        /// before new launches are deferred. Zero means unlimited.
        /// </summary>
        public int MaxConcurrentBuilds
        {
            get => _MaxConcurrentBuilds;
            set => _MaxConcurrentBuilds = Math.Max(0, Math.Min(1000, value));
        }

        /// <summary>
        /// Seconds that new captain launches are deferred after a kernel OOM
        /// (exit 137) classification. A mission may only retry once the cooldown
        /// has elapsed AND the memory probe reports capacity has returned.
        /// </summary>
        public int OomCooldownSeconds
        {
            get => _OomCooldownSeconds;
            set => _OomCooldownSeconds = Math.Max(1, Math.Min(7200, value));
        }

        /// <summary>
        /// Copy every value from another instance into this one, in place.
        /// The live admission gate is constructed with a reference to this object
        /// (see ArmadaServer wiring), so an update must mutate the existing instance
        /// rather than replace the reference. Replacing it would leave the running
        /// gate reading the old values until the next restart.
        /// </summary>
        /// <param name="source">Instance to copy values from. Null is ignored.</param>
        public void CopyFrom(ResourcePressureAdmissionSettings source)
        {
            if (source == null) return;
            Enabled = source.Enabled;
            MinAvailableMemoryMb = source.MinAvailableMemoryMb;
            MaxConcurrentBuilds = source.MaxConcurrentBuilds;
            OomCooldownSeconds = source.OomCooldownSeconds;
        }

        private int _MinAvailableMemoryMb = 512;
        private int _MaxConcurrentBuilds = 0;
        private int _OomCooldownSeconds = 120;
    }
}