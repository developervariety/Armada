namespace Armada.Core.Database
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Shared reader for the typed regression columns on stored Checks.
    /// </summary>
    internal static class CheckRunRegressionColumns
    {
        internal static RegressionPurposeEnum ReadPurpose(object value)
        {
            string? text = ProductionFactSql.ReadText(value);
            if (String.IsNullOrWhiteSpace(text)) return RegressionPurposeEnum.None;
            if (Enum.TryParse(text, false, out RegressionPurposeEnum purpose)) return purpose;
            throw new InvalidOperationException("Stored Check has an unknown regression purpose: " + text);
        }
    }
}
