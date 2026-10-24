namespace Armada.Core.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Binds a Harbor job to the mission it runs. A bound launch runs only for the runner's enrolled tenant and user:
    /// administrator authority over the owner does not widen it.
    /// </summary>
    public sealed class HarborJobBinding
    {
        #region Public-Members

        /// <summary>Mission the job runs.</summary>
        public string? MissionId { get; set; } = null;

        /// <summary>Captain the job runs for.</summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>Dock that owns the mission attempt.</summary>
        public string? DockId { get; set; } = null;

        /// <summary>Admiral synthetic process identifier bound to the attempt.</summary>
        public int? AdmiralProcessId { get; set; } = null;

        /// <summary>Start time of the Admiral process attempt, in UTC.</summary>
        public DateTime? AdmiralProcessStartedUtc { get; set; } = null;

        /// <summary>Receives the job lifecycle. A bound job's output goes to the observer and is not kept in memory.</summary>
        public IHarborJobObserver? Observer { get; set; } = null;

        /// <summary>Durably bind the mission to the created job before the launch is sent.</summary>
        public Func<HarborJobSnapshot, CancellationToken, Task<bool>>? OnRecorded { get; set; } = null;

        #endregion
    }
}
