namespace Armada.Core.Services
{
    using System;
    using System.Text.RegularExpressions;
    using System.Text.Json;

    /// <summary>Shared secret-shaped value redaction for stored and displayed runtime output.</summary>
    public static class SecretRedactor
    {
        private static readonly RedactionRule[] _Redactions = new RedactionRule[]
        {
            new RedactionRule(new Regex(@"(?i)(api[_\-]?key|apikey|secret|token|password)(\s*[""']?\s*[=:]\s*)(?<q>[""'])(?:\\[\s\S]|(?!\k<q>)[^\\])*(?:\k<q>|$)", RegexOptions.Compiled), "$1$2${q}[REDACTED]${q}"),
            new RedactionRule(new Regex(@"(?i)\bBearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.Compiled), "Bearer [REDACTED]"),
            new RedactionRule(new Regex(@"\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled), "sk-[REDACTED]"),
            new RedactionRule(new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "gh_[REDACTED]"),
            new RedactionRule(new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled), "AKIA[REDACTED]"),
            new RedactionRule(new Regex(@"(?i)(api[_\-]?key|apikey|secret|token|password)(\s*[=:]\s*)([""']?)[^\s""']{6,}", RegexOptions.Compiled), "$1$2$3[REDACTED]"),
        };

        private static readonly Regex _JsonStringProperty = new Regex(
            @"(?<key>""(?:\\[\s\S]|[^""\\])*"")(?<separator>\s*:\s*)(?<value>""(?:\\[\s\S]|[^""\\])*""?)", RegexOptions.Compiled);
        private static readonly Regex _JsonStringToken = new Regex(@"""(?:\\[\s\S]|[^""\\])*""", RegexOptions.Compiled);
        private static readonly Regex _SensitiveProperty = new Regex(
            @"\A(?:api[_\-]?key|apikey|secret|token|password|passwd|authorization|access_token|refresh_token|client_secret)\z",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Redact protected values without applying a display size limit.</summary>
        public static string Redact(string? text) => RedactCore(text ?? String.Empty, 0);

        private static string RedactCore(string text, int depth)
        {
            if (depth > 8) return "[REDACTED: nested value]";
            string safe = _JsonStringProperty.Replace(text, match => RedactProperty(match, depth));
            safe = _JsonStringToken.Replace(safe, match => RedactStringToken(match, depth));
            foreach (RedactionRule rule in _Redactions) safe = rule.Pattern.Replace(safe, rule.Replacement);
            return safe;
        }

        private static string RedactProperty(Match match, int depth)
        {
            string key;
            try { key = JsonSerializer.Deserialize<string>(match.Groups["key"].Value) ?? String.Empty; }
            catch (JsonException)
            {
                // A malformed escaped key cannot establish that its value is safe to display.
                return match.Groups["key"].Value + match.Groups["separator"].Value + "\"[REDACTED]\"";
            }
            string prefix = match.Groups["key"].Value + match.Groups["separator"].Value;
            if (_SensitiveProperty.IsMatch(key)) return prefix + "\"[REDACTED]\"";
            return match.Value;
        }

        private static string RedactStringToken(Match match, int depth)
        {
            try
            {
                string decoded = JsonSerializer.Deserialize<string>(match.Value) ?? String.Empty;
                string redacted = RedactCore(decoded, depth + 1);
                return redacted == decoded ? match.Value : JsonSerializer.Serialize(redacted);
            }
            catch (JsonException)
            {
                return "\"[REDACTED: malformed string]\"";
            }
        }

        private sealed class RedactionRule
        {
            internal Regex Pattern { get; }
            internal string Replacement { get; }
            internal RedactionRule(Regex pattern, string replacement) { Pattern = pattern; Replacement = replacement; }
        }
    }
}
