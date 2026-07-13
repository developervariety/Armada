namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Pure regex evaluator over '+' lines (additions only) of a unified diff.
    /// Rules check CORE RULE 2 (mocking libs), CORE RULE 4 (structured logging),
    /// CORE RULE 5 (secret patterns), CORE RULE 12 (spec/plan refs in comments).
    /// Non-blocking: failures don't prevent auto-land; they escalate to deep review.
    /// </summary>
    public sealed class ConventionChecker : IConventionChecker
    {
        // SHA-256 hex digest: exactly 64 lowercase hex chars bounded by non-word characters.
        private static readonly Regex _Sha256HexDigestPattern =
            new Regex(@"\b[0-9a-f]{64}\b", RegexOptions.Compiled);

        // SRI integrity hash: sha256- prefix followed by 43-44 base64 chars with optional padding.
        private static readonly Regex _Sha256SriDigestPattern =
            new Regex(@"sha256-[A-Za-z0-9+/]{43,44}={0,2}", RegexOptions.Compiled);

        // Hash-related field keyword indicating the line is a content-digest declaration.
        private static readonly Regex _HashFieldKeywordPattern =
            new Regex(@"\b(?:sha256|integrity|hash|digest|checksum)\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly (string Rule, Regex Pattern)[] _Rules = new (string, Regex)[]
        {
            ("CORE_RULE_2_mocking_lib", new Regex(@"using\s+(Moq|NSubstitute|FakeItEasy|Rhino\.Mocks|JustMock|Moq\.Protected|NSubstitute\.Extensions)\b", RegexOptions.Compiled)),
            ("CORE_RULE_4_log_interpolation", new Regex(@"\.(LogInformation|LogDebug|LogWarning|LogError|LogTrace|LogCritical)\s*\(\s*\$""", RegexOptions.Compiled)),
            ("CORE_RULE_5_private_key", new Regex(@"-----BEGIN (RSA |EC )?PRIVATE KEY-----", RegexOptions.Compiled)),
            ("CORE_RULE_5_base64_chunk", new Regex(@"""[A-Za-z0-9+/]{40,}={0,2}""", RegexOptions.Compiled)),
            ("CORE_RULE_5_password_literal", new Regex(@"password\s*[:=]\s*""\w{8,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("CORE_RULE_5_apikey_literal", new Regex(@"api_?key\s*[:=]\s*""\w{16,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("CORE_RULE_5_bearer_literal", new Regex(@"bearer\s+[A-Za-z0-9._~-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("CORE_RULE_5_seed_literal", new Regex(@"\bseed\s*[:=]\s*""[A-Za-z0-9+/\s]{8,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("CORE_RULE_12_spec_plan_ref", new Regex(@"(see plan|per the.*(spec|plan)|tracked in TODO|superpowers/(plans|specs)|TODO\.md)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        };

        /// <summary>
        /// CORE_RULE_5 regex pattern strings for inclusion in the dock boundary hook
        /// configuration file. These are the same patterns used by
        /// <see cref="CheckSecretLine"/> so the hook and the server-side gate are consistent.
        /// </summary>
        public static IReadOnlyList<string> BuiltInSecretPatternStrings
        {
            get
            {
                List<string> patterns = new List<string>();
                foreach ((string rule, Regex pattern) in _Rules)
                {
                    if (rule.StartsWith("CORE_RULE_5", StringComparison.Ordinal))
                        patterns.Add(pattern.ToString());
                }
                return patterns.AsReadOnly();
            }
        }

        /// <summary>
        /// Check a single addition line against CORE_RULE_5 secret patterns only.
        /// Used by DockBoundaryScanner to run file-scoped secret detection without
        /// re-scanning the full diff through all convention rules.
        /// Returns matched rule names; empty list when no secret pattern fires.
        /// Secret bytes are never echoed -- only the rule name is returned.
        /// </summary>
        /// <param name="addedLine">
        /// Content of a '+' addition line from a unified diff, with the leading '+' stripped.
        /// </param>
        /// <returns>Read-only list of CORE_RULE_5 rule names that matched.</returns>
        public static IReadOnlyList<string> CheckSecretLine(string addedLine)
        {
            List<string> matched = new List<string>();
            if (String.IsNullOrEmpty(addedLine)) return matched;
            foreach ((string rule, System.Text.RegularExpressions.Regex pattern) in _Rules)
            {
                if (!rule.StartsWith("CORE_RULE_5", StringComparison.Ordinal)) continue;
                if (pattern.IsMatch(addedLine)) matched.Add(rule);
            }
            return matched;
        }

        /// <summary>
        /// Returns true when the fired CORE_RULE_5 rule should be exempted because the
        /// matched token is a SHA-256 content digest appearing in a manifest or lockfile
        /// context. Only the <c>CORE_RULE_5_base64_chunk</c> rule is eligible for this
        /// exemption; all other rules are unaffected.
        /// The allowlist requires TWO conditions to exempt a match:
        /// (1) the addition line contains a SHA-256 digest token (64 lowercase hex chars,
        ///     or a <c>sha256-</c> SRI base64 prefix form), AND
        /// (2) the line contains a hash-field keyword (<c>sha256</c>, <c>integrity</c>,
        ///     <c>hash</c>, <c>digest</c>, <c>checksum</c>) or the file is a known
        ///     manifest/lockfile type.
        /// A bare 64-hex token in a line that has none of these context signals is NOT
        /// exempted and continues to be treated as a potential secret per existing policy.
        /// </summary>
        /// <param name="rule">The CORE_RULE_5 rule name returned by <see cref="CheckSecretLine"/>.</param>
        /// <param name="addedLine">Addition line content (leading '+' already stripped).</param>
        /// <param name="filePath">Repository-relative file path; may be null or empty.</param>
        /// <returns>True when the match should be suppressed as a manifest content digest.</returns>
        public static bool IsManifestHashAllowed(string rule, string addedLine, string? filePath)
        {
            if (!String.Equals(rule, "CORE_RULE_5_base64_chunk", StringComparison.Ordinal))
                return false;
            if (String.IsNullOrEmpty(addedLine))
                return false;

            // Token must look like a SHA-256 content digest: 64 lowercase hex chars or SRI form.
            bool hasHexDigest = _Sha256HexDigestPattern.IsMatch(addedLine);
            bool hasSriDigest = _Sha256SriDigestPattern.IsMatch(addedLine);
            if (!hasHexDigest && !hasSriDigest)
                return false;

            // Context must indicate this is a hash field or a manifest/lockfile.
            return _HashFieldKeywordPattern.IsMatch(addedLine) || IsKnownManifestFile(filePath);
        }

        private static bool IsKnownManifestFile(string? filePath)
        {
            if (String.IsNullOrEmpty(filePath))
                return false;

            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName);

            // Known manifest/lockfile extensions.
            if (String.Equals(ext, ".lock", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(ext, ".lockfile", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(ext, ".manifest", StringComparison.OrdinalIgnoreCase)) return true;

            // Known manifest file names that don't have a distinctive extension.
            string[] knownNames = new string[]
            {
                "package-lock.json",
                "npm-shrinkwrap.json",
                "go.sum",
                "pnpm-lock.yaml",
                "pnpm-lock.yml"
            };

            foreach (string name in knownNames)
            {
                if (String.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // File names ending in -lock.json (e.g. composer-lock.json, bun-lock.json).
            if (fileName.EndsWith("-lock.json", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>Checks the unified diff and returns the result of all rule evaluations.</summary>
        public ConventionCheckResult Check(string unifiedDiff)
        {
            ConventionCheckResult result = new ConventionCheckResult();
            if (string.IsNullOrEmpty(unifiedDiff)) return result;

            foreach (string rawLine in unifiedDiff.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                // Only '+' addition lines. Skip '+++' headers and context/deletion lines.
                if (line.Length == 0 || line[0] != '+') continue;
                if (line.StartsWith("+++", StringComparison.Ordinal)) continue;

                foreach ((string rule, Regex pattern) in _Rules)
                {
                    if (pattern.IsMatch(line))
                    {
                        result.Violations.Add(new ConventionViolation(rule, line));
                        result.Passed = false;
                    }
                }
            }
            return result;
        }
    }
}
