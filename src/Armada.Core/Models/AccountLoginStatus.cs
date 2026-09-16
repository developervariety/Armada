namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>Login status of one account: the last dashboard login plus the server's own login check.</summary>
    public sealed class AccountLoginStatus
    {
        #region Public-Members

        /// <summary>Account identifier.</summary>
        public string AccountId { get; set; } = String.Empty;

        /// <summary>Runtime configured on the saved account, or null.</summary>
        public AgentRuntimeEnum? Runtime { get; set; }

        /// <summary>True when the account is saved in the usage routing policy.</summary>
        public bool Configured { get; set; }

        /// <summary>Server-derived account folder.</summary>
        public string HomeDirectory { get; set; } = String.Empty;

        /// <summary>The last dashboard login for this account since the Admiral started, or null.</summary>
        public AccountLoginSession? Session { get; set; }

        /// <summary>True when the saved account's login check passes. Null when the account is not saved or has no login binding.</summary>
        public bool? LoginReady { get; set; }

        /// <summary>Safe reason from the login check, for example account_login_missing; null when ready.</summary>
        public string? LoginReason { get; set; }

        /// <summary>When the runtime login status probe last finished, or null.</summary>
        public DateTime? LoginCheckedUtc { get; set; }

        #endregion
    }
}
