namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One probe's observation within a <see cref="ModelProbeRun"/>.
    /// </summary>
    public class ModelProbeResult
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier (mpo_ prefix).
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = String.IsNullOrEmpty(value) ? throw new ArgumentNullException(nameof(Id)) : value;
        }

        /// <summary>
        /// Run (mpr_ prefix).
        /// </summary>
        public string RunId { get; set; } = "";

        /// <summary>
        /// Tenant of the run.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Probe.
        /// </summary>
        public ModelProbeKindEnum Probe { get; set; } = ModelProbeKindEnum.ServedContext;

        /// <summary>
        /// Outcome.
        /// </summary>
        public ModelProbeOutcomeEnum Outcome { get; set; } = ModelProbeOutcomeEnum.CouldNotRun;

        /// <summary>
        /// One-line plain-text observation (for example "2 of 3 compliant; repair needed for 1").
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// Why the probe could not run, or null.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>
        /// Model calls the probe made.
        /// </summary>
        public int Attempts { get; set; } = 0;

        /// <summary>
        /// Estimated tokens the probe used.
        /// </summary>
        public long TokensUsed { get; set; } = 0;

        /// <summary>
        /// Wall-clock duration of the probe in milliseconds.
        /// </summary>
        public long DurationMs { get; set; } = 0;

        /// <summary>
        /// The probe's measured values. Never null.
        /// </summary>
        public ModelProbeMetrics Metrics
        {
            get => _Metrics;
            set => _Metrics = value ?? new ModelProbeMetrics();
        }

        /// <summary>
        /// Start of the model's last answer (at most 2000 characters), for diagnosis, or null.
        /// </summary>
        public string? OutputExcerpt { get; set; } = null;

        /// <summary>
        /// When the result was recorded.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.ModelProbeResultIdPrefix, 24);
        private ModelProbeMetrics _Metrics = new ModelProbeMetrics();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ModelProbeResult()
        {
        }

        #endregion
    }
}
