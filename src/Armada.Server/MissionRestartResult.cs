namespace Armada.Server
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// Outcome of an operator restart of one mission.
    /// </summary>
    public sealed class MissionRestartResult
    {
        #region Public-Members

        /// <summary>
        /// True when the mission was returned to Pending.
        /// </summary>
        public bool Succeeded { get; private set; }

        /// <summary>
        /// Refusal code when refused; null on success.
        /// </summary>
        public string? Code { get; private set; }

        /// <summary>
        /// Refusal reason when refused; null on success.
        /// </summary>
        public string? Message { get; private set; }

        /// <summary>
        /// The restarted mission, or the unchanged mission when refused.
        /// </summary>
        public Mission Mission { get; private set; } = null!;

        #endregion

        #region Constructors-and-Factories

        private MissionRestartResult()
        {
        }

        /// <summary>
        /// A completed restart.
        /// </summary>
        /// <param name="mission">Restarted mission.</param>
        /// <returns>The result.</returns>
        public static MissionRestartResult Restarted(Mission mission)
        {
            return new MissionRestartResult { Succeeded = true, Mission = mission ?? throw new ArgumentNullException(nameof(mission)) };
        }

        /// <summary>
        /// A refused restart. Nothing changed.
        /// </summary>
        /// <param name="mission">Unchanged mission.</param>
        /// <param name="code">Refusal code.</param>
        /// <param name="message">Refusal reason.</param>
        /// <returns>The result.</returns>
        public static MissionRestartResult Refused(Mission mission, string code, string message)
        {
            return new MissionRestartResult { Succeeded = false, Mission = mission ?? throw new ArgumentNullException(nameof(mission)), Code = code, Message = message };
        }

        #endregion
    }
}
