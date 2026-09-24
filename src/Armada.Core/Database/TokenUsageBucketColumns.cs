namespace Armada.Core.Database
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Reads and writes the token-usage input-bucket and counting-rule columns the same way on every provider. A null
    /// rule column is a row stored before the column existed, and it reads as <see cref="TokenUsageRuleEnum.Legacy"/>.
    /// </summary>
    internal static class TokenUsageBucketColumns
    {
        /// <summary>
        /// The value stored in the rule column.
        /// </summary>
        /// <param name="rule">Counting rule.</param>
        /// <returns>The rule name.</returns>
        internal static string ToColumn(TokenUsageRuleEnum rule)
        {
            return rule.ToString();
        }

        /// <summary>
        /// Read the rule column; a null or unknown value reads as the legacy rule.
        /// </summary>
        /// <param name="value">Column value.</param>
        /// <returns>The counting rule.</returns>
        internal static TokenUsageRuleEnum RuleFromColumn(object? value)
        {
            if (value == null || value == DBNull.Value) return TokenUsageRuleEnum.Legacy;
            return Enum.TryParse(value.ToString(), true, out TokenUsageRuleEnum rule) ? rule : TokenUsageRuleEnum.Legacy;
        }

        /// <summary>
        /// Value to bind for a nullable bucket column.
        /// </summary>
        /// <param name="value">Bucket count.</param>
        /// <returns>The count, or DBNull when it is absent.</returns>
        internal static object ToColumn(long? value)
        {
            return value.HasValue ? value.Value : DBNull.Value;
        }

        /// <summary>
        /// Read a nullable bucket column.
        /// </summary>
        /// <param name="value">Column value.</param>
        /// <returns>The count, or null.</returns>
        internal static long? CountFromColumn(object? value)
        {
            if (value == null || value == DBNull.Value) return null;
            return Convert.ToInt64(value);
        }
    }
}
