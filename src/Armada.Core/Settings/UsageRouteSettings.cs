namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// An optional Smart Routing restriction for one persona: its work may run only on captains of the named
    /// account, and only on the listed models when any are listed. Routes restrict; they never order captains.
    /// </summary>
    public sealed class UsageRouteSettings
    {
        #region Public-Members

        /// <summary>Account whose captains may take the persona's work.</summary>
        public string AccountId { get; set; } = String.Empty;

        /// <summary>Allowed model subset; empty means all models on this account, subject to mission eligibility.</summary>
        public List<string> Models { get; set; } = new List<string>();

        #endregion
    }
}
