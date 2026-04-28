namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
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
        private static readonly (string Rule, Regex Pattern)[] _Rules = new (string, Regex)[]
        {
            ("CORE_RULE_2_mocking_lib", new Regex(@"using\s+(Moq|NSubstitute|FakeItEasy|Rhino\.Mocks|JustMock|Moq\.Protected|NSubstitute\.Extensions)\b", RegexOptions.Compiled)),
            ("CORE_RULE_4_log_interpolation", new Regex(@"\.(LogInformation|LogDebug|LogWarning|LogError|LogTrace|LogCritical)\s*\(\s*\$""", RegexOptions.Compiled)),
            ("CORE_RULE_5_private_key", new Regex(@"-----BEGIN (RSA |EC )?PRIVATE KEY-----", RegexOptions.Compiled)),
            ("CORE_RULE_5_base64_chunk", new Regex(@"""[A-Za-z0-9+/]{40,}={0,2}""", RegexOptions.Compiled)),
            ("CORE_RULE_5_password_literal", new Regex(@"password\s*[:=]\s*""\w{8,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("CORE_RULE_5_apikey_literal", new Regex(@"api_?key\s*[:=]\s*""\w{16,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("CORE_RULE_5_bearer_literal", new Regex(@"bearer\s+[A-Za-z0-9._~-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("CORE_RULE_12_spec_plan_ref", new Regex(@"(see plan|per the.*(spec|plan)|tracked in TODO|superpowers/(plans|specs)|TODO\.md)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        };

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
