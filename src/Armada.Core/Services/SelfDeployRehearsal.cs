namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Gate for the isolated self-deploy rehearsal. A rehearsal runs the real cutover against a disposable
    /// data directory and database copy, so it is refused unless the process was started with the explicit
    /// rehearsal value and a data directory override.
    /// </summary>
    public static class SelfDeployRehearsal
    {
        /// <summary>
        /// Environment variable that must carry <see cref="GateValue"/>.
        /// </summary>
        public const string GateVariable = "ARMADA_SELF_DEPLOY_REHEARSAL";

        /// <summary>
        /// Value that authorizes a rehearsal.
        /// </summary>
        public const string GateValue = "isolated-disposable";

        /// <summary>
        /// Optional seconds the supervisor holds after recording a launched candidate, so an interruption can be
        /// injected in that window. Honoured only for an authorized rehearsal.
        /// </summary>
        public const string HoldVariable = "ARMADA_SELF_DEPLOY_REHEARSAL_HOLD_SECONDS";

        /// <summary>
        /// Server argument that runs a rehearsal cutover for a prebuilt candidate assembly.
        /// </summary>
        public const string RehearseArgument = "--self-deploy-rehearse";

        /// <summary>
        /// Whether this process is an authorized rehearsal.
        /// </summary>
        /// <returns>True only with the gate value and a data directory override.</returns>
        public static bool IsAuthorized()
        {
            return String.Equals(Environment.GetEnvironmentVariable(GateVariable), GateValue, StringComparison.Ordinal)
                && !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Constants.DataDirectoryOverrideVariable));
        }

        /// <summary>
        /// Supervisor hold after a recorded candidate launch.
        /// </summary>
        /// <returns>Zero unless an authorized rehearsal sets a value; at most ten minutes.</returns>
        public static TimeSpan HoldAfterCandidateLaunch()
        {
            if (!IsAuthorized()) return TimeSpan.Zero;
            string? value = Environment.GetEnvironmentVariable(HoldVariable);
            if (String.IsNullOrWhiteSpace(value) || !Int32.TryParse(value, out int seconds)) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 600));
        }
    }
}
