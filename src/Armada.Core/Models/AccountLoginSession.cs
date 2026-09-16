namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Safe view of one account login. It carries only the verification URL and user code the runtime printed; no
    /// other process output, and never a submitted key or pasted code.
    /// </summary>
    public sealed class AccountLoginSession
    {
        #region Public-Members

        /// <summary>Login session identifier.</summary>
        public string SessionId { get; set; } = String.Empty;

        /// <summary>Account the login belongs to.</summary>
        public string AccountId { get; set; } = String.Empty;

        /// <summary>Runtime being logged in.</summary>
        public AgentRuntimeEnum Runtime { get; set; }

        /// <summary>How the login is completed.</summary>
        public AccountLoginMethodEnum Method { get; set; }

        /// <summary>Current state.</summary>
        public AccountLoginStateEnum State { get; set; } = AccountLoginStateEnum.Pending;

        /// <summary>Safe reason code when the login did not succeed; null otherwise.</summary>
        public string? Reason { get; set; }

        /// <summary>Verification or sign-in URL on the provider's own domain, once printed.</summary>
        public string? VerificationUrl { get; set; }

        /// <summary>Device user code to confirm in the browser, once printed.</summary>
        public string? UserCode { get; set; }

        /// <summary>True while the login waits for the owner to paste a code back.</summary>
        public bool NeedsCode { get; set; }

        /// <summary>True when an existing pending login was returned instead of starting a new one.</summary>
        public bool Reused { get; set; }

        /// <summary>When the login started.</summary>
        public DateTime StartedUtc { get; set; }

        /// <summary>When a pending login expires.</summary>
        public DateTime? ExpiresUtc { get; set; }

        /// <summary>When the login reached a final state.</summary>
        public DateTime? CompletedUtc { get; set; }

        #endregion
    }
}
