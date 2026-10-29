namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Serializes the list columns of a handoff artifact (findings, criteria, actions, check results, commits, protocol
    /// lines) the same way on every database provider. An unreadable column reads as an empty list.
    /// </summary>
    public static class HandoffArtifactJson
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Serialize a list.
        /// </summary>
        /// <typeparam name="T">Item type.</typeparam>
        /// <param name="items">Items, or null.</param>
        /// <returns>JSON array text.</returns>
        public static string Write<T>(List<T>? items)
        {
            return JsonSerializer.Serialize(items ?? new List<T>(), _Options);
        }

        /// <summary>
        /// Deserialize a list; null, empty or invalid text gives an empty list.
        /// </summary>
        /// <typeparam name="T">Item type.</typeparam>
        /// <param name="json">JSON array text, or null.</param>
        /// <returns>The items.</returns>
        public static List<T> Read<T>(string? json)
        {
            if (String.IsNullOrWhiteSpace(json)) return new List<T>();
            try
            {
                return JsonSerializer.Deserialize<List<T>>(json!, _Options) ?? new List<T>();
            }
            catch (JsonException)
            {
                return new List<T>();
            }
        }

        #endregion
    }
}
