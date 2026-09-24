namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text.Json;
    using Armada.Core.Models;

    internal static class DatabaseAssert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static T NotNull<T>(T? value, string message) where T : class
        {
            if (value == null) throw new Exception(message);
            return value;
        }

        public static void Equal<T>(T expected, T actual, string fieldName)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception(fieldName + " mismatch: expected '" + expected + "' got '" + actual + "'");
            }
        }

        /// <summary>
        /// A stored timestamp reads back as the same instant, tagged UTC. An unspecified kind is
        /// treated as local time by serializers and ToUniversalTime, so it fails even when the digits match.
        /// </summary>
        public static void UtcInstant(DateTime expected, DateTime actual, string fieldName)
        {
            if (actual.Kind != DateTimeKind.Utc)
                throw new Exception(fieldName + " kind mismatch: expected 'Utc' got '" + actual.Kind + "'");
            TimeSpan difference = expected.ToUniversalTime() - actual;
            if (difference.Duration() > TimeSpan.FromMilliseconds(1))
                throw new Exception(fieldName + " mismatch: expected '" + expected.ToUniversalTime().ToString("O") + "' got '" + actual.ToString("O") + "'");
        }

        public static void UtcInstant(DateTime? expected, DateTime? actual, string fieldName)
        {
            if (expected == null || actual == null)
            {
                if (expected != null || actual != null)
                    throw new Exception(fieldName + " mismatch: expected '" + expected + "' got '" + actual + "'");
                return;
            }
            UtcInstant(expected.Value, actual.Value, fieldName);
        }

        /// <summary>
        /// Every public property of a record reads back as written. Timestamps compare as UTC instants and must
        /// carry DateTimeKind.Utc; scalars compare by value; collections and nested objects compare by their
        /// JSON form. A property the provider maps wrongly, or not at all, fails here on every provider.
        /// </summary>
        public static void AllProperties<T>(T expected, T actual, string label, params string[] ignored)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (actual == null) throw new Exception(label + " read back as null");
            foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                if (Array.IndexOf(ignored, property.Name) >= 0) continue;
                object? expectedValue = property.GetValue(expected);
                object? actualValue = property.GetValue(actual);
                string name = label + "." + property.Name;
                Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (type == typeof(DateTime))
                {
                    UtcInstant((DateTime?)expectedValue, (DateTime?)actualValue, name);
                }
                else if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal))
                {
                    if (!Object.Equals(expectedValue, actualValue))
                        throw new Exception(name + " mismatch: expected '" + Describe(expectedValue) + "' got '" + Describe(actualValue) + "'");
                }
                else
                {
                    string expectedJson = JsonSerializer.Serialize(expectedValue);
                    string actualJson = JsonSerializer.Serialize(actualValue);
                    if (!String.Equals(expectedJson, actualJson, StringComparison.Ordinal))
                        throw new Exception(name + " mismatch: expected " + expectedJson + " got " + actualJson);
                }
            }
        }

        private static string Describe(object? value)
        {
            if (value == null) return "<null>";
            if (value is string text && text.Length == 0) return "<empty>";
            return value.ToString() ?? String.Empty;
        }

        public static void HasPrefix(string value, string prefix, string fieldName)
        {
            if (String.IsNullOrEmpty(value) || !value.StartsWith(prefix))
            {
                throw new Exception(fieldName + " should start with '" + prefix + "' but was '" + value + "'");
            }
        }

        public static void EnumerationPage<T>(EnumerationResult<T> result, int expectedPageNumber, int expectedPageSize, long expectedTotalRecords, int expectedTotalPages, int expectedObjectCount)
        {
            Equal(expectedPageNumber, result.PageNumber, "PageNumber");
            Equal(expectedPageSize, result.PageSize, "PageSize");
            Equal(expectedTotalRecords, result.TotalRecords, "TotalRecords");
            Equal(expectedTotalPages, result.TotalPages, "TotalPages");
            Equal(expectedObjectCount, result.Objects.Count, "Objects.Count");
            True(result.TotalMs >= 0, "TotalMs should be >= 0");
        }

        public static void ContainsIds<T>(IEnumerable<T> objects, Func<T, string> idSelector, params string[] expectedIds)
        {
            HashSet<string> actualIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (T obj in objects) actualIds.Add(idSelector(obj));

            foreach (string id in expectedIds)
            {
                if (!actualIds.Contains(id))
                {
                    throw new Exception("Expected ID '" + id + "' not found in result set");
                }
            }
        }
    }
}
