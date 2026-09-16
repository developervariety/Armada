namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;

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
        /// Redact an arbitrary state object. The object is serialized to JSON and the whole JSON
        /// text is redacted and truncated, so no string field can egress unredacted. Returns the
        /// redacted string, suitable as the request state.
        /// </summary>
        /// <param name="state">The state object. Null is treated as empty.</param>
        /// <param name="maxChars">Maximum characters retained.</param>
        /// <returns>The redacted state as a string; never null.</returns>
        public static object RedactObject(object? state, int maxChars)
        {
            if (state == null) return String.Empty;
            if (state is string text) return Redact(text, maxChars);

            string json;
            try
            {
                json = JsonSerializer.Serialize(state);
            }
            catch (Exception)
            {
                json = state.ToString() ?? String.Empty;
            }

            return Redact(json, maxChars);
        }

        #endregion

        #region Private-Methods

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
