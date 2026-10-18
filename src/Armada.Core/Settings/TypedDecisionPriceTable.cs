namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The price table the typed-decision recorder uses to estimate the cost of each call. Ships empty:
    /// the provider's prices are an operator's configuration, never a product default, so until the table
    /// names a model every call records a null cost with the reason <c>no_price_for_model</c>.
    /// </summary>
    public class TypedDecisionPriceTable
    {
        #region Public-Members

        /// <summary>
        /// Label for this version of the table, recorded on every call event beside the estimate so a
        /// later price change does not silently rewrite what earlier estimates meant.
        /// </summary>
        public string Version
        {
            get => _Version;
            set => _Version = String.IsNullOrWhiteSpace(value) ? "unversioned" : value.Trim();
        }

        /// <summary>
        /// Prices keyed by model. A reported model takes the price of its exact key, else of the longest
        /// key it starts with (so <c>jev-1</c> prices <c>jev-1.13.0</c>); case is ignored.
        /// </summary>
        public Dictionary<string, TypedDecisionModelPrice> Models
        {
            get => _Models;
            set => _Models = value ?? new Dictionary<string, TypedDecisionModelPrice>(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Private-Members

        private string _Version = "unversioned";
        private Dictionary<string, TypedDecisionModelPrice> _Models = new Dictionary<string, TypedDecisionModelPrice>(StringComparer.OrdinalIgnoreCase);

        #endregion
    }
}
