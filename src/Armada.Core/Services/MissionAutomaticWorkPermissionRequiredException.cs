namespace Armada.Core.Services
{
    using System;

    /// <summary>A permission wait, not a failed build, skipped gate or accepted result.</summary>
    public sealed class MissionAutomaticWorkPermissionRequiredException : Exception
    {
        /// <summary>Mission whose current permission could not admit the command.</summary>
        public string MissionId { get; }

        /// <summary>Construct a named admission refusal.</summary>
        public MissionAutomaticWorkPermissionRequiredException(string missionId, string reason) : base(reason)
        {
            MissionId = missionId;
        }
    }
}
