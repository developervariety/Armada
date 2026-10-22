namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// The answer of <see cref="MissionRestartService.PlanUpstreamFailureReopenAsync"/>: either the refusal, or the
    /// stage whose commit the restarted mission runs on and the upstream stages the restart supersedes.
    /// </summary>
    public sealed class MissionRestartReopenPlan
    {
        #region Public-Members

        /// <summary>
        /// Refusal reason, or null when the voyage may be reopened.
        /// </summary>
        public string? Refusal { get; private set; } = null;

        /// <summary>
        /// The nearest upstream stage that handed off verified work; the restarted mission depends on it.
        /// </summary>
        public Mission? Anchor { get; private set; } = null;

        /// <summary>
        /// The Failed or Cancelled stages between the anchor and the restarted mission.
        /// </summary>
        public List<Mission> Superseded { get; private set; } = new List<Mission>();

        #endregion

        #region Constructors-and-Factories

        private MissionRestartReopenPlan()
        {
        }

        /// <summary>
        /// A refused plan.
        /// </summary>
        /// <param name="reason">Refusal reason.</param>
        /// <returns>The plan.</returns>
        public static MissionRestartReopenPlan Refuse(string reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));
            return new MissionRestartReopenPlan { Refusal = reason };
        }

        /// <summary>
        /// An accepted plan.
        /// </summary>
        /// <param name="anchor">Stage whose commit the restarted mission runs on.</param>
        /// <param name="superseded">Stages the restart supersedes.</param>
        /// <returns>The plan.</returns>
        public static MissionRestartReopenPlan Reopen(Mission anchor, List<Mission> superseded)
        {
            return new MissionRestartReopenPlan
            {
                Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor)),
                Superseded = superseded ?? throw new ArgumentNullException(nameof(superseded))
            };
        }

        #endregion
    }
}
