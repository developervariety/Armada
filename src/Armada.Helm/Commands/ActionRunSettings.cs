namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using Spectre.Console.Cli;

    /// <summary>
    /// Settings for the action run command.
    /// </summary>
    public class ActionRunSettings : BaseSettings
    {
        /// <summary>
        /// Fleet action ID or name.
        /// </summary>
        [Description("Fleet action ID or name")]
        [CommandArgument(0, "<action>")]
        public string Action { get; set; } = string.Empty;

        /// <summary>
        /// Target vessels (repeatable), by ID or name.
        /// </summary>
        [Description("Target vessel ID or name (repeatable)")]
        [CommandOption("--vessel|-v")]
        public string[]? Vessels { get; set; }

        /// <summary>
        /// Target every vessel in a fleet.
        /// </summary>
        [Description("Target every vessel in this fleet (ID)")]
        [CommandOption("--fleet|-f")]
        public string? Fleet { get; set; }

        /// <summary>
        /// Concurrency for this run.
        /// </summary>
        [Description("Concurrency for this run (1-32)")]
        [CommandOption("--concurrency|-c")]
        public int? Concurrency { get; set; }
        /// <summary>Approve tool prompts and human mission reviews for this run.</summary>
        [Description("Approve all tool prompts and mission reviews for this run (admin only)")]
        [CommandOption("--approve-all")]
        public bool ApproveAll { get; set; }

        /// <summary>Per-run approval countdown.</summary>
        [Description("Approval countdown in seconds (10-3600)")]
        [CommandOption("--approval-timeout")]
        public int? ApprovalTimeoutSeconds { get; set; }

        /// <summary>Decision applied when the countdown expires.</summary>
        [Description("Default answer on timeout: Deny or Approve (Approve requires admin)")]
        [CommandOption("--approval-default")]
        public string? ApprovalDefault { get; set; }
    }
}
