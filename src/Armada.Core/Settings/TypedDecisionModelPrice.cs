namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// The provider's price for one typed-decision model, in US dollars per million tokens.
    /// </summary>
    public class TypedDecisionModelPrice
    {
        #region Public-Members

        /// <summary>
        /// Price of one million prompt tokens in US dollars. Negative values are stored as zero.
        /// </summary>
        public double InputUsdPerMillionTokens
        {
            get => _InputUsdPerMillionTokens;
            set => _InputUsdPerMillionTokens = value < 0 ? 0 : value;
        }

        /// <summary>
        /// Price of one million completion tokens in US dollars. Negative values are stored as zero.
        /// </summary>
        public double OutputUsdPerMillionTokens
        {
            get => _OutputUsdPerMillionTokens;
            set => _OutputUsdPerMillionTokens = value < 0 ? 0 : value;
        }

        #endregion

        #region Private-Members

        private double _InputUsdPerMillionTokens = 0;
        private double _OutputUsdPerMillionTokens = 0;

        #endregion
    }
}
