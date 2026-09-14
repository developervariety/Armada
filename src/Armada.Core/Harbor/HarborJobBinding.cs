namespace Armada.Core.Harbor
{
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

        /// <summary>Receives the job lifecycle. A bound job's output goes to the observer and is not kept in memory.</summary>
        public IHarborJobObserver? Observer { get; set; } = null;

        #endregion
    }
}
