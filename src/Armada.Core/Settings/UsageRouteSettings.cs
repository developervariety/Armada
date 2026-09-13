namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>An approved account and optional model subset for one persona.</summary>
    public sealed class UsageRouteSettings
    {
        #region Public-Members

        /// <summary>Account to try at this position in the preference order.</summary>
        public string AccountId { get; set; } = String.Empty;

        /// <summary>Allowed model subset; empty means all models on this account, subject to mission eligibility.</summary>
        public List<string> Models { get; set; } = new List<string>();

        #endregion
    }
}
