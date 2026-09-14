namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Bounds for one supervised cutover.
    /// </summary>
    public sealed class SelfDeployCutoverOptions
    {
        /// <summary>
        /// Maximum wait for the other side of the admiral-supervisor handshake.
        /// </summary>
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum wait for the previous admiral to exit before it is terminated by verified identity.
        /// </summary>
        public TimeSpan OldProcessExitTimeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Maximum wait for a terminated process to be confirmed gone.
        /// </summary>
        public TimeSpan TerminationTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Maximum wait for a launched process to prove health.
        /// </summary>
        public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Delay between state polls.
        /// </summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    }
}
