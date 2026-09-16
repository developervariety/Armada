namespace Armada.Core.Models
{
    using System;

    /// <summary>The server-derived folder and launch references for an account login.</summary>
    public sealed class AccountLoginHomeResult
    {
        #region Public-Members

        /// <summary>Account identifier.</summary>
        public string AccountId { get; set; } = String.Empty;

        /// <summary>Account folder, created with owner-only permissions. Use as homeDirectory for ClaudeCode, Codex, and OpenCode.</summary>
        public string HomeDirectory { get; set; } = String.Empty;

        /// <summary>Cursor key file inside the folder. Use as launchCredentialFile for Cursor.</summary>
        public string CursorKeyFile { get; set; } = String.Empty;

        /// <summary>True when this call created the folder.</summary>
        public bool Created { get; set; }

        #endregion
    }
}
