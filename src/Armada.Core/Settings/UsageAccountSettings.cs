namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>One shared subscription or prepaid allowance account.</summary>
    public sealed class UsageAccountSettings
    {
        #region Public-Members

        /// <summary>Manual, File, Codex, Claude, Cursor, or OpenCodeGo collector. Codex queries the server runtime user's authenticated account.</summary>
        public string Collector { get; set; } = "Manual";

        /// <summary>Environment variable containing a read credential. The value is never stored in settings.</summary>
        public string? CredentialEnv { get; set; }

        /// <summary>Explicit runtime credential file or cookie-header file; never returned file contents.</summary>
        public string? CredentialFilePath { get; set; }

        /// <summary>Exact collector window names mapped to model IDs. Unmapped windows conservatively apply to all models.</summary>
        public Dictionary<string, List<string>> WindowModels { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Captain runtime whose login this account owns: ClaudeCode, Codex, OpenCode, or Cursor. Null keeps the
        /// account usage-only, and every captain launches with the shared login.
        /// </summary>
        public AgentRuntimeEnum? Runtime { get; set; }

        /// <summary>
        /// Absolute login home for ClaudeCode (CLAUDE_CONFIG_DIR), Codex (CODEX_HOME), or OpenCode (XDG_DATA_HOME).
        /// Holds a path only; the owner logs in inside it. Null keeps the shared login.
        /// </summary>
        public string? HomeDirectory { get; set; }

        /// <summary>
        /// Cursor only: the NAME of the server environment variable holding this account's API key, passed to the
        /// captain as CURSOR_API_KEY. The value is read at launch and never stored.
        /// </summary>
        public string? LaunchCredentialEnv { get; set; }

        /// <summary>
        /// Cursor only: absolute path of a file holding this account's API key, passed to the captain as CURSOR_API_KEY.
        /// An alternative to <see cref="LaunchCredentialEnv"/>. The file must be <c>cursor-api-key</c> inside the
        /// account's own folder; the dashboard key login writes it with owner-only permissions. The value is read at
        /// launch and never stored in settings.
        /// </summary>
        public string? LaunchCredentialFile { get; set; }

        /// <summary>Unique operator-defined account identifier.</summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>Captains sharing this account; each captain may belong to only one account.</summary>
        public List<string> CaptainIds { get; set; } = new List<string>();

        /// <summary>Informational recurring cost or prepaid monthly allowance.</summary>
        public decimal MonthlyCost { get; set; } = 0;

        /// <summary>Account workload ceiling, including assignment reservations. Zero disables.</summary>
        public int MaxConcurrentMissions { get; set; } = 0;

        /// <summary>Conserve routine work at or below this remaining percentage.</summary>
        public double LowRemainingPercent { get; set; } = 25;

        /// <summary>Block routine work at or below this remaining percentage.</summary>
        public double ReserveRemainingPercent { get; set; } = 10;

        /// <summary>Resume normal routing when all windows recover to this percentage.</summary>
        public double RecoveryRemainingPercent { get; set; } = 35;

        /// <summary>Release Low conservation shortly before reset. Never releases Reserve or Exhausted.</summary>
        public int ResetGraceMinutes { get; set; } = 0;

        /// <summary>Maximum age of measured allowance; expired windows become unknown.</summary>
        public int MaxAgeMinutes { get; set; } = 15;

        /// <summary>Allow, Conserve, or Block when any required usage window is unknown.</summary>
        public string UnknownUsagePolicy { get; set; } = "Allow";

        /// <summary>Personas allowed to consume conserved and reserved allowance.</summary>
        public List<string> ReservedPersonas { get; set; } = new List<string>();

        /// <summary>Allow missions at this priority or higher importance (lower numeric value).</summary>
        public int? ReservedPriorityAtOrAbove { get; set; }

        /// <summary>Optional bounded JSON snapshot file maintained by an external collector. No commands or credentials.</summary>
        public string? UsageFilePath { get; set; }

        /// <summary>Operator-provided snapshot when no file collector is configured. Timestamps are mandatory.</summary>
        public ProviderUsageSnapshot? ManualSnapshot { get; set; }

        /// <summary>Temporary Normal, Low, Reserve, or Exhausted override.</summary>
        public string? OverrideState { get; set; }

        /// <summary>Required future expiry for an operator override.</summary>
        public DateTime? OverrideUntilUtc { get; set; }

        #endregion
    }
}
