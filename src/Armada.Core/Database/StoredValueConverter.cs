namespace Armada.Core.Database
{
    using System;
    using System.Globalization;

    /// <summary>
    /// Converts one provider's stored column values to model values. Each provider configures one instance
    /// with the representations its schema uses; the per-entity column readers call it and never convert
    /// a stored value themselves, so a conversion rule exists once per provider instead of once per entity.
    /// </summary>
    internal sealed class StoredValueConverter
    {
        #region Internal-Members

        /// <summary>
        /// Provider name used in messages about an unreadable stored value.
        /// </summary>
        internal string Provider { get; }

        #endregion

        #region Private-Members

        private readonly bool _IntegerBooleans;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="provider">Provider name used in messages.</param>
        /// <param name="integerBooleans">True when the provider stores a boolean as an integer where only 1 means true;
        /// false when it stores a native boolean or bit.</param>
        internal StoredValueConverter(string provider, bool integerBooleans)
        {
            if (String.IsNullOrEmpty(provider)) throw new ArgumentNullException(nameof(provider));
            Provider = provider;
            _IntegerBooleans = integerBooleans;
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Convert a non-null stored boolean.
        /// </summary>
        /// <param name="value">Non-null column value.</param>
        /// <returns>The boolean.</returns>
        internal bool ToBool(object value)
        {
            if (value is bool flag) return flag;
            if (_IntegerBooleans) return Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Convert a non-null stored timestamp to a UTC instant. The rule follows the value the driver returns,
        /// because one provider can mix storage types across columns (PostgreSQL uses TEXT, TIMESTAMP and
        /// TIMESTAMPTZ): a native timestamp already holds UTC and is tagged as such, an offset value is converted,
        /// and text is parsed invariantly with an explicit offset honoured and a missing one read as UTC, so the
        /// host time zone never changes the instant.
        /// </summary>
        /// <param name="value">Non-null column value.</param>
        /// <returns>The instant tagged as UTC.</returns>
        internal DateTime ToUtc(object value)
        {
            if (value is DateTime timestamp) return DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
            if (value is DateTimeOffset offset) return offset.UtcDateTime;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
            return DateTime.SpecifyKind(
                DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                DateTimeKind.Utc);
        }

        #endregion
    }
}
