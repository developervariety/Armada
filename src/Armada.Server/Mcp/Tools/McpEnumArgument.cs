namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Reflection;
    using System.Text.Json;

    /// <summary>
    /// One rule for enum arguments on MCP tools: a caller that sends a value outside the enum gets the
    /// field name and every valid value back, never a bare deserialization exception.
    /// </summary>
    internal static class McpEnumArgument
    {
        #region Public-Methods

        /// <summary>
        /// Every name of <typeparamref name="TEnum"/>, in declaration order.
        /// </summary>
        /// <typeparam name="TEnum">Enum type.</typeparam>
        /// <returns>The valid values.</returns>
        internal static string[] ValidValues<TEnum>() where TEnum : struct, Enum
        {
            return Enum.GetNames<TEnum>();
        }

        /// <summary>
        /// Describe a deserialization failure that names an enum property of <paramref name="requestType"/>.
        /// Returns null when the failure concerns anything else, so the caller keeps its own handling.
        /// </summary>
        /// <param name="tool">Tool name.</param>
        /// <param name="exception">The deserialization failure.</param>
        /// <param name="requestType">The type the arguments were deserialized into.</param>
        /// <returns>The structured result, or null.</returns>
        internal static McpInvalidEnumArgumentResult? TryDescribe(string tool, JsonException exception, Type requestType)
        {
            if (exception == null || requestType == null) return null;

            string? path = exception.Path;
            if (String.IsNullOrWhiteSpace(path) || !path.StartsWith("$.", StringComparison.Ordinal)) return null;

            string field = path.Substring(2);
            int cut = field.IndexOfAny(new[] { '.', '[' });
            if (cut >= 0) field = field.Substring(0, cut);
            if (field.Length == 0) return null;

            PropertyInfo? property = requestType.GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null) return null;

            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!propertyType.IsEnum) return null;

            return new McpInvalidEnumArgumentResult
            {
                Tool = tool,
                Field = field,
                Error = "Invalid value for " + field + ". Use one of the ValidValues returned with this response.",
                ValidValues = Enum.GetNames(propertyType)
            };
        }

        #endregion
    }
}
