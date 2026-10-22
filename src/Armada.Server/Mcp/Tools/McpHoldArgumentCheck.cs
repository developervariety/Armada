namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The argument check the operator hold tools share. Every argument problem is reported in one
    /// refusal, so a caller fixes the whole call at once instead of one argument per retry. A single
    /// problem keeps its own reason code; several report <see cref="InvalidArgumentsReason"/> and list
    /// each code in <c>Problems</c>.
    /// </summary>
    public static class McpHoldArgumentCheck
    {
        #region Public-Members

        /// <summary>Reason code when more than one argument is missing or invalid.</summary>
        public const string InvalidArgumentsReason = "invalid_arguments";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Check the four hold arguments and return the refusal, or null when every argument is usable.
        /// </summary>
        /// <param name="actionValid">Whether the action parsed as one of the tool's actions.</param>
        /// <param name="actionError">The error text for an invalid action, for example "action must be clear or fail".</param>
        /// <param name="missionId">The missionId argument.</param>
        /// <param name="operatorName">The operator argument.</param>
        /// <param name="reason">The reason argument.</param>
        /// <returns>The refusal object, or null.</returns>
        public static object? Check(bool actionValid, string actionError, string? missionId, string? operatorName, string? reason)
        {
            List<string> errors = new List<string>();
            List<string> problems = new List<string>();
            if (!actionValid) { errors.Add(actionError); problems.Add("invalid_action"); }
            if (String.IsNullOrWhiteSpace(missionId)) { errors.Add("missionId is required"); problems.Add("missing_mission_id"); }
            if (String.IsNullOrWhiteSpace(operatorName)) { errors.Add("operator is required"); problems.Add("missing_operator"); }
            if (String.IsNullOrWhiteSpace(reason)) { errors.Add("reason is required"); problems.Add("missing_reason"); }
            if (problems.Count == 0) return null;

            return new
            {
                Error = String.Join("; ", errors) + ".",
                Reason = problems.Count == 1 ? problems[0] : InvalidArgumentsReason,
                Problems = problems
            };
        }

        #endregion
    }
}
