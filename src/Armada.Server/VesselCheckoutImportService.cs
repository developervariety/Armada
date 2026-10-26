namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Settings;

    /// <summary>Discovers and registers safe, existing local Git checkouts as vessels.</summary>
    public sealed partial class VesselCheckoutImportService
    {
        #region Private-Members

        private static readonly SemaphoreSlim _ImportGate = new SemaphoreSlim(1, 1);
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate the checkout import service.</summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Live application settings.</param>
        public VesselCheckoutImportService(DatabaseDriver database, ArmadaSettings settings)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion
    }
}
