namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Redacts secrets from a settings document written into a backup archive, and merges an archived document back
    /// onto a host's live settings without ever writing a placeholder over a real value. Secret detection uses
    /// <see cref="SecretRedactor.IsSecretPropertyName"/> for names and <see cref="SecretRedactor.Redact"/> for values,
    /// so settings follow the same rule as every other redacted surface.
    /// </summary>
    public static class SettingsSecretRedaction
    {
        /// <summary>
        /// Value written in place of a secret.
        /// </summary>
        public const string Placeholder = "[REDACTED]";

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions { WriteIndented = true };

        /// <summary>
        /// Replace every secret string value with <see cref="Placeholder"/>.
        /// </summary>
        /// <param name="settingsJson">Settings document.</param>
        /// <param name="redactedCount">Number of values replaced.</param>
        /// <returns>Redacted document.</returns>
        /// <exception cref="JsonException">The document is not valid JSON.</exception>
        public static string Redact(string settingsJson, out int redactedCount)
        {
            JsonNode root = JsonNode.Parse(settingsJson) ?? throw new JsonException("settings document is empty");
            Counter redacted = new Counter();
            RedactNode(root, redacted);
            redactedCount = redacted.Value;
            return root.ToJsonString(WriteOptions);
        }

        /// <summary>
        /// Merge an archived settings document onto the target host's settings. Every placeholder takes the target's
        /// value at the same path; a placeholder with no target value is removed. Non-secret values come from the archive.
        /// </summary>
        /// <param name="archivedJson">Settings document from the archive.</param>
        /// <param name="targetJson">Current settings on the target host, or null when none exist.</param>
        /// <param name="preservedCount">Number of target secrets kept.</param>
        /// <param name="droppedCount">Number of placeholders removed because the target had no value.</param>
        /// <returns>Merged document containing no placeholder.</returns>
        /// <exception cref="JsonException">Either document is not valid JSON.</exception>
        public static string MergeForRestore(string archivedJson, string? targetJson, out int preservedCount, out int droppedCount)
        {
            JsonNode archived = JsonNode.Parse(archivedJson) ?? throw new JsonException("archived settings document is empty");
            JsonNode? target = String.IsNullOrWhiteSpace(targetJson) ? null : JsonNode.Parse(targetJson);
            Counter preserved = new Counter();
            Counter dropped = new Counter();
            MergeNode(archived, target, preserved, dropped);
            string merged = archived.ToJsonString(WriteOptions);
            if (merged.Contains("[REDACTED", StringComparison.Ordinal))
                throw new InvalidOperationException("settings_placeholder_remains");
            preservedCount = preserved.Value;
            droppedCount = dropped.Value;
            return merged;
        }

        private static void RedactNode(JsonNode? node, Counter redacted)
        {
            if (node is JsonObject obj)
            {
                foreach (string key in new List<string>(KeysOf(obj)))
                {
                    JsonNode? child = obj[key];
                    if (TryGetString(child, out string value))
                    {
                        if (value.Length > 0 && (SecretRedactor.IsSecretPropertyName(key) || SecretRedactor.Redact(value) != value))
                        {
                            obj[key] = Placeholder;
                            redacted.Value++;
                        }
                    }
                    else
                    {
                        RedactNode(child, redacted);
                    }
                }
            }
            else if (node is JsonArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (TryGetString(array[i], out string value))
                    {
                        if (value.Length > 0 && SecretRedactor.Redact(value) != value)
                        {
                            array[i] = Placeholder;
                            redacted.Value++;
                        }
                    }
                    else
                    {
                        RedactNode(array[i], redacted);
                    }
                }
            }
        }

        private static void MergeNode(JsonNode node, JsonNode? target, Counter preserved, Counter dropped)
        {
            if (node is JsonObject obj)
            {
                JsonObject? targetObject = target as JsonObject;
                foreach (string key in new List<string>(KeysOf(obj)))
                {
                    JsonNode? child = obj[key];
                    JsonNode? targetChild = targetObject != null && targetObject.ContainsKey(key) ? targetObject[key] : null;
                    if (IsPlaceholder(child))
                    {
                        if (targetChild != null && !ContainsPlaceholder(targetChild))
                        {
                            obj[key] = targetChild.DeepClone();
                            preserved.Value++;
                        }
                        else
                        {
                            obj.Remove(key);
                            dropped.Value++;
                        }
                    }
                    else if (child != null)
                    {
                        MergeNode(child, targetChild, preserved, dropped);
                    }
                }
            }
            else if (node is JsonArray array)
            {
                JsonArray? targetArray = target as JsonArray;
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    JsonNode? child = array[i];
                    JsonNode? targetChild = targetArray != null && i < targetArray.Count ? targetArray[i] : null;
                    if (IsPlaceholder(child))
                    {
                        if (targetChild != null && !ContainsPlaceholder(targetChild))
                        {
                            array[i] = targetChild.DeepClone();
                            preserved.Value++;
                        }
                        else
                        {
                            array.RemoveAt(i);
                            dropped.Value++;
                        }
                    }
                    else if (child != null)
                    {
                        MergeNode(child, targetChild, preserved, dropped);
                    }
                }
            }
        }

        private static IEnumerable<string> KeysOf(JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> property in obj) yield return property.Key;
        }

        private static bool TryGetString(JsonNode? node, out string value)
        {
            value = String.Empty;
            if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? text) && text != null)
            {
                value = text;
                return true;
            }
            return false;
        }

        private static bool IsPlaceholder(JsonNode? node)
        {
            return TryGetString(node, out string value) && value.Contains("[REDACTED", StringComparison.Ordinal);
        }

        private static bool ContainsPlaceholder(JsonNode node)
        {
            return node.ToJsonString().Contains("[REDACTED", StringComparison.Ordinal);
        }

        private sealed class Counter
        {
            public int Value { get; set; }
        }
    }
}
