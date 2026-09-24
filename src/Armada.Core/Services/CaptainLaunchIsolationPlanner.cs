namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using Armada.Core.Enums;

    /// <summary>
    /// Pure planner that, for a given runtime, produces the steps to launch a captain in an isolated agent
    /// configuration: what extra CLI arguments to append, what environment overrides to apply, and what
    /// scoped configuration files to write so the agent still reaches the Armada MCP server while being
    /// blocked from the host user's global settings.
    ///
    /// Strategy per runtime:
    /// - Claude Code: --strict-mcp-config + an injected --mcp-config file (strict ignores host servers, so
    ///   the Armada server must be supplied explicitly) plus --setting-sources project,local.
    /// - Codex: a per-process config override registers the Armada server without hiding the
    ///   captain's existing authentication and provider profiles behind a replacement CODEX_HOME.
    /// - Gemini / Cursor: a scoped HOME/USERPROFILE containing the client's settings file, so they
    ///   physically cannot read the host user's configuration.
    /// - Mux: --mcp-config naming a scoped servers file (whose auth object presents the launch credential as a
    ///   bearer token referenced by variable name) plus --strict-mcp-config. `mux print` loads MCP servers only
    ///   from --mcp-config, so the scoped file is the only MCP source, and the captain's own config directory
    ///   (--config-dir, or ~/.mux) still selects its endpoints and settings.
    ///
    /// Every plan puts a chosen MCP credential in the captain's environment, and each client references it
    /// by variable name, so the token never lands in a scoped configuration file. Both a mission launch and
    /// a chat launch carry a caller-scoped session token (the mission owner's or the chat caller's), never
    /// the admiral launch credential.
    ///
    /// A captain on a subscription account also receives that account's login switch through
    /// <see cref="ApplyAccount"/>, independent of MCP isolation.
    ///
    /// Side-effect free (writes nothing) so it can be unit tested in isolation; the caller materializes the
    /// returned files and applies the environment/arguments.
    /// </summary>
    public static class CaptainLaunchIsolationPlanner
    {
        #region Public-Methods

        /// <summary>
        /// Build the isolation plan for a runtime with a chosen MCP credential. The configuration files reference
        /// the credential's environment variable by name; the environment carries the value only when the
        /// credential has one, so an unauthenticated chat turn writes the same configuration but presents no
        /// credential and reaches no MCP tool. Returns an empty plan when isolation cannot be expressed for the
        /// runtime or when the MCP port is invalid.
        /// </summary>
        /// <param name="runtime">The captain's runtime.</param>
        /// <param name="mcpPort">The Admiral MCP port (must be positive).</param>
        /// <param name="scopedConfigDirectory">Absolute path to the per-launch scoped configuration directory.</param>
        /// <param name="credential">The MCP credential the launch references; its value never lands in a file.</param>
        /// <returns>The isolation plan; never null.</returns>
        public static CaptainLaunchIsolationPlan Plan(AgentRuntimeEnum runtime, int mcpPort, string scopedConfigDirectory, McpCredentialReference credential)
        {
            if (credential == null) throw new ArgumentNullException(nameof(credential));

            CaptainLaunchIsolationPlan plan = new CaptainLaunchIsolationPlan();
            if (mcpPort <= 0 || mcpPort > 65535) return plan;
            if (String.IsNullOrWhiteSpace(scopedConfigDirectory)) return plan;

            // The Armada MCP endpoint refuses a request without credentials. A launched captain carries the
            // chosen credential's value in its environment, and the configuration files below reference it by
            // variable name in each client's own syntax. A credential with no value (an unauthenticated chat
            // turn) leaves the variable unset, so the runtime presents nothing and the endpoint refuses it.
            if (credential.HasToken)
                plan.EnvironmentOverrides[credential.EnvironmentVariable] = credential.Token;

            switch (runtime)
            {
                case AgentRuntimeEnum.ClaudeCode:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile("armada-mcp.json", ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(mcpPort, ArmadaMcpConfigBuilder.AuthorizationDollarBrace(credential.EnvironmentVariable))));
                        string mcpConfigPath = Path.Combine(scopedConfigDirectory, "armada-mcp.json");
                        plan.ExtraArguments.Add("--setting-sources");
                        plan.ExtraArguments.Add("project,local");
                        plan.ExtraArguments.Add("--strict-mcp-config");
                        plan.ExtraArguments.Add("--mcp-config");
                        plan.ExtraArguments.Add(mcpConfigPath);
                        break;
                    }
                case AgentRuntimeEnum.Codex:
                    {
                        plan.ExtraArguments.Add("-c");
                        plan.ExtraArguments.Add("mcp_servers.armada.url=\"" + ArmadaMcpConfigBuilder.GetMcpUrl(mcpPort) + "\"");
                        plan.ExtraArguments.Add("-c");
                        plan.ExtraArguments.Add("mcp_servers.armada.bearer_token_env_var=\"" + credential.EnvironmentVariable + "\"");
                        break;
                    }
                case AgentRuntimeEnum.Gemini:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile(Path.Combine(".gemini", "settings.json"), ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(mcpPort, ArmadaMcpConfigBuilder.AuthorizationDollarBrace(credential.EnvironmentVariable))));
                        ApplyHomeOverride(plan, scopedConfigDirectory);
                        break;
                    }
                case AgentRuntimeEnum.Cursor:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile(Path.Combine(".cursor", "mcp.json"), ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(mcpPort, ArmadaMcpConfigBuilder.AuthorizationCursor(credential.EnvironmentVariable))));
                        ApplyHomeOverride(plan, scopedConfigDirectory);
                        break;
                    }
                case AgentRuntimeEnum.OpenCode:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile("opencode.json", ArmadaMcpConfigBuilder.BuildOpenCodeMcpJson(mcpPort, ArmadaMcpConfigBuilder.AuthorizationOpenCode(credential.EnvironmentVariable))));
                        break;
                    }
                case AgentRuntimeEnum.Mux:
                    {
                        // The plan never replaces the Mux config directory: that directory holds the captain's
                        // endpoints, and a flag-selected directory outranks the environment variable anyway.
                        plan.FilesToWrite.Add(new IsolationConfigFile(MuxCommandBuilder.ScopedMcpConfigFileName, ArmadaMcpConfigBuilder.BuildMuxServersJson(mcpPort, credential.EnvironmentVariable)));
                        plan.ExtraArguments.AddRange(MuxCommandBuilder.BuildStrictMcpConfigArguments(Path.Combine(scopedConfigDirectory, MuxCommandBuilder.ScopedMcpConfigFileName)));
                        break;
                    }
                default:
                    break;
            }

            return plan;
        }

        /// <summary>
        /// Add a captain's subscription account login switch to a launch plan: CLAUDE_CONFIG_DIR, CODEX_HOME,
        /// XDG_DATA_HOME, or CURSOR_API_KEY. The switch is always the account's own home or key, never a scoped
        /// directory, so the runtime keeps that account's login and provider profiles. A captain with no account, or an
        /// account with no login binding, leaves the plan unchanged.
        /// </summary>
        /// <param name="plan">Plan to extend; returned for chaining.</param>
        /// <param name="captain">Captain being launched.</param>
        /// <param name="account">The captain's account, or null.</param>
        /// <param name="readEnvironment">Reads a server environment variable; defaults to the process environment.</param>
        /// <param name="requireAccountLogin">When true, a supported-runtime captain with no account login is refused.</param>
        /// <returns>The same plan.</returns>
        /// <exception cref="CaptainAccountLaunchException">The account cannot supply its login, or an account is required and missing.</exception>
        public static CaptainLaunchIsolationPlan ApplyAccount(CaptainLaunchIsolationPlan plan, Armada.Core.Models.Captain captain, Armada.Core.Settings.UsageAccountSettings? account, Func<string, string?>? readEnvironment = null, bool requireAccountLogin = false)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            CaptainAccountLaunch.RequireLaunchIdentity(captain, account, requireAccountLogin);
            if (!CaptainAccountLaunch.HasLaunchIdentity(account)) return plan;
            // Captains that carry their own provider key or endpoint keep their launch unchanged.
            if (!String.IsNullOrWhiteSpace(captain.ApiKey) || !String.IsNullOrWhiteSpace(captain.ApiBaseUrl))
                throw new CaptainAccountLaunchException(CaptainAccountLaunch.ReasonProviderCaptain, account!.Id);
            foreach (System.Collections.Generic.KeyValuePair<string, string> pair in CaptainAccountLaunch.BuildEnvironment(captain.Runtime, account, readEnvironment))
                plan.EnvironmentOverrides[pair.Key] = pair.Value;
            return plan;
        }

        /// <summary>
        /// Apply the captain's usage-routing account (and the requireAccountLogin setting) to a launch plan.
        /// </summary>
        public static CaptainLaunchIsolationPlan ApplyAccountFromSettings(CaptainLaunchIsolationPlan plan, Armada.Core.Models.Captain captain, Armada.Core.Settings.UsageRoutingSettings? routing, Func<string, string?>? readEnvironment = null)
        {
            Armada.Core.Settings.UsageAccountSettings? account = CaptainAccountLaunch.FindAccount(routing, captain.Id);
            return ApplyAccount(plan, captain, account, readEnvironment, routing?.RequireAccountLogin == true);
        }

        /// <summary>
        /// The per-launch scoped configuration directory of one mission captain start.
        /// </summary>
        /// <param name="logDirectory">Armada log directory.</param>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <returns>Absolute directory path.</returns>
        public static string MissionScopedDirectory(string logDirectory, string missionId, string captainId)
        {
            if (String.IsNullOrWhiteSpace(logDirectory)) throw new ArgumentNullException(nameof(logDirectory));
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentNullException(nameof(missionId));
            if (String.IsNullOrWhiteSpace(captainId)) throw new ArgumentNullException(nameof(captainId));
            return Path.Combine(logDirectory, "runtime-config", missionId, captainId);
        }

        /// <summary>
        /// Build the launch plan for one captain start with the mission's scoped MCP credential. When dock MCP
        /// delivery is enabled, Claude Code, Codex and Mux receive their scoped MCP configuration, and every
        /// runtime receives the credential value, because the dock configuration seeded for Cursor, Gemini and
        /// OpenCode references it by variable name. The credential is the mission owner's scoped session token,
        /// never the admiral launch credential, so a mission never gains global admin; a credential with no
        /// value (an unresolved owner) leaves the variable unset, so the launch presents nothing and the
        /// endpoint refuses it, failing closed rather than falling back to global admin.
        /// </summary>
        /// <param name="runtime">Captain runtime.</param>
        /// <param name="seedDockMcpConfig">Whether dock and launch MCP delivery is enabled.</param>
        /// <param name="mcpPort">Local Armada MCP port.</param>
        /// <param name="scopedConfigDirectory">Per-launch scoped configuration directory.</param>
        /// <param name="credential">The mission's scoped MCP credential; its value never lands in a file.</param>
        /// <returns>The plan; empty when nothing applies.</returns>
        public static CaptainLaunchIsolationPlan PlanForLaunch(AgentRuntimeEnum runtime, bool seedDockMcpConfig, int mcpPort, string scopedConfigDirectory, McpCredentialReference credential)
        {
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            if (!seedDockMcpConfig) return new CaptainLaunchIsolationPlan();
            bool scoped = runtime == AgentRuntimeEnum.ClaudeCode || runtime == AgentRuntimeEnum.Codex || runtime == AgentRuntimeEnum.Mux;
            CaptainLaunchIsolationPlan plan = scoped ? Plan(runtime, mcpPort, scopedConfigDirectory, credential) : new CaptainLaunchIsolationPlan();
            // The endpoint refuses a request without credentials, so a runtime that reads only its dock
            // configuration still needs the credential that configuration names. A credential with no value
            // leaves the variable unset (fail closed): the launch presents nothing and the endpoint refuses it.
            if (credential.HasToken)
                plan.EnvironmentOverrides[credential.EnvironmentVariable] = credential.Token;
            return plan;
        }

        #endregion

        #region Private-Methods

        private static void ApplyHomeOverride(CaptainLaunchIsolationPlan plan, string scopedConfigDirectory)
        {
            // HOME is honored on POSIX; USERPROFILE and HOMEPATH cover Windows CLIs that resolve the user
            // profile. Setting all three makes the scoped directory the effective home regardless of OS.
            plan.EnvironmentOverrides["HOME"] = scopedConfigDirectory;
            plan.EnvironmentOverrides["USERPROFILE"] = scopedConfigDirectory;
        }

        #endregion
    }
}
