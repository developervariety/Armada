namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Outcome of cancelling one mission.
    /// </summary>
    public sealed class MissionCancellationResult
    {
        #region Public-Members

        /// <summary>
        /// True when the mission was written Cancelled.
        /// </summary>
        public bool Succeeded { get; private set; }

        /// <summary>
        /// Refusal code when the cancel was refused; null on success.
        /// </summary>
        public string? Code { get; private set; }

        /// <summary>
        /// Refusal reason when the cancel was refused; null on success.
        /// </summary>
        public string? Message { get; private set; }

        /// <summary>
        /// The stored mission after the cancel, or the unchanged mission when refused.
        /// </summary>
        public Mission Mission { get; private set; } = null!;

        /// <summary>
        /// Stages waiting on the mission that were cancelled with it.
        /// </summary>
        public List<Mission> CancelledDependents { get; private set; } = new List<Mission>();

        /// <summary>
        /// The captain recalled to stop its agent process, or null when none was working the mission.
        /// </summary>
        public string? RecalledCaptainId { get; private set; }

        #endregion

        #region Constructors-and-Factories

        private MissionCancellationResult()
        {
        }

        /// <summary>
        /// A successful cancel.
        /// </summary>
        /// <param name="mission">Stored mission.</param>
        /// <param name="dependents">Dependents cancelled with it.</param>
        /// <param name="recalledCaptainId">Recalled captain, or null.</param>
        /// <returns>The result.</returns>
        public static MissionCancellationResult Cancelled(Mission mission, List<Mission> dependents, string? recalledCaptainId)
        {
            return new MissionCancellationResult
            {
                Succeeded = true,
                Mission = mission ?? throw new ArgumentNullException(nameof(mission)),
                CancelledDependents = dependents ?? new List<Mission>(),
                RecalledCaptainId = recalledCaptainId
            };
        }

        /// <summary>
        /// A refused cancel. Nothing changed.
        /// </summary>
        /// <param name="mission">Unchanged mission.</param>
        /// <param name="code">Refusal code.</param>
        /// <param name="message">Refusal reason.</param>
        /// <returns>The result.</returns>
        public static MissionCancellationResult Refused(Mission mission, string code, string message)
        {
            return new MissionCancellationResult
            {
                Succeeded = false,
                Mission = mission ?? throw new ArgumentNullException(nameof(mission)),
                Code = code,
                Message = message
            };
        }

        #endregion
    }
}
