namespace Inventory
{
    /// <summary>
    /// Codes kept for the legacy import format. This file is UTF-8 with a byte order mark and CRLF line endings.
    /// </summary>
    public static class LegacyCodes
    {
        /// <summary>
        /// Longest code the legacy format accepts.
        /// </summary>
        public const int MaxCodeLength = 8;

        /// <summary>
        /// Prefix of every legacy code.
        /// </summary>
        public const string Prefix = "LG-";
    }
}
