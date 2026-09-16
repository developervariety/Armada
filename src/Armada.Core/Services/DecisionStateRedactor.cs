namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// Redacts decision state before it egresses to the typed-decision provider. This is the
    /// single guard that keeps private context out of an outbound request: Armada identifiers,
    /// absolute paths, hosts and URLs, commit hashes, and key-shaped tokens are removed, and the
    /// result is truncated to a byte budget. The redaction order is load-bearing (ids before
    /// hashes, hosts before hashes) and is proven by a guard test.
    /// </summary>
    public static class DecisionStateRedactor
    {
        #region Private-Members

        // 1. Armada identifiers: a known entity prefix followed by the id body.
        private static readonly Regex _ArmadaIds = new Regex(
            @"\b(flt_|vsl_|cpt_|msn_|vyg_|obj_|inc_|chk_|mrg_|dock_|cmsg_|sig_|evt_)[A-Za-z0-9_]+",
            RegexOptions.Compiled);

        // 2. Absolute paths: the workspace roots on every host, plus a Windows drive letter. The
        // tail stops at whitespace and at the delimiters that bound a value in JSON or prose, so a
        // path embedded in compact JSON (no spaces) does not swallow the rest of the object.
        private static readonly Regex _AbsolutePaths = new Regex(
            @"(?:/srv|/home|/tmp|/Volumes|[A-Za-z]:\\)[^\s""',;<>]*",
            RegexOptions.Compiled);

        // 3. URLs, then IPv4 addresses, then dotted hostnames. The URL tail stops at the same
        // delimiters for the same reason.
        private static readonly Regex _Urls = new Regex(
            @"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s""',<>]+",
            RegexOptions.Compiled);
        private static readonly Regex _IPv4 = new Regex(
            @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
            RegexOptions.Compiled);
        private static readonly Regex _Hostnames = new Regex(
            @"\b(?:[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?\.)+[A-Za-z]{2,}\b",
            RegexOptions.Compiled);

        // 4. Commit-hash-shaped hex runs.
        private static readonly Regex _Hex = new Regex(
            @"\b[0-9a-fA-F]{7,40}\b",
            RegexOptions.Compiled);

        // 5. Key-shaped tokens: provider key prefixes and long base64-ish runs. Prefix tails stop
        // at JSON/prose delimiters so a key value in compact JSON does not over-consume.
        private static readonly Regex _KeyShaped = new Regex(
            @"\bsk-[^\s""',<>]+|\bghp_[^\s""',<>]+|\bglpat-[^\s""',<>]+|\b[A-Za-z0-9+/]{32,}={0,2}",
            RegexOptions.Compiled);

        private const string _ArmadaMarker = "[ARMADA:";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Redact a state string and truncate it to the supplied character budget. Applies the six
        /// redaction steps in order; step six keeps the head and tail while preserving any lines
        /// carrying an <c>[ARMADA:</c> marker from the elided middle.
        /// </summary>
        /// <param name="text">The raw state text. Null is treated as empty.</param>
        /// <param name="maxChars">Maximum characters retained. Values below one are treated as one.</param>
        /// <returns>The redacted, truncated state; never null.</returns>
        public static string Redact(string? text, int maxChars)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            if (maxChars < 1) maxChars = 1;

            string working = text;
            working = _ArmadaIds.Replace(working, "#id");
            working = _AbsolutePaths.Replace(working, "<path>");
            working = _Urls.Replace(working, "<host>");
            working = _IPv4.Replace(working, "<host>");
            working = _Hostnames.Replace(working, "<host>");
            working = _Hex.Replace(working, "#sha");
            working = _KeyShaped.Replace(working, "<secret>");

            return Truncate(working, maxChars);
        }

        /// <summary>
        /// Redact a decision state for egress. A string state is redacted and truncated as text. Any
        /// other state is sent as a JSON object: every property name and string value is redacted in
        /// place, and when the serialized object exceeds the budget its longest strings are truncated
        /// (keeping head, tail, and marker lines) until it fits, so the provider still receives valid,
        /// structured JSON. A state that cannot fit that way falls back to redacted, truncated text.
        /// </summary>
        /// <param name="state">The state. Null is treated as empty.</param>
        /// <param name="maxChars">Maximum characters of serialized state retained.</param>
        /// <returns>The value to transmit and its exact serialized text; never null.</returns>
        public static RedactedDecisionState RedactState(object? state, int maxChars)
        {
            if (maxChars < 1) maxChars = 1;
            if (state == null) return RedactedDecisionState.FromText(String.Empty);
            if (state is string text) return RedactedDecisionState.FromText(Redact(text, maxChars));

            JsonNode? node;
            try
            {
                node = JsonSerializer.SerializeToNode(state);
            }
            catch (Exception)
            {
                return RedactedDecisionState.FromText(Redact(state.ToString(), maxChars));
            }

            if (node is not JsonObject && node is not JsonArray)
                return RedactedDecisionState.FromText(Redact(node?.ToJsonString() ?? String.Empty, maxChars));

            node = RedactNode(node);
            string json = node!.ToJsonString();
            if (json.Length > maxChars && !FitToBudget(node, maxChars, out json))
                return RedactedDecisionState.FromText(Redact(json, maxChars));

            return new RedactedDecisionState(node, json);
        }

        #endregion

        #region Private-Methods

        // The shortest a string leaf is cut to while fitting an object to its budget; below this a
        // leaf carries too little to be worth keeping, and the text fallback is used instead.
        private const int _MinLeafChars = 64;

        private static JsonNode? RedactNode(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    JsonObject redactedObject = new JsonObject();
                    foreach (KeyValuePair<string, JsonNode?> property in obj.ToList())
                    {
                        obj.Remove(property.Key);
                        string key = Redact(property.Key, Int32.MaxValue);
                        string unique = key;
                        for (int suffix = 2; redactedObject.ContainsKey(unique); suffix++)
                            unique = key + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        redactedObject[unique] = RedactNode(property.Value);
                    }
                    return redactedObject;
                case JsonArray array:
                    JsonArray redactedArray = new JsonArray();
                    foreach (JsonNode? item in array.ToList())
                    {
                        array.Remove(item);
                        redactedArray.Add(RedactNode(item));
                    }
                    return redactedArray;
                case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                    return JsonValue.Create(Redact(value.GetValue<string>(), Int32.MaxValue));
                default:
                    return node;
            }
        }

        private static bool FitToBudget(JsonNode root, int maxChars, out string json)
        {
            json = root.ToJsonString();
            HashSet<JsonValue> exhausted = new HashSet<JsonValue>(ReferenceEqualityComparer.Instance);
            while (json.Length > maxChars)
            {
                JsonValue? longest = null;
                int longestLength = 0;
                foreach (JsonValue leaf in StringLeaves(root))
                {
                    if (exhausted.Contains(leaf)) continue;
                    int length = leaf.GetValue<string>().Length;
                    if (length > longestLength)
                    {
                        longest = leaf;
                        longestLength = length;
                    }
                }

                if (longest == null || longestLength <= _MinLeafChars) return false;

                int target = Math.Max(_MinLeafChars, longestLength - (json.Length - maxChars));
                string cut = Truncate(longest.GetValue<string>(), target);
                if (cut.Length >= longestLength)
                {
                    exhausted.Add(longest);
                    continue;
                }

                JsonValue replacement = JsonValue.Create(cut);
                exhausted.Remove(longest);
                ReplaceNode(longest, replacement);
                json = root.ToJsonString();
            }
            return true;
        }

        private static IEnumerable<JsonValue> StringLeaves(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (KeyValuePair<string, JsonNode?> property in obj)
                        foreach (JsonValue leaf in StringLeaves(property.Value)) yield return leaf;
                    break;
                case JsonArray array:
                    foreach (JsonNode? item in array)
                        foreach (JsonValue leaf in StringLeaves(item)) yield return leaf;
                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                    yield return value;
                    break;
            }
        }

        private static void ReplaceNode(JsonNode current, JsonNode replacement)
        {
            JsonNode? parent = current.Parent;
            if (parent is JsonObject obj)
            {
                obj[current.GetPropertyName()] = replacement;
            }
            else if (parent is JsonArray array)
            {
                array[current.GetElementIndex()] = replacement;
            }
        }

        private static string Truncate(string text, int maxChars)
        {
            if (text.Length <= maxChars) return text;

            int headLen = (int)(maxChars * 0.6);
            int tailLen = maxChars - headLen;
            if (headLen < 0) headLen = 0;
            if (tailLen < 0) tailLen = 0;

            string head = text.Substring(0, Math.Min(headLen, text.Length));
            string tail = tailLen > 0 && tailLen <= text.Length ? text.Substring(text.Length - tailLen) : String.Empty;

            int middleStart = head.Length;
            int middleLen = text.Length - head.Length - tail.Length;
            string middle = middleLen > 0 ? text.Substring(middleStart, middleLen) : String.Empty;

            List<string> preserved = new List<string>();
            if (middle.Length > 0)
            {
                foreach (string line in middle.Split('\n'))
                {
                    if (line.Contains(_ArmadaMarker, StringComparison.Ordinal))
                        preserved.Add(line.Trim());
                }
            }

            StringBuilder sb = new StringBuilder(maxChars + 128);
            sb.Append(head);
            sb.Append("\n…[state truncated]…\n");
            foreach (string line in preserved)
            {
                sb.Append(line);
                sb.Append('\n');
            }
            sb.Append(tail);
            return sb.ToString();
        }

        #endregion
    }
}
