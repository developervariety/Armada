namespace Armada.Core.Database
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Writes the token-usage input-bucket and counting-rule columns the same way on every provider. The shared
    /// column reader reads them back; a null rule column is a row stored before the column existed.
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
        /// Value to bind for a nullable bucket column.
        /// </summary>
        /// <param name="value">Bucket count.</param>
        /// <returns>The count, or DBNull when it is absent.</returns>
        internal static object ToColumn(long? value)
        {
            return value.HasValue ? value.Value : DBNull.Value;
        }
    }
}
