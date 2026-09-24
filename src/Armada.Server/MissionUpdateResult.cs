namespace Armada.Server
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// Outcome of a mission metadata update through <see cref="MissionOperations.UpdateMissionMetadataAsync"/>.
    /// </summary>
    public sealed class MissionUpdateResult
    {
        #region Public-Members

        /// <summary>
        /// Refusal code for an update that names a different vessel or voyage.
        /// </summary>
        public const string BindingImmutableCode = "mission_binding_immutable";

        /// <summary>
        /// Refusal code for a dependency or parent that names no mission visible to the caller.
        /// </summary>
        public const string LinkNotFoundCode = "mission_link_not_found";

        /// <summary>
        /// True when the mission was updated.
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
        /// The stored mission after the update, or the unchanged mission when refused.
        /// </summary>
        public Mission Mission { get; private set; } = null!;

        /// <summary>
        /// True when the refusal is a link that names no visible mission.
        /// </summary>
        public bool LinkNotFound => !Succeeded && String.Equals(Code, LinkNotFoundCode, StringComparison.Ordinal);

        #endregion

        #region Constructors-and-Factories

        private MissionUpdateResult()
        {
        }

        /// <summary>
        /// A completed update.
        /// </summary>
        /// <param name="mission">Stored mission.</param>
        /// <returns>The result.</returns>
        public static MissionUpdateResult Updated(Mission mission)
        {
            return new MissionUpdateResult { Succeeded = true, Mission = mission ?? throw new ArgumentNullException(nameof(mission)) };
        }

        /// <summary>
        /// A refused update. Nothing changed.
        /// </summary>
        /// <param name="mission">Unchanged mission.</param>
        /// <param name="code">Refusal code.</param>
        /// <param name="message">Refusal reason.</param>
        /// <returns>The result.</returns>
        public static MissionUpdateResult Refused(Mission mission, string code, string message)
        {
            return new MissionUpdateResult { Succeeded = false, Mission = mission ?? throw new ArgumentNullException(nameof(mission)), Code = code, Message = message };
        }

        #endregion
    }
}
