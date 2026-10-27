namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A seeded item and the finding that matched it.
    /// </summary>
    public class BatterySeededMatch
    {
        #region Public-Members

        /// <summary>
        /// Seeded item identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = value ?? String.Empty;
        }

        /// <summary>
        /// The finding (or output line) that matched.
        /// </summary>
        public string? Finding { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Id = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatterySeededMatch()
        {
        }

        #endregion
    }
}
