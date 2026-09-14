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
    /// - Mux: a scoped MUX_CONFIG_DIR containing mcp-servers.json.
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
        /// Build the isolation plan for a runtime. Returns an empty plan (nothing to apply) when isolation
        /// cannot be expressed for the runtime or when the MCP port is invalid.
        /// </summary>
        /// <param name="runtime">The captain's runtime.</param>
        /// <param name="mcpPort">The Admiral MCP port (must be positive).</param>
        /// <param name="scopedConfigDirectory">Absolute path to the per-launch scoped configuration directory.</param>
        /// <returns>The isolation plan; never null.</returns>
        public static CaptainLaunchIsolationPlan Plan(AgentRuntimeEnum runtime, int mcpPort, string scopedConfigDirectory)
        {
            CaptainLaunchIsolationPlan plan = new CaptainLaunchIsolationPlan();
            if (mcpPort <= 0 || mcpPort > 65535) return plan;
            if (String.IsNullOrWhiteSpace(scopedConfigDirectory)) return plan;

            switch (runtime)
            {
                case AgentRuntimeEnum.ClaudeCode:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile("armada-mcp.json", ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(mcpPort)));
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
                        break;
                    }
                case AgentRuntimeEnum.Gemini:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile(Path.Combine(".gemini", "settings.json"), ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(mcpPort)));
                        ApplyHomeOverride(plan, scopedConfigDirectory);
                        break;
                    }
                case AgentRuntimeEnum.Cursor:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile(Path.Combine(".cursor", "mcp.json"), ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(mcpPort)));
                        ApplyHomeOverride(plan, scopedConfigDirectory);
                        break;
                    }
                case AgentRuntimeEnum.OpenCode:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile("opencode.json", ArmadaMcpConfigBuilder.BuildOpenCodeMcpJson(mcpPort)));
                        break;
                    }
                case AgentRuntimeEnum.Mux:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile("mcp-servers.json", ArmadaMcpConfigBuilder.BuildMuxServersJson(mcpPort)));
                        plan.EnvironmentOverrides["MUX_CONFIG_DIR"] = scopedConfigDirectory;
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
        /// <returns>The same plan.</returns>
        /// <exception cref="CaptainAccountLaunchException">The account cannot supply its login.</exception>
        public static CaptainLaunchIsolationPlan ApplyAccount(CaptainLaunchIsolationPlan plan, Armada.Core.Models.Captain captain, Armada.Core.Settings.UsageAccountSettings? account, Func<string, string?>? readEnvironment = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (!CaptainAccountLaunch.HasLaunchIdentity(account)) return plan;
            // Captains that carry their own provider key or endpoint keep their launch unchanged.
            if (!String.IsNullOrWhiteSpace(captain.ApiKey) || !String.IsNullOrWhiteSpace(captain.ApiBaseUrl))
                throw new CaptainAccountLaunchException(CaptainAccountLaunch.ReasonProviderCaptain, account!.Id);
            foreach (System.Collections.Generic.KeyValuePair<string, string> pair in CaptainAccountLaunch.BuildEnvironment(captain.Runtime, account, readEnvironment))
                plan.EnvironmentOverrides[pair.Key] = pair.Value;
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
