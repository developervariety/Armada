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
        #region Public-Members

        /// <summary>
        /// Version of the redaction rules. Bump it in the same commit as any change to what this
        /// class removes or keeps. Retained training samples are stamped with it, so samples redacted
        /// under different rules are never mixed in one training set: redacted text cannot be
        /// re-redacted later, because the original is never kept. Version 1 erased every dotted name
        /// and every 7-40 character hex run; version 2 keeps code symbols, file names and plain
        /// numbers; version 3 also removes every key shape the display redactor removes (labelled
        /// secrets, Bearer tokens, provider tokens by prefix) and the string value of a property the
        /// display redactor names as secret.
        /// </summary>
        public const int Version = 3;

        #endregion

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
        // Dotted-name candidates. Every dotted run is a candidate; whether it is a HOST or a code
        // symbol / file name is decided by <see cref="EvaluateDottedName"/>, not by the pattern. The
        // old pattern replaced EVERY dotted run with &lt;host&gt;, so a diff's file names
        // (LeakHunkAdapter.cs), namespaces (System.Text.Json) and member accesses (foo.Bar) were
        // erased before the diff-reading decisions ever saw them. A real host has a known public or
        // internal TLD in its final label; a code token does not.
        private static readonly Regex _DottedNames = new Regex(
            @"\b[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?)+\b",
            RegexOptions.Compiled);

        // A hex run of 7-40 chars is a commit hash ONLY when it either MIXES a hex letter with a
        // digit (a shape a plain decimal number or an English word cannot take) or sits in a commit
        // context. The old pattern replaced every 7-40 hex run, so it erased plain source numbers
        // (1234567), 8-digit constants (30000000), and hex-letter words ("defaced") as #sha.
        //
        // _HexContext: a pure-digit or pure-letter run that a commit cue introduces (commit 1234567,
        // HEAD is now at deadbeef, index 1234568..89abcde). Variable-length lookbehind is a .NET
        // feature. _HexMixed: a run carrying both a hex letter and a digit, redacted anywhere.
        private static readonly Regex _HexContext = new Regex(
            @"(?<=\b(?:commit|commits|sha|shas|hash|hashes|rev|revs|revision|revisions|HEAD|tip|tips|parent|parents|onto|checkout|checked out|index|blob|tree|show|ref|refs|at|from)\s{1,4})[0-9a-fA-F]{7,40}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _HexMixed = new Regex(
            @"\b(?=[0-9a-fA-F]{7,40}\b)(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]*[0-9][0-9a-fA-F]*\b",
            RegexOptions.Compiled);

        // Key-shaped tokens. The prefixed forms (provider keys) are unambiguous and always removed.
        // The generic long base64-ish run is decided by <see cref="EvaluateBlob"/>: the old
        // unconditional \b[A-Za-z0-9+/]{32,}={0,2} also matched any 32+-character identifier, so a
        // long method name (TruncatesLongestLeafAndStaysValidJson) was erased as &lt;secret&gt;.
        private static readonly Regex _KeyPrefixed = new Regex(
            @"\bsk-[^\s""',<>]+|\bghp_[^\s""',<>]+|\bglpat-[^\s""',<>]+",
            RegexOptions.Compiled);
        private static readonly Regex _KeyBlob = new Regex(
            @"\b[A-Za-z0-9+/]{32,}={0,2}",
            RegexOptions.Compiled);

        // Known TLDs whose presence in a dotted name's final label marks it a host. Curated, not the
        // full IANA list: short labels that collide with common code member names or Indonesian/other
        // vanity TLDs are deliberately left out (id → obj.Id, info → logger.info, now → DateTime.Now,
        // name, bar) because in this workspace those appear as code far more than as hostnames, and a
        // real host almost always reaches egress inside a URL (removed first) or with an internal
        // suffix below. Internal/reserved suffixes are the important half — they never appear in code.
        private static readonly HashSet<string> _KnownTlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Common public TLDs.
            "com", "net", "org", "io", "gov", "edu", "mil", "int", "biz", "dev", "app", "cloud",
            "ai", "co", "uk", "us", "ca", "de", "fr", "eu", "ru", "cn", "jp", "au", "nl", "se",
            "tv", "cc", "xyz", "tech", "online", "site", "store", "live", "sh", "ovh", "gg",
            // Internal, private-use and reserved suffixes: never a source symbol, always a host.
            "local", "lan", "corp", "arpa", "internal", "intranet", "home", "localdomain",
            "test", "example", "invalid", "onion", "k8s", "svc", "cluster",
        };

        // Extensions that make a SINGLE-dotted name a file, not a host, even when the extension also
        // exists as a TLD (run.sh, page.io would be a file only at one dot). A host reaches this list
        // only with 2+ dots (relay.example.sh), matching the "2+ dots and a known TLD" rule.
        private static readonly HashSet<string> _SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sh", "md", "cs", "py", "js", "ts", "jsx", "tsx", "mjs", "cjs", "json", "txt", "xml",
            "yml", "yaml", "sln", "csproj", "props", "targets", "cfg", "ini", "toml", "sql", "html",
            "css", "scss", "go", "rb", "rs", "java", "kt", "cpp", "hpp", "git", "log", "lock",
            "sample", "dll", "exe", "so", "dylib", "png", "jpg", "jpeg", "svg", "gif", "pdf", "csv",
            "mermaid", "http", "razor", "cshtml", "config", "gitignore", "editorconfig", "ipynb",
        };

        private const string _ArmadaMarker = "[ARMADA:";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Redact a state string and truncate it to the supplied character budget. Applies the
        /// redaction steps in order, then truncates: the final step keeps the head and tail while
        /// preserving any lines carrying an <c>[ARMADA:</c> marker from the elided middle.
        /// </summary>
        /// <param name="text">The raw state text. Null is treated as empty.</param>
        /// <param name="maxChars">Maximum characters retained. Values below one are treated as one.</param>
        /// <returns>The redacted, truncated state; never null.</returns>
        public static string Redact(string? text, int maxChars)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            if (maxChars < 1) maxChars = 1;

            // Key shapes first, with the display redactor's own rules: a token carries dots, dashes and
            // underscores that the host and id rules below would otherwise split before it is recognised.
            string working = SecretRedactor.RedactKeyShapes(text);
            working = _ArmadaIds.Replace(working, "#id");
            working = _AbsolutePaths.Replace(working, "<path>");
            working = _Urls.Replace(working, "<host>");
            working = _IPv4.Replace(working, "<host>");
            working = _DottedNames.Replace(working, EvaluateDottedName);
            working = _HexContext.Replace(working, "#sha");
            working = _HexMixed.Replace(working, "#sha");
            working = _KeyPrefixed.Replace(working, "<secret>");
            working = _KeyBlob.Replace(working, EvaluateBlob);

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

        // Decide whether a dotted run is a host to remove or a code symbol / file name to keep. A
        // host has a known public or internal TLD as its final label; a single-dotted file whose
        // extension merely happens to also be a TLD (run.sh) is kept, while the same extension with
        // two or more dots (relay.example.sh) is a host.
        private static string EvaluateDottedName(Match match)
        {
            string token = match.Value;
            int lastDot = token.LastIndexOf('.');
            if (lastDot < 0 || lastDot == token.Length - 1) return token;

            string tld = token.Substring(lastDot + 1);
            int dots = 0;
            for (int i = 0; i < token.Length; i++) if (token[i] == '.') dots++;

            if (dots == 1 && _SourceExtensions.Contains(tld)) return token;
            if (_KnownTlds.Contains(tld)) return "<host>";
            return token;
        }

        // Decide whether a 32+-character base64-ish run is a secret to remove or a long identifier to
        // keep. A base64 secret carries entropy a wordy identifier does not: a digit, a base64 special
        // character, or a high density of upper/lower case transitions. A run that is all letters, one
        // case-run per word (a method or type name), is kept. This can miss the rare secret that is
        // pure mixed-case letters with few transitions, but that shape is statistically uncommon and
        // an under-caught host reaches egress far more often through a URL or internal suffix, both
        // removed earlier; erasing every long identifier instead is the larger, certain loss.
        private static string EvaluateBlob(Match match)
        {
            string token = match.Value;
            // '+' and '=' are base64-specific; '/' is deliberately NOT a signal, because the run may
            // be a slash-joined code path (Services/TypedDecisions/TypedPriorArtAdapter), which the
            // digit and case-transition tests below correctly keep.
            if (token.IndexOfAny(_BlobSecretChars) >= 0) return "<secret>";
            foreach (char c in token) if (c >= '0' && c <= '9') return "<secret>";

            int transitions = 0;
            for (int i = 1; i < token.Length; i++)
            {
                bool upperNow = Char.IsUpper(token[i]);
                bool upperPrev = Char.IsUpper(token[i - 1]);
                if (upperNow != upperPrev) transitions++;
            }
            // A ratio of one case transition per two characters or denser reads as random base64, not
            // an identifier whose words run several same-case characters at a time.
            if (transitions * 2 >= token.Length) return "<secret>";
            return token;
        }

        private static readonly char[] _BlobSecretChars = new[] { '+', '=' };

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
                        bool secretValue = SecretRedactor.IsRedactedPropertyName(property.Key)
                            && property.Value is JsonValue leaf && leaf.GetValueKind() == JsonValueKind.String;
                        redactedObject[unique] = secretValue ? JsonValue.Create("[REDACTED]") : RedactNode(property.Value);
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
