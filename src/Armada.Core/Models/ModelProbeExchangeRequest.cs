namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One model call a probe makes through the captain's runtime.
    /// </summary>
    public class ModelProbeExchangeRequest
    {
        #region Public-Members

        /// <summary>
        /// Captain whose runtime runs the prompt (for a model endpoint run, a transient API endpoint captain).
        /// </summary>
        public Captain Captain
        {
            get => _Captain;
            set => _Captain = value ?? throw new ArgumentNullException(nameof(Captain));
        }

        /// <summary>
        /// The captain's model endpoint, or null.
        /// </summary>
        public ModelEndpoint? ModelEndpoint { get; set; } = null;

        /// <summary>
        /// Run the exchange belongs to.
        /// </summary>
        public string RunId { get; set; } = "";

        /// <summary>
        /// Short label for logs and the Harbor's job list (for example "Output contract (Judge)").
        /// </summary>
        public string Label { get; set; } = "";

        /// <summary>
        /// Prompt.
        /// </summary>
        public string Prompt { get; set; } = "";

        /// <summary>
        /// Tenant of the run.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User of the run.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Longest time the exchange may take, in milliseconds.
        /// </summary>
        public int TimeoutMs { get; set; } = 600000;

        #endregion

        #region Private-Members

        private Captain _Captain = new Captain();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ModelProbeExchangeRequest()
        {
        }

        #endregion
    }
}
