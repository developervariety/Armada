namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Smart Routing settings: Legacy Routing order filtered by account usage. Disabled by default.</summary>
    public sealed class UsageRoutingSettings
    {
        #region Public-Members

        /// <summary>Minimum polling interval in minutes, from 1 through 60.</summary>
        public int RefreshIntervalMinutes { get; set; } = 5;


        /// <summary>Minutes a runtime login status result is reused per account, from 1 through 1440.</summary>
        public int LoginProbeIntervalMinutes { get; set; } = 10;

        /// <summary>Seconds a runtime login status command may run before it counts as timed out, from 1 through 60.</summary>
        public int LoginProbeTimeoutSeconds { get; set; } = 10;

        /// <summary>Enable Smart Routing: the usage filter, persona model groups, the capacity decision, and persona route restrictions.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Accounts with shared captain membership and allowance sources.</summary>
        public List<UsageAccountSettings> Accounts { get; set; } = new List<UsageAccountSettings>();

        /// <summary>
        /// Optional persona restrictions to named accounts and models. A persona with routes (or every persona, through a
        /// <c>*</c> entry) is restricted to them; a persona without routes is unrestricted. Routes never order captains.
        /// </summary>
        public Dictionary<string, List<UsageRouteSettings>> PersonaRoutes { get; set; } = new Dictionary<string, List<UsageRouteSettings>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per-persona model preference: Default, Lighter and Stronger model lists. Persona names match after
        /// normalization. A persona without an entry keeps the Legacy Routing order.
        /// </summary>
        public Dictionary<string, PersonaModelSettings> PersonaModels
        {
            get => _PersonaModels;
            set => _PersonaModels = value ?? new Dictionary<string, PersonaModelSettings>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Informational monthly budget in the configured currency; not a billing cap.</summary>
        public decimal MonthlyBudget { get; set; } = 0;

        /// <summary>Currency shared by the informational budget and account costs.</summary>
        public string Currency { get; set; } = "USD";

        #endregion

        #region Private-Members

        private Dictionary<string, PersonaModelSettings> _PersonaModels = new Dictionary<string, PersonaModelSettings>(StringComparer.OrdinalIgnoreCase);

        #endregion
    }
}
