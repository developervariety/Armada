namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// The one rule that binds a captain to a subscription account login. Launch, validation, usage status, and
    /// collectors all call it, so the per-runtime switch and the login check are defined once.
    /// </summary>
    public static class CaptainAccountLaunch
    {
        #region Public-Members

        /// <summary>The account's login home does not exist.</summary>
        public const string ReasonHomeMissing = "account_home_missing";

        /// <summary>The account's login home has no login file for its runtime.</summary>
        public const string ReasonLoginMissing = "account_login_missing";

        /// <summary>The Cursor account's named key variable is unset or empty on the server.</summary>
        public const string ReasonCredentialUnavailable = "account_launch_credential_unavailable";

        /// <summary>The captain's runtime differs from the account's runtime.</summary>
        public const string ReasonRuntimeMismatch = "account_runtime_mismatch";

        /// <summary>The captain carries its own provider key or base URL, so an account login must not replace it.</summary>
        public const string ReasonProviderCaptain = "account_provider_captain_conflict";

        /// <summary>Claude Code config directory switch.</summary>
        public const string ClaudeConfigDirVariable = "CLAUDE_CONFIG_DIR";

        /// <summary>Codex home switch.</summary>
        public const string CodexHomeVariable = "CODEX_HOME";

        /// <summary>OpenCode data home switch.</summary>
        public const string OpenCodeDataHomeVariable = "XDG_DATA_HOME";

        /// <summary>Cursor API key switch.</summary>
        public const string CursorApiKeyVariable = "CURSOR_API_KEY";

        #endregion

        #region Private-Members

        private static readonly Regex _VariableName = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>Runtimes that support a per-account login.</summary>
        public static bool IsSupportedRuntime(AgentRuntimeEnum runtime)
        {
            return runtime == AgentRuntimeEnum.ClaudeCode || runtime == AgentRuntimeEnum.Codex
                || runtime == AgentRuntimeEnum.OpenCode || runtime == AgentRuntimeEnum.Cursor;
        }

        /// <summary>True when the account changes how its captains authenticate.</summary>
        public static bool HasLaunchIdentity(UsageAccountSettings? account)
        {
            return account != null && account.Runtime.HasValue
                && (!String.IsNullOrWhiteSpace(account.HomeDirectory) || !String.IsNullOrWhiteSpace(account.LaunchCredentialEnv));
        }

        /// <summary>Find the account that lists a captain, or null.</summary>
        public static UsageAccountSettings? FindAccount(UsageRoutingSettings? settings, string? captainId)
        {
            if (settings?.Accounts == null || String.IsNullOrWhiteSpace(captainId)) return null;
            return settings.Accounts.FirstOrDefault(a => a?.CaptainIds != null && a.CaptainIds.Contains(captainId, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>Reject account launch fields that cannot describe one runtime login.</summary>
        public static void ValidateAccount(UsageAccountSettings account)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            bool hasHome = !String.IsNullOrWhiteSpace(account.HomeDirectory);
            bool hasKey = !String.IsNullOrWhiteSpace(account.LaunchCredentialEnv);
            if (!account.Runtime.HasValue)
            {
                if (hasHome || hasKey) throw new ArgumentException("Usage account " + account.Id + " sets a login home or key variable without a runtime.");
                return;
            }
            AgentRuntimeEnum runtime = account.Runtime.Value;
            if (!IsSupportedRuntime(runtime)) throw new ArgumentException("Usage account " + account.Id + " runtime must be ClaudeCode, Codex, OpenCode, or Cursor.");
            if (runtime == AgentRuntimeEnum.Cursor)
            {
                // Replacing Cursor's home would hide git, gh, and ssh configuration; its switch is the key variable.
                if (hasHome) throw new ArgumentException("Usage account " + account.Id + " is a Cursor account; use launchCredentialEnv, not homeDirectory.");
                if (hasKey && !_VariableName.IsMatch(account.LaunchCredentialEnv!)) throw new ArgumentException("Usage account " + account.Id + " launchCredentialEnv must be an environment variable name, not a key.");
                return;
            }
            if (hasKey) throw new ArgumentException("Usage account " + account.Id + " launchCredentialEnv applies only to Cursor accounts.");
            if (hasHome && !Path.IsPathFullyQualified(account.HomeDirectory!)) throw new ArgumentException("Usage account " + account.Id + " homeDirectory must be an absolute path.");
        }

        /// <summary>
        /// Reject accounts whose runtime differs from a listed captain, and accounts that would change the login of a
        /// captain carrying its own provider key or base URL. Unknown captain IDs are ignored here; preview warns.
        /// </summary>
        public static void ValidateCaptainBindings(UsageRoutingSettings settings, IEnumerable<Captain> captains)
        {
            if (settings?.Accounts == null || captains == null) return;
            Dictionary<string, Captain> byId = new Dictionary<string, Captain>(StringComparer.OrdinalIgnoreCase);
            foreach (Captain captain in captains) if (captain?.Id != null) byId[captain.Id] = captain;
            foreach (UsageAccountSettings account in settings.Accounts)
            {
                if (account?.CaptainIds == null) continue;
                foreach (string captainId in account.CaptainIds)
                {
                    if (!byId.TryGetValue(captainId, out Captain? captain)) continue;
                    if (account.Runtime.HasValue && captain.Runtime != account.Runtime.Value)
                        throw new ArgumentException("Usage account " + account.Id + " runtime " + account.Runtime.Value + " does not match captain " + captainId + " runtime " + captain.Runtime + ".");
                    if (HasLaunchIdentity(account) && (!String.IsNullOrWhiteSpace(captain.ApiKey) || !String.IsNullOrWhiteSpace(captain.ApiBaseUrl)))
                        throw new ArgumentException("Usage account " + account.Id + " sets a login, but captain " + captainId + " carries its own provider key or base URL.");
                }
            }
        }

        /// <summary>
        /// Check that the account's login is present. Returns null when ready or when the account has no login
        /// binding; otherwise a safe reason code. This checks presence only; the runtime itself decides validity.
        /// </summary>
        public static string? CheckReadiness(UsageAccountSettings account, Func<string, string?>? readEnvironment = null)
        {
            if (!HasLaunchIdentity(account)) return null;
            Func<string, string?> read = readEnvironment ?? Environment.GetEnvironmentVariable;
            AgentRuntimeEnum runtime = account.Runtime!.Value;
            if (runtime == AgentRuntimeEnum.Cursor)
                return String.IsNullOrWhiteSpace(account.LaunchCredentialEnv) || String.IsNullOrWhiteSpace(read(account.LaunchCredentialEnv!)) ? ReasonCredentialUnavailable : null;
            string home = account.HomeDirectory!;
            if (!Directory.Exists(home)) return ReasonHomeMissing;
            string? login = LoginFilePath(account);
            return login != null && !File.Exists(login) ? ReasonLoginMissing : null;
        }

        /// <summary>The runtime's login file inside the account home, or null when the runtime has none.</summary>
        public static string? LoginFilePath(UsageAccountSettings account)
        {
            if (account?.Runtime == null || String.IsNullOrWhiteSpace(account.HomeDirectory)) return null;
            return account.Runtime.Value switch
            {
                AgentRuntimeEnum.ClaudeCode => Path.Combine(account.HomeDirectory, ".credentials.json"),
                AgentRuntimeEnum.Codex => Path.Combine(account.HomeDirectory, "auth.json"),
                AgentRuntimeEnum.OpenCode => Path.Combine(account.HomeDirectory, "opencode", "auth.json"),
                _ => null
            };
        }

        /// <summary>
        /// Environment a captain launches with for its account. Empty when the captain has no account or the account
        /// has no login binding, so such captains launch exactly as before. Throws a named
        /// <see cref="CaptainAccountLaunchException"/> rather than fall back to the shared login.
        /// </summary>
        public static Dictionary<string, string> BuildEnvironment(AgentRuntimeEnum captainRuntime, UsageAccountSettings? account, Func<string, string?>? readEnvironment = null)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!HasLaunchIdentity(account)) return result;
            if (account!.Runtime!.Value != captainRuntime) throw new CaptainAccountLaunchException(ReasonRuntimeMismatch, account.Id);
            string? notReady = CheckReadiness(account, readEnvironment);
            if (notReady != null) throw new CaptainAccountLaunchException(notReady, account.Id);
            Func<string, string?> read = readEnvironment ?? Environment.GetEnvironmentVariable;
            switch (captainRuntime)
            {
                case AgentRuntimeEnum.ClaudeCode: result[ClaudeConfigDirVariable] = account.HomeDirectory!; break;
                case AgentRuntimeEnum.Codex: result[CodexHomeVariable] = account.HomeDirectory!; break;
                case AgentRuntimeEnum.OpenCode: result[OpenCodeDataHomeVariable] = account.HomeDirectory!; break;
                case AgentRuntimeEnum.Cursor: result[CursorApiKeyVariable] = read(account.LaunchCredentialEnv!)!.Trim(); break;
                default: throw new CaptainAccountLaunchException(ReasonRuntimeMismatch, account.Id);
            }
            return result;
        }

        #endregion
    }
}
