namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Settings;

    /// <summary>
    /// Decides which inherited environment variables a captain process must not receive. A captain can list its
    /// own environment (a shell <c>env</c>, <c>/proc/self/environ</c>), and everything it lists reaches its model
    /// provider, so an admiral secret is removed from the child environment before the launch adds the variables
    /// the captain needs. A variable the launch sets on purpose (the mission-scoped MCP token, a per-captain
    /// provider key) is applied after the scrub and is kept.
    /// </summary>
    /// <remarks>
    /// A name is an admiral secret when the operator configured it (a model provider's key variable, the
    /// typed-decision key variable, or an extra name in settings), or when it is in the admiral's own
    /// <c>ARMADA_</c> namespace and one of its underscore-separated parts names a credential (KEY, TOKEN,
    /// SECRET, PASSWORD, PASS, CREDENTIAL, CREDENTIALS). The <c>ARMADA_TEST_</c> namespace carries test
    /// fixture values to fake agents and is not scrubbed by the name rule.
    /// </remarks>
    public static class CaptainEnvironmentScrub
    {
        #region Private-Members

        private const string AdmiralPrefix = "ARMADA_";
        private const string TestPrefix = "ARMADA_TEST_";

        private static readonly HashSet<string> _CredentialParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KEY", "TOKEN", "SECRET", "PASSWORD", "PASS", "CREDENTIAL", "CREDENTIALS"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Collect the configured secret variable names from settings: every model provider's key variable, the
        /// typed-decision key variable, and the extra names in <see cref="ArmadaSettings.CaptainEnvironmentScrubNames"/>.
        /// </summary>
        /// <param name="settings">Admiral settings; null yields no names.</param>
        /// <returns>Distinct configured names.</returns>
        public static IReadOnlyCollection<string> ConfiguredNames(ArmadaSettings? settings)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (settings == null) return names;
            if (settings.ModelProviders != null)
            {
                foreach (ModelProviderSettings provider in settings.ModelProviders.Providers.Values)
                    AddName(names, provider?.ApiKeyEnv);
            }
            AddName(names, settings.TypedDecisions?.ApiKeyEnv);
            foreach (string name in settings.CaptainEnvironmentScrubNames)
                AddName(names, name);
            return names;
        }

        /// <summary>
        /// Whether a variable name is an admiral secret a captain must not inherit.
        /// </summary>
        /// <param name="name">Variable name.</param>
        /// <param name="configuredNames">Operator-configured secret names; may be null.</param>
        /// <returns>True when the variable must be removed from a captain environment.</returns>
        public static bool IsAdmiralSecret(string? name, IReadOnlyCollection<string>? configuredNames)
        {
            if (String.IsNullOrWhiteSpace(name)) return false;
            if (configuredNames != null)
            {
                foreach (string configured in configuredNames)
                    if (String.Equals(configured, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (!name.StartsWith(AdmiralPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith(TestPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string part in name.Substring(AdmiralPrefix.Length).Split('_', StringSplitOptions.RemoveEmptyEntries))
                if (_CredentialParts.Contains(part)) return true;
            return false;
        }

        /// <summary>
        /// Remove every admiral secret from a child environment.
        /// </summary>
        /// <param name="environment">Environment the child process will receive.</param>
        /// <param name="configuredNames">Operator-configured secret names; may be null.</param>
        /// <returns>The names removed, so the launch can record that a scrub happened (never the values).</returns>
        public static List<string> Scrub(IDictionary<string, string?> environment, IReadOnlyCollection<string>? configuredNames)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            List<string> removed = new List<string>();
            foreach (string name in new List<string>(environment.Keys))
            {
                if (!IsAdmiralSecret(name, configuredNames)) continue;
                environment.Remove(name);
                removed.Add(name);
            }
            return removed;
        }

        #endregion

        #region Private-Methods

        private static void AddName(HashSet<string> names, string? name)
        {
            if (!String.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
        }

        #endregion
    }
}
