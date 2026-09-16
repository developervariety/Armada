namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>What deleting one subscription account removed.</summary>
    public sealed class UsageAccountDeleteResult
    {
        #region Public-Members

        /// <summary>Account identifier.</summary>
        public string AccountId { get; set; } = String.Empty;

        /// <summary>Number of persona route entries that named the account and were removed.</summary>
        public int RoutesRemoved { get; set; } = 0;

        /// <summary>Persona keys dropped because their route list became empty.</summary>
        public List<string> PersonasRemoved { get; set; } = new List<string>();

        /// <summary>True when a pending dashboard login for the account was cancelled.</summary>
        public bool LoginCancelled { get; set; } = false;

        /// <summary>True when the server-derived account folder was deleted.</summary>
        public bool HomeDeleted { get; set; } = false;

        /// <summary>
        /// Named folder outcome: <c>account_home_deleted</c>, <c>account_home_not_found</c>,
        /// <c>account_home_not_managed</c> (the account's homeDirectory is not the server-derived folder and was left in
        /// place), or <c>account_home_delete_failed</c>.
        /// </summary>
        public string HomeReason { get; set; } = String.Empty;

        #endregion
    }
}
