namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Opt-in account allowance conservation. Preferences always precede remaining allowance.</summary>
    public sealed class UsageRoutingSettings
    {
        #region Public-Members

        /// <summary>Minimum polling interval in minutes, from 1 through 60.</summary>
        public int RefreshIntervalMinutes { get; set; } = 5;


        /// <summary>Minutes a runtime login status result is reused per account, from 1 through 1440.</summary>
        public int LoginProbeIntervalMinutes { get; set; } = 10;

        /// <summary>Seconds a runtime login status command may run before it counts as timed out, from 1 through 60.</summary>
        public int LoginProbeTimeoutSeconds { get; set; } = 10;

        /// <summary>Enable usage admission and persona route order.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Accounts with shared captain membership and allowance sources.</summary>
        public List<UsageAccountSettings> Accounts { get; set; } = new List<UsageAccountSettings>();

        /// <summary>Ordered approved account/model routes by persona. A missing persona waits unless a * default route exists.</summary>
        public Dictionary<string, List<UsageRouteSettings>> PersonaRoutes { get; set; } = new Dictionary<string, List<UsageRouteSettings>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Informational monthly budget in the configured currency; not a billing cap.</summary>
        public decimal MonthlyBudget { get; set; } = 0;

        /// <summary>Currency shared by the informational budget and account costs.</summary>
        public string Currency { get; set; } = "USD";

        #endregion
    }
}
