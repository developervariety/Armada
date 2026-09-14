namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Removes model-provider credentials and agent-session variables from a test process, so nothing the
    /// process or its children run can read the developer's provider access. Every test entry point calls
    /// <see cref="RemoveProviderVariables"/> first. A test that asserts on inherited provider variables sets
    /// them itself.
    /// </summary>
    public static class TestProcessEnvironment
    {
        #region Public-Members

        /// <summary>
        /// Set to <c>1</c> or <c>true</c> to keep the provider variables for a deliberate real-runtime run.
        /// </summary>
        public const string KeepVariable = "ARMADA_TEST_KEEP_PROVIDER_ENVIRONMENT";

        /// <summary>
        /// Name prefixes of removed variables.
        /// </summary>
        public static readonly IReadOnlyList<string> Prefixes = new List<string>
        {
            "ANTHROPIC_",
            "OPENAI_",
            "AZURE_OPENAI_",
            "CLAUDE_CODE_",
            "CODEX_",
            "CURSOR_",
            "GEMINI_",
            "OPENCODE_",
            "OPENROUTER_",
            "DEEPSEEK_"
        };

        /// <summary>
        /// Exact names of removed variables that no prefix covers.
        /// </summary>
        public static readonly IReadOnlyList<string> Names = new List<string>
        {
            "CLAUDECODE",
            "GOOGLE_API_KEY",
            "MISTRAL_API_KEY"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a variable name is a provider credential or agent-session variable.
        /// </summary>
        /// <param name="name">Variable name.</param>
        /// <returns>True when the variable is removed from test processes.</returns>
        public static bool IsProviderVariable(string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            string upper = name.ToUpperInvariant();
            return Names.Contains(upper, StringComparer.Ordinal)
                || Prefixes.Any(p => upper.StartsWith(p, StringComparison.Ordinal));
        }

        /// <summary>
        /// Whether the caller asked to keep provider variables.
        /// </summary>
        /// <returns>True when <see cref="KeepVariable"/> is set to 1 or true.</returns>
        public static bool KeepRequested()
        {
            string? value = Environment.GetEnvironmentVariable(KeepVariable);
            return String.Equals(value, "1", StringComparison.Ordinal)
                || String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Remove every provider variable from this process unless <see cref="KeepVariable"/> is set.
        /// </summary>
        /// <returns>Sorted names of the removed variables. Values are never returned.</returns>
        public static List<string> RemoveProviderVariables()
        {
            List<string> removed = new List<string>();
            if (KeepRequested()) return removed;

            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string name = entry.Key.ToString() ?? "";
                if (!IsProviderVariable(name)) continue;
                Environment.SetEnvironmentVariable(name, null);
                removed.Add(name);
            }

            removed.Sort(StringComparer.Ordinal);
            return removed;
        }

        /// <summary>
        /// Remove the provider variables and print one line saying how many were removed, or that they were kept.
        /// </summary>
        public static void RemoveProviderVariablesAndReport()
        {
            if (KeepRequested())
            {
                Console.WriteLine("Provider environment kept (" + KeepVariable + " is set).");
                return;
            }

            List<string> removed = RemoveProviderVariables();
            Console.WriteLine("Provider environment removed from the test process: " + removed.Count
                + (removed.Count == 0 ? "" : " (" + String.Join(", ", removed) + ")"));
        }

        #endregion
    }
}
