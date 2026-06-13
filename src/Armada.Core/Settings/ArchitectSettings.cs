namespace Armada.Core.Settings
{
    /// <summary>
    /// Settings for Architect captain decomposition and voyage planning.
    /// </summary>
    public class ArchitectSettings
    {
        #region Public-Members

        /// <summary>
        /// Maximum number of missions allowed in a single Architect decomposition before
        /// the parser returns an OverCap verdict. Clamped to [1, 50].
        /// </summary>
        public int MaxMissionsPerVoyage
        {
            get => _MaxMissionsPerVoyage;
            set => _MaxMissionsPerVoyage = Math.Max(1, Math.Min(50, value));
        }

        #endregion

        #region Private-Members

        private int _MaxMissionsPerVoyage = 8;

        #endregion
    }
}
