namespace Armada.Core.Database
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Reads the stored ownership scope of a configuration record on every provider.
    /// </summary>
    public static class OwnershipColumns
    {
        #region Public-Methods

        /// <summary>
        /// Parse a stored ownership scope. A missing value is tenant-wide, which is what the
        /// migration gives every record that predates ownership. An unknown value fails loudly
        /// rather than widening or narrowing visibility by guess.
        /// </summary>
        /// <param name="value">Column value.</param>
        /// <returns>Ownership scope.</returns>
        public static OwnershipScopeEnum ParseScope(object? value)
        {
            if (value == null || value == DBNull.Value) return OwnershipScopeEnum.TenantWide;
            string text = value.ToString() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(text)) return OwnershipScopeEnum.TenantWide;
            if (Enum.TryParse<OwnershipScopeEnum>(text.Trim(), true, out OwnershipScopeEnum parsed)
                && Enum.IsDefined(typeof(OwnershipScopeEnum), parsed))
                return parsed;
            throw new InvalidOperationException("Stored ownership scope '" + text + "' is not a known scope.");
        }

        #endregion
    }
}
