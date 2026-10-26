namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using Armada.Core.Models;
    using Armada.Core.Services;

    internal static class AskActionArguments
    {
        internal static string Canonicalize(JsonElement? arguments)
        {
            string text = arguments.HasValue ? JsonSerializer.Serialize(arguments) : "{}";
            if (text.Length > 32000) throw new ArgumentException("The action arguments exceed the saved-action limit.");
            JsonNode? node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 16 });
            if (node is not JsonObject) throw new ArgumentException("Action arguments must be a JSON object.");
            RejectSensitive(node);
            return Sort(node).ToJsonString();
        }

        internal static string Fingerprint(AskActionProposal proposal)
        {
            string text = JsonSerializer.Serialize(new[]
            {
                "ask-action-v1", proposal.TenantId, proposal.UserId, proposal.ThreadId,
                proposal.RequestId, proposal.ToolName, proposal.ArgumentsJson
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        private static JsonNode Sort(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                JsonObject sorted = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> item in obj.OrderBy(item => item.Key, StringComparer.Ordinal))
                    sorted[item.Key] = item.Value == null ? null : Sort(item.Value);
                return sorted;
            }
            if (node is JsonArray array)
            {
                JsonArray sorted = new JsonArray();
                foreach (JsonNode? item in array) sorted.Add(item == null ? null : Sort(item));
                return sorted;
            }
            return node.DeepClone();
        }

        private static void RejectSensitive(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (KeyValuePair<string, JsonNode?> item in obj)
                {
                    if (SecretRedactor.IsSecretPropertyName(item.Key))
                        throw new ArgumentException("This action contains protected values and cannot be saved in chat.");
                    if (item.Value != null) RejectSensitive(item.Value);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (JsonNode? item in array) if (item != null) RejectSensitive(item);
            }
            else
            {
                string text = node.ToJsonString();
                if (!String.Equals(text, SecretRedactor.Redact(text), StringComparison.Ordinal))
                    throw new ArgumentException("This action contains protected values and cannot be saved in chat.");
            }
        }
    }
}
