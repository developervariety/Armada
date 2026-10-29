namespace Inventory
{
    using System;

    /// <summary>
    /// Text helpers.
    /// </summary>
    public static class TextUtil
    {
        /// <summary>
        /// Shorten a value to at most maxLength characters, ending in "..." when it was cut.
        /// </summary>
        /// <param name="value">Value.</param>
        /// <param name="maxLength">Maximum length, at least 3.</param>
        /// <returns>The value, or its shortened form.</returns>
        /// <exception cref="ArgumentNullException">Thrown when value is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when maxLength is less than 3.</exception>
        public static string Truncate(string value, int maxLength)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (maxLength < 3) throw new ArgumentOutOfRangeException(nameof(maxLength));
            if (value.Length <= maxLength) return value;
            return value.Substring(0, maxLength - 3) + "...";
        }
    }
}
