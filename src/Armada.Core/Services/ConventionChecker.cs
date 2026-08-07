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
    /// CORE_RULE_5_base64_chunk applies an entropy and character-class gate before
    /// firing, so long CamelCase identifiers, slash-joined path lists, and hex-ID runs
    /// inside single-line JSON files no longer false-positive while genuine base64
    /// key/seed/password material still blocks.
    /// </summary>
    public sealed class ConventionChecker : IConventionChecker
    {
        // SHA-256 hex digest: exactly 64 lowercase hex chars bounded by non-word characters.
        private static readonly Regex _Sha256HexDigestPattern =
            new Regex(@"\b[0-9a-f]{64}\b", RegexOptions.Compiled);

        // SRI integrity hash: sha256- prefix followed by 43-44 base64 chars with optional padding.
        private static readonly Regex _Sha256SriDigestPattern =
            new Regex(@"sha256-[A-Za-z0-9+/]{43,44}={0,2}", RegexOptions.Compiled);

        // ERE pattern string for the base64-chunk rule. Shared verbatim by the dock-boundary
        // hook config (DockService) so the hook and the server-side gate cannot drift. The
        // pattern is deliberately broad; <see cref="LooksLikeBase64Secret"/> decides whether
        // a quoted run is genuine secret material before the rule fires.
        internal const string Base64ChunkPatternString = "\"[A-Za-z0-9+/]{40,}={0,2}\"";

        // Entropy-gate thresholds for CORE_RULE_5_base64_chunk, calibrated against the
        // source-glossary certified-command-catalog.json false-positive population (long
        // CamelCase identifiers, slash-joined path lists, hex-ID runs) and against real
        // base64 key/seed/password material. Two independent branches fire:
        // 1. Structural: balanced case (|P(upper) - P(lower)| &lt;= MaxCaseBalanceStructuralBranch)
        //    with meaningful fractions of upper, lower, and digit-or-slash characters.
        // 2. Entropy: Shannon entropy above MinEntropyForEntropyBranch with case balance up
        //    to MaxCaseBalanceForEntropyBranch, catching real random material whose case
        //    split happens to be skewed.
        // Chunks whose alphabet is a subset of hex ([0-9a-fA-F]) never fire: they are IDs,
        // digests, or hex-ID runs, not base64 secrets.
        internal const double MaxCaseBalanceStructuralBranch = 0.40;
        internal const double MinClassFractionStructuralBranch = 0.15;
        internal const double MinDigitFractionStructuralBranch = 0.05;
        internal const double MinEntropyForEntropyBranch = 4.6;
        internal const double MaxCaseBalanceForEntropyBranch = 0.62;

        // Hash-related field keyword indicating the line is a content-digest declaration.
        // "sha256" matches embedded (BundleSha256, SourceTreeSha256, ...), not just as a standalone word --
        // manifest hash fields commonly qualify the digest name. The digest-value gate still requires a genuine
        // 64-hex SHA-256, so a real base64 secret is never exempted by this.
        private static readonly Regex _HashFieldKeywordPattern =
            new Regex(@"(?:sha-?256|\b(?:integrity|hash|digest|checksum)\b)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly (string Rule, Regex Pattern)[] _Rules = new (string, Regex)[]
        {
            ("CORE_RULE_2_mocking_lib", new Regex(@"using\s+(Moq|NSubstitute|FakeItEasy|Rhino\.Mocks|JustMock|Moq\.Protected|NSubstitute\.Extensions)\b", RegexOptions.Compiled)),
            ("CORE_RULE_4_log_interpolation", new Regex(@"\.(LogInformation|LogDebug|LogWarning|LogError|LogTrace|LogCritical)\s*\(\s*\$""", RegexOptions.Compiled)),
            ("CORE_RULE_5_private_key", new Regex(@"-----BEGIN (RSA |EC )?PRIVATE KEY-----", RegexOptions.Compiled)),
            ("CORE_RULE_5_base64_chunk", new Regex(Base64ChunkPatternString, RegexOptions.Compiled)),
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
        /// The base64-chunk rule fires only when at least one quoted run passes
        /// <see cref="LooksLikeBase64Secret"/>, so identifiers and hex runs in
        /// single-line JSON catalogs do not false-positive.
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
                if (RuleFiresOnLine(rule, pattern, addedLine)) matched.Add(rule);
            }
            return matched;
        }

        /// <summary>
        /// Evaluate one rule against one addition line. The base64-chunk rule evaluates
        /// every quoted candidate run on the line and fires only when a candidate passes
        /// <see cref="LooksLikeBase64Secret"/>; all other rules use a plain regex match.
        /// </summary>
        private static bool RuleFiresOnLine(string rule, Regex pattern, string line)
        {
            if (!String.Equals(rule, "CORE_RULE_5_base64_chunk", StringComparison.Ordinal))
                return pattern.IsMatch(line);

            foreach (Match match in pattern.Matches(line))
            {
                // Match.Value carries the surrounding quotes; strip them before measuring.
                string chunk = match.Value.Trim('"');
                if (LooksLikeBase64Secret(chunk)) return true;
            }
            return false;
        }

        /// <summary>
        /// Decide whether a quoted base64-alphabet run is genuine secret material.
        /// The run must be non-hex, and it must satisfy either the structural branch
        /// (balanced case with meaningful upper, lower, and digit-or-slash fractions)
        /// or the entropy branch (Shannon entropy at or above
        /// <see cref="MinEntropyForEntropyBranch"/> with case balance at or below
        /// <see cref="MaxCaseBalanceForEntropyBranch"/>). Trailing '=' padding is
        /// stripped before measurement because it is not encoded content.
        /// </summary>
        /// <param name="chunk">Quoted base64 run with surrounding quotes already removed.</param>
        /// <returns>True when the run looks like real base64 secret material.</returns>
        internal static bool LooksLikeBase64Secret(string? chunk)
        {
            if (String.IsNullOrEmpty(chunk)) return false;

            string body = chunk.TrimEnd('=');
            int length = body.Length;
            if (length < 2) return false;

            int upper = 0;
            int lower = 0;
            int digitOrSlash = 0;
            bool hexOnly = true;
            Dictionary<char, int> counts = new Dictionary<char, int>();

            foreach (char c in body)
            {
                if (c >= 'A' && c <= 'Z') upper++;
                else if (c >= 'a' && c <= 'z') lower++;
                if ((c >= '0' && c <= '9') || c == '+' || c == '/') digitOrSlash++;
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    hexOnly = false;

                if (!counts.ContainsKey(c)) counts[c] = 1;
                else counts[c] = counts[c] + 1;
            }

            // Hex-alphabet runs are IDs, digests, or hex-ID runs, never base64 secrets.
            if (hexOnly) return false;

            double upperFraction = (double)upper / length;
            double lowerFraction = (double)lower / length;
            double digitFraction = (double)digitOrSlash / length;
            double balance = Math.Abs(upperFraction - lowerFraction);

            bool structural = balance <= MaxCaseBalanceStructuralBranch &&
                              upperFraction >= MinClassFractionStructuralBranch &&
                              lowerFraction >= MinClassFractionStructuralBranch &&
                              digitFraction >= MinDigitFractionStructuralBranch;
            if (structural) return true;

            double entropy = 0.0;
            foreach (KeyValuePair<char, int> entry in counts)
            {
                double probability = (double)entry.Value / length;
                entropy -= probability * Math.Log(probability, 2.0);
            }

            return entropy >= MinEntropyForEntropyBranch && balance <= MaxCaseBalanceForEntropyBranch;
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
                "pnpm-lock.yml",
                // Extractor bundle manifests carry per-file/bundle SHA-256 digests (Sha256, BundleSha256,
                // SourceTreeSha256). Still gated to a genuine 64-hex value below, so no real secret is exempted.
                "manifest.json"
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
                    if (RuleFiresOnLine(rule, pattern, line))
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
