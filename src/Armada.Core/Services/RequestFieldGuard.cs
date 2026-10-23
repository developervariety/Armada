namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json;

    /// <summary>
    /// Names the top-level fields of a write request body that the request type does not accept. A deserializer
    /// drops such a field without a word, so a caller that misspells a field, or sends one the request has never
    /// had, reads a success for a write that stored nothing. A write path refuses the body and lists the fields
    /// instead. Field names compare without case, as the request deserializer reads them.
    /// </summary>
    public static class RequestFieldGuard
    {
        #region Private-Members

        private static readonly ConcurrentDictionary<Type, HashSet<string>> _Accepted = new ConcurrentDictionary<Type, HashSet<string>>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// The top-level fields of the body that neither the request type nor the additional names accept, in the
        /// order the body holds them. A body that is not a JSON object has no fields to judge and returns none.
        /// </summary>
        /// <typeparam name="T">The request type the body is read into.</typeparam>
        /// <param name="body">The request body.</param>
        /// <param name="additional">Further accepted names, such as the id arguments a tool reads beside the request.</param>
        /// <returns>The unknown field names; empty when every field is accepted.</returns>
        public static List<string> Unknown<T>(JsonElement body, IEnumerable<string>? additional = null)
        {
            List<string> unknown = new List<string>();
            if (body.ValueKind != JsonValueKind.Object) return unknown;
            HashSet<string> accepted = _Accepted.GetOrAdd(typeof(T), AcceptedNames);
            HashSet<string> extra = new HashSet<string>(additional ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in body.EnumerateObject())
            {
                if (accepted.Contains(property.Name) || extra.Contains(property.Name)) continue;
                if (!unknown.Contains(property.Name, StringComparer.Ordinal)) unknown.Add(property.Name);
            }
            return unknown;
        }

        /// <summary>
        /// The refusal text for unknown fields: names every field and says that nothing was written.
        /// </summary>
        /// <param name="unknown">The unknown field names.</param>
        /// <returns>The message.</returns>
        public static string Message(IReadOnlyCollection<string> unknown)
        {
            if (unknown == null || unknown.Count == 0) return String.Empty;
            return "Unknown field(s): " + String.Join(", ", unknown) + ". Nothing was written; send only the fields the request accepts.";
        }

        #endregion

        #region Private-Methods

        private static HashSet<string> AcceptedNames(Type type)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod == null || !property.SetMethod.IsPublic) continue;
                names.Add(property.Name);
            }
            return names;
        }

        #endregion
    }
}
