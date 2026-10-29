namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Protocol;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Server.Ask;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// CLI tool permission policy resolution (Ask turns and missions, with the legacy auto-approve mapping and the
    /// ApproveInArmada fallbacks), the flags each runtime gets for Refuse, ApproveInArmada, and Bypass, and the typed
    /// permission denial report of Claude Code's stream-json result event.
    /// </summary>
    public sealed class CliPermissionPolicySuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.CliPermissionPolicy";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("run_approve_all_precedes_vessel_and_captain", "Explicit run approval bypasses vessel/captain defaults without modifying either", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                Captain captain = new Captain("configured-refuse") { CliPermissionPolicy = CliPermissionPolicyEnum.Refuse, Runtime = AgentRuntimeEnum.ApiEndpoint };
                Vessel vessel = new Vessel("vessel", "https://example.com/repo.git") { AutoApprove = false };
                CliPermissionResolution result = CliPermissionPolicyResolver.ResolveForMission(settings, captain, vessel, true, true, false, new RunApprovalOptions { ApproveAll = true });
                AssertEqual(CliPermissionPolicyEnum.Bypass, result.Effective);
                AssertEqual(CliPermissionPolicySourceEnum.RunApproval, result.Source);
                AssertEqual(CliPermissionPolicyEnum.Refuse, captain.CliPermissionPolicy!.Value);
                AssertFalse(vessel.AutoApprove!.Value);
                AssertThrows<UnauthorizedAccessException>(() => new RunApprovalOptions { ApproveAll = true }.ValidateFor(new AuthContext { IsAuthenticated = true }));
                AssertThrows<UnauthorizedAccessException>(() => new RunApprovalOptions { TimeoutDecision = ApprovalTimeoutDecisionEnum.Approve }.ValidateFor(new AuthContext { IsAuthenticated = true }));
                AssertThrows<ArgumentException>(() => new RunApprovalOptions { TimeoutSeconds = 0 }.Snapshot());
            }));

            cases.Add(Case("ask_defaults_to_approve_in_armada", "With no overrides an Ask turn uses Permissions.AskDefaultPolicy (ApproveInArmada)", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, settings.Permissions.AskDefaultPolicy, "default Ask policy");
                AssertEqual(CliPermissionPolicyEnum.Bypass, settings.Permissions.MissionDefaultPolicy, "default mission policy keeps the previous behavior");
                AssertFalse(settings.Permissions.AllowOwnerApproval, "owner approval off by default");
                AssertEqual(600, settings.Permissions.PromptTimeoutSeconds);

                CliPermissionResolution r = CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), new Captain("c"), true);
                AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, r.Effective);
                AssertEqual(CliPermissionPolicySourceEnum.ServerDefault, r.Source);
                AssertNull(r.FallbackReason);
                AssertContains("Permissions.AskDefaultPolicy", r.Note);
            }));

            cases.Add(Case("ask_resolution_order", "Thread override, then captain policy, then legacy auto-approve (only with Ask.CaptainAutoApprove), then the default", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                Captain bypassCaptain = new Captain("b") { CliPermissionPolicy = CliPermissionPolicyEnum.Bypass };
                Captain refuseCaptain = new Captain("r") { CliPermissionPolicy = CliPermissionPolicyEnum.Refuse };
                Captain legacyOn = new Captain("l");
                Captain legacyOff = new Captain("lo") { RuntimeOptionsJson = "{\"autoApprove\":false}" };

                AskThread overridden = new AskThread { CliPermissionPolicy = CliPermissionPolicyEnum.Refuse };
                CliPermissionResolution thread = CliPermissionPolicyResolver.ResolveForAsk(settings, overridden, bypassCaptain, true);
                AssertEqual(CliPermissionPolicyEnum.Refuse, thread.Effective, "thread override wins");
                AssertEqual(CliPermissionPolicySourceEnum.AskThread, thread.Source);

                CliPermissionResolution captain = CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), refuseCaptain, true);
                AssertEqual(CliPermissionPolicyEnum.Refuse, captain.Effective, "captain policy");
                AssertEqual(CliPermissionPolicySourceEnum.Captain, captain.Source);

                CliPermissionResolution untrusted = CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), bypassCaptain, true);
                AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, untrusted.Effective, "a captain-level Bypass is not honored for Ask unless Ask.CaptainAutoApprove");
                AssertEqual(CliPermissionPolicySourceEnum.ServerDefault, untrusted.Source);
                AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), legacyOn, true).Effective, "legacy autoApprove ignored for Ask by default");

                settings.Ask.CaptainAutoApprove = true;
                AssertEqual(CliPermissionPolicyEnum.Bypass, CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), bypassCaptain, true).Effective, "captain Bypass honored with Ask.CaptainAutoApprove");
                CliPermissionResolution legacy = CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), legacyOn, true);
                AssertEqual(CliPermissionPolicyEnum.Bypass, legacy.Effective, "legacy autoApprove (absent = true) maps to Bypass");
                AssertEqual(CliPermissionPolicySourceEnum.CaptainAutoApprove, legacy.Source);
                AssertEqual(CliPermissionPolicyEnum.Refuse, CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), legacyOff, true).Effective, "legacy autoApprove false maps to Refuse");

                AskThread bypassThread = new AskThread { CliPermissionPolicy = CliPermissionPolicyEnum.Bypass };
                settings.Ask.CaptainAutoApprove = false;
                AssertEqual(CliPermissionPolicyEnum.Bypass, CliPermissionPolicyResolver.ResolveForAsk(settings, bypassThread, refuseCaptain, true).Effective, "an admin's thread Bypass is honored");
            }));

            cases.Add(Case("mission_resolution_order", "Vessel auto-approve, captain policy, captain legacy auto-approve, then the mission default; vessel false caps Bypass", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                Captain plain = new Captain("p");
                CliPermissionResolution def = CliPermissionPolicyResolver.ResolveForMission(settings, plain, null, true, false);
                AssertEqual(CliPermissionPolicyEnum.Bypass, def.Effective, "previous behavior: captains without autoApprove bypass");
                AssertEqual(CliPermissionPolicySourceEnum.ServerDefault, def.Source);

                settings.Permissions.MissionDefaultPolicy = CliPermissionPolicyEnum.ApproveInArmada;
                AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, CliPermissionPolicyResolver.ResolveForMission(settings, plain, null, true, false).Effective);

                Captain legacyOff = new Captain("lo") { RuntimeOptionsJson = "{\"autoApprove\":false}" };
                CliPermissionResolution legacy = CliPermissionPolicyResolver.ResolveForMission(settings, legacyOff, null, true, false);
                AssertEqual(CliPermissionPolicyEnum.Refuse, legacy.Effective, "explicit legacy false maps to Refuse");
                AssertEqual(CliPermissionPolicySourceEnum.CaptainAutoApprove, legacy.Source);
                Captain legacyOn = new Captain("ln") { RuntimeOptionsJson = "{\"autoApprove\":true}" };
                AssertEqual(CliPermissionPolicyEnum.Bypass, CliPermissionPolicyResolver.ResolveForMission(settings, legacyOn, null, true, false).Effective, "explicit legacy true maps to Bypass");

                Captain policy = new Captain("pol") { CliPermissionPolicy = CliPermissionPolicyEnum.ApproveInArmada, RuntimeOptionsJson = "{\"autoApprove\":true}" };
                CliPermissionResolution captain = CliPermissionPolicyResolver.ResolveForMission(settings, policy, null, true, false);
                AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, captain.Effective, "captain policy wins over legacy autoApprove");
                AssertEqual(CliPermissionPolicySourceEnum.Captain, captain.Source);

                Vessel allow = new Vessel { AutoApprove = true };
                Vessel deny = new Vessel { AutoApprove = false };
                CliPermissionResolution vesselOn = CliPermissionPolicyResolver.ResolveForMission(settings, policy, allow, true, false);
                AssertEqual(CliPermissionPolicyEnum.Bypass, vesselOn.Effective, "vessel auto-approve true is Bypass");
                AssertEqual(CliPermissionPolicySourceEnum.VesselAutoApprove, vesselOn.Source);
                CliPermissionResolution capped = CliPermissionPolicyResolver.ResolveForMission(settings, legacyOn, deny, true, false);
                AssertEqual(CliPermissionPolicyEnum.Refuse, capped.Effective, "vessel auto-approve false caps Bypass to Refuse");
                AssertEqual(CliPermissionPolicySourceEnum.VesselAutoApprove, capped.Source);
                AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, CliPermissionPolicyResolver.ResolveForMission(settings, policy, deny, true, false).Effective, "vessel false keeps a non-Bypass policy");
            }));

            cases.Add(Case("approve_in_armada_fallbacks", "ApproveInArmada runs as Refuse without a prompt hook, without a scoped token, or on a Harbor, with the reason", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.Permissions.MissionDefaultPolicy = CliPermissionPolicyEnum.ApproveInArmada;
                MuxCapabilities.VersionProbe = () => "mux v1.4.1";
                try
                {
                    foreach (AgentRuntimeEnum runtime in Enum.GetValues<AgentRuntimeEnum>())
                    {
                        Captain captain = new Captain("c") { Runtime = runtime };
                        CliPermissionResolution r = CliPermissionPolicyResolver.ResolveForMission(settings, captain, null, true, false);
                        bool hook = runtime == AgentRuntimeEnum.ClaudeCode || runtime == AgentRuntimeEnum.ApiEndpoint || runtime == AgentRuntimeEnum.Pi;
                        AssertEqual(hook, CliPermissionPolicyResolver.SupportsPromptHook(runtime), "hook support: " + runtime);
                        AssertEqual(hook ? CliPermissionPolicyEnum.ApproveInArmada : CliPermissionPolicyEnum.Refuse, r.Effective, "effective: " + runtime);
                        AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, r.Requested, "requested: " + runtime);
                        if (!hook)
                        {
                            AssertEqual(CliPermissionFallbackReasonEnum.RuntimeUnsupported, r.FallbackReason, "reason: " + runtime);
                            AssertContains("running as Refuse", r.Note);
                        }
                    }

                    Captain claude = new Captain("cc") { Runtime = AgentRuntimeEnum.ClaudeCode };
                    AssertEqual(CliPermissionFallbackReasonEnum.NoSessionToken, CliPermissionPolicyResolver.ResolveForMission(settings, claude, null, false, false).FallbackReason);
                    AssertEqual(CliPermissionFallbackReasonEnum.RemoteHarbor, CliPermissionPolicyResolver.ResolveForMission(settings, claude, null, true, true).FallbackReason);
                    AssertEqual(CliPermissionFallbackReasonEnum.NoSessionToken, CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), claude, false).FallbackReason);
                    Captain piCaptain = new Captain("pi") { Runtime = AgentRuntimeEnum.Pi };
                    AssertTrue(CliPermissionPolicyResolver.PromptsOverMcp(AgentRuntimeEnum.Pi), "the pi gate reaches cli_permission_prompt over the scoped MCP connection");
                    AssertEqual(CliPermissionFallbackReasonEnum.NoSessionToken, CliPermissionPolicyResolver.ResolveForMission(settings, piCaptain, null, false, false).FallbackReason, "the pi gate needs the scoped token");
                    AssertEqual(CliPermissionFallbackReasonEnum.RemoteHarbor, CliPermissionPolicyResolver.ResolveForMission(settings, piCaptain, null, true, true, false).FallbackReason, "a Harbor that cannot prompt falls back");
                    AssertNull(CliPermissionPolicyResolver.ResolveForMission(settings, piCaptain, null, true, true, true).FallbackReason, "a prompting Harbor runs the pi gate");
                    AssertNull(CliPermissionPolicyResolver.ResolveForMission(settings, new Captain("api") { Runtime = AgentRuntimeEnum.ApiEndpoint }, null, false, false).FallbackReason, "the in-process gate needs no token");
                    AssertEqual(CliPermissionFallbackReasonEnum.RemoteHarbor, CliPermissionPolicyResolver.ResolveForMission(settings, new Captain("api2") { Runtime = AgentRuntimeEnum.ApiEndpoint }, null, true, true).FallbackReason, "Harbor launches fall back");
                    settings.Permissions.MissionDefaultPolicy = CliPermissionPolicyEnum.Bypass;
                    AssertNull(CliPermissionPolicyResolver.ResolveForMission(settings, claude, null, false, true).FallbackReason, "only ApproveInArmada falls back");
                }
                finally
                {
                    MuxCapabilities.VersionProbe = null;
                }
            }));

            cases.Add(Case("mux_prompt_hook_follows_version", "Mux honors ApproveInArmada from 1.5.0 (with a scoped token, not on a Harbor); older or missing Mux falls back with a Mux-specific note", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.Permissions.MissionDefaultPolicy = CliPermissionPolicyEnum.ApproveInArmada;
                Captain mux = new Captain("mx") { Runtime = AgentRuntimeEnum.Mux };
                try
                {
                    MuxCapabilities.VersionProbe = () => "mux v1.5.0\n";
                    AssertTrue(CliPermissionPolicyResolver.SupportsPromptHook(AgentRuntimeEnum.Mux), "1.5.0 has the hook");
                    CliPermissionResolution ok = CliPermissionPolicyResolver.ResolveForMission(settings, mux, null, true, false);
                    AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, ok.Effective, "Mux 1.5.0 runs ApproveInArmada");
                    AssertNull(ok.FallbackReason, "no fallback");
                    AssertEqual(CliPermissionFallbackReasonEnum.NoSessionToken, CliPermissionPolicyResolver.ResolveForMission(settings, mux, null, false, false).FallbackReason, "Mux needs the scoped token");
                    AssertEqual(CliPermissionFallbackReasonEnum.NoSessionToken, CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), mux, false).FallbackReason, "Ask turns need it too");
                    AssertEqual(CliPermissionFallbackReasonEnum.RemoteHarbor, CliPermissionPolicyResolver.ResolveForMission(settings, mux, null, true, true).FallbackReason, "Harbor launches fall back");

                    MuxCapabilities.VersionProbe = () => "mux v2.0.3";
                    AssertTrue(CliPermissionPolicyResolver.SupportsPromptHook(AgentRuntimeEnum.Mux), "later versions have the hook");

                    MuxCapabilities.VersionProbe = () => "mux v1.4.9";
                    CliPermissionResolution old = CliPermissionPolicyResolver.ResolveForMission(settings, mux, null, true, false);
                    AssertEqual(CliPermissionPolicyEnum.Refuse, old.Effective, "older Mux refuses");
                    AssertEqual(CliPermissionFallbackReasonEnum.RuntimeUnsupported, old.FallbackReason);
                    AssertContains("Mux 1.5.0 or later", old.Note);

                    MuxCapabilities.VersionProbe = () => null;
                    AssertFalse(CliPermissionPolicyResolver.SupportsPromptHook(AgentRuntimeEnum.Mux), "missing Mux has no hook");
                }
                finally
                {
                    MuxCapabilities.VersionProbe = null;
                }

                AssertEqual(new Version(1, 5, 0), MuxCapabilities.ParseVersion("mux v1.5.0"));
                AssertEqual(new Version(12, 0, 7), MuxCapabilities.ParseVersion("Mux CLI 12.0.7 (build abc)"));
                AssertNull(MuxCapabilities.ParseVersion("mux"), "no version");
                AssertNull(MuxCapabilities.ParseVersion(null), "null output");
            }));

            cases.Add(Case("harbor_prompt_support", "On a Harbor that can prompt, Claude Code and Mux keep ApproveInArmada (with a token); API endpoints and Harbors that cannot fall back; an Ask turn counts a Harbor it may run on", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.Permissions.MissionDefaultPolicy = CliPermissionPolicyEnum.ApproveInArmada;
                Captain claude = new Captain("cc") { Runtime = AgentRuntimeEnum.ClaudeCode };
                Captain mux = new Captain("mx") { Runtime = AgentRuntimeEnum.Mux };
                Captain api = new Captain("api") { Runtime = AgentRuntimeEnum.ApiEndpoint };
                MuxCapabilities.VersionProbe = () => null;
                try
                {
                    AssertNull(CliPermissionPolicyResolver.ResolveForMission(settings, claude, null, true, true, true).FallbackReason, "Claude Code on a prompting Harbor");
                    CliPermissionResolution muxOnHarbor = CliPermissionPolicyResolver.ResolveForMission(settings, mux, null, true, true, true);
                    AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, muxOnHarbor.Effective, "Mux on a prompting Harbor, whatever the Admiral host has");
                    AssertEqual(CliPermissionFallbackReasonEnum.NoSessionToken, CliPermissionPolicyResolver.ResolveForMission(settings, mux, null, false, true, true).FallbackReason, "still needs the token");
                    AssertEqual(CliPermissionFallbackReasonEnum.RemoteHarbor, CliPermissionPolicyResolver.ResolveForMission(settings, api, null, true, true, true).FallbackReason, "API endpoints cannot prompt from a Harbor");
                    CliPermissionResolution cannot = CliPermissionPolicyResolver.ResolveForMission(settings, mux, null, true, true, false);
                    AssertEqual(CliPermissionFallbackReasonEnum.RemoteHarbor, cannot.FallbackReason, "a Harbor that cannot prompt");
                    AssertContains("install Mux 1.5.0", cannot.Note);

                    settings.Permissions.AskDefaultPolicy = CliPermissionPolicyEnum.ApproveInArmada;
                    AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), mux, true, true).Effective, "an Ask turn that may run on a prompting Harbor");
                    CliPermissionResolution nowhere = CliPermissionPolicyResolver.ResolveForAsk(settings, new AskThread(), mux, true, false);
                    AssertEqual(CliPermissionFallbackReasonEnum.RuntimeUnsupported, nowhere.FallbackReason, "no Mux 1.5.0 anywhere");
                    AssertContains("neither the Admiral host nor a connected Harbor", nowhere.Note);
                }
                finally
                {
                    MuxCapabilities.VersionProbe = null;
                }
            }));

            cases.Add(Case("prompt_timeout_is_clamped", "Permissions.PromptTimeoutSeconds is clamped to 10..3600", () =>
            {
                CliPermissionSettings p = new CliPermissionSettings();
                p.PromptTimeoutSeconds = 1;
                AssertEqual(10, p.PromptTimeoutSeconds);
                p.PromptTimeoutSeconds = 99999;
                AssertEqual(3600, p.PromptTimeoutSeconds);
                ArmadaSettings settings = new ArmadaSettings();
                settings.Permissions = null!;
                AssertNotNull(settings.Permissions, "null restores defaults");
            }));

            cases.Add(Case("claude_flags_per_policy", "Claude Code: Refuse is acceptEdits, ApproveInArmada adds --permission-prompt-tool and MCP_TOOL_TIMEOUT, Bypass skips permissions", () =>
            {
                Captain refuse = CaptainRuntimeOptions.WithEffectiveAutoApprove(new Captain("c"), false);
                InspectableClaude refuseRuntime = new InspectableClaude(Logging());
                AssertFalse(CliPermissionLaunch.Apply(refuseRuntime, CliPermissionPolicyEnum.Refuse, 600), "Refuse configures no prompt tool");
                List<string> refuseArgs = refuseRuntime.Args(refuse);
                AssertTrue(refuseArgs.Contains("acceptEdits"), "acceptEdits");
                AssertFalse(refuseArgs.Contains("--permission-prompt-tool"), "no prompt tool under Refuse");
                AssertFalse(refuseArgs.Contains("--dangerously-skip-permissions"));

                InspectableClaude approveRuntime = new InspectableClaude(Logging());
                AssertTrue(CliPermissionLaunch.Apply(approveRuntime, CliPermissionPolicyEnum.ApproveInArmada, 300), "ApproveInArmada configures the prompt tool");
                List<string> approveArgs = approveRuntime.Args(refuse);
                int at = approveArgs.IndexOf("--permission-prompt-tool");
                AssertTrue(at >= 0 && approveArgs[at + 1] == "mcp__armada__cli_permission_prompt", "prompt tool flag: " + String.Join(" ", approveArgs));
                AssertTrue(approveArgs.Contains("acceptEdits"), "edits still accepted");
                int allowed = approveArgs.IndexOf("--allowedTools");
                AssertTrue(allowed >= 0 && approveArgs[allowed + 1] == "mcp__armada", "Armada MCP tools stay allowed");
                Dictionary<string, string?> env = approveRuntime.Env(refuse);
                AssertEqual("420000", env.TryGetValue("MCP_TOOL_TIMEOUT", out string? t) ? t : null, "MCP tool timeout above the prompt timeout");

                Captain bypass = CaptainRuntimeOptions.WithEffectiveAutoApprove(new Captain("b") { RuntimeOptionsJson = "{\"autoApprove\":false}" }, true);
                List<string> bypassArgs = approveRuntime.Args(bypass);
                AssertTrue(bypassArgs.Contains("--dangerously-skip-permissions"), "Bypass");
                AssertFalse(bypassArgs.Contains("--permission-prompt-tool"), "no prompt tool with the bypass flag");
                AssertFalse(approveRuntime.Env(bypass).ContainsKey("MCP_TOOL_TIMEOUT"), "no timeout override with the bypass flag");
            }));

            cases.Add(Case("other_runtime_flags_per_policy", "Codex, Gemini, Cursor, OpenCode, and Mux map Refuse and Bypass onto their existing flags, with no prompt tool under either", () =>
            {
                Captain refuse = CaptainRuntimeOptions.WithEffectiveAutoApprove(new Captain("c"), false);
                Captain bypass = new Captain("b");

                InspectableCodex codex = new InspectableCodex(Logging());
                AssertFalse(CliPermissionLaunch.Apply(codex, CliPermissionPolicyEnum.ApproveInArmada, 600), "Codex has no prompt hook");
                List<string> codexRefuse = codex.Args(refuse);
                AssertTrue(codexRefuse.Contains("--sandbox") && codexRefuse.Contains("workspace-write"), "Codex Refuse sandboxes");
                AssertFalse(codexRefuse.Contains("--dangerously-bypass-approvals-and-sandbox"));
                AssertFalse(CliPermissionLaunch.Apply(codex, CliPermissionPolicyEnum.Bypass, 600), "Bypass has no prompt hook");
                AssertTrue(codex.Args(bypass).Contains("--dangerously-bypass-approvals-and-sandbox"), "Resolved Codex Bypass must not retain the workspace sandbox");
                AssertFalse(codex.Args(refuse).Contains("--dangerously-bypass-approvals-and-sandbox"), "Refuse never bypasses, whatever the mode");
                CliPermissionLaunch.Apply(codex, CliPermissionPolicyEnum.Refuse, 600);
                AssertEqual("full-auto", codex.ApprovalMode, "reusing a runtime clears a prior bypass mode");
                AssertTrue(codex.Args(refuse).Contains("workspace-write"), "Refuse stays sandboxed");

                InspectableGemini gemini = new InspectableGemini(Logging());
                AssertTrue(gemini.Args(refuse).Contains("auto_edit"), "Gemini Refuse is auto_edit");
                AssertTrue(gemini.Args(bypass).Contains("yolo"), "Gemini Bypass is yolo");

                InspectableCursor cursor = new InspectableCursor(Logging());
                AssertFalse(cursor.Args(refuse).Contains("--force"), "Cursor Refuse has no --force");
                AssertTrue(cursor.Args(bypass).Contains("--force"), "Cursor Bypass is --force");

                InspectableOpenCode openCode = new InspectableOpenCode(Logging());
                AssertFalse(openCode.Args(refuse).Contains("--auto"), "OpenCode Refuse has no --auto");
                AssertTrue(openCode.Args(bypass).Contains("--auto"), "OpenCode Bypass is --auto");

                InspectablePi pi = new InspectablePi(Logging());
                AssertTrue(CliPermissionLaunch.Apply(pi, CliPermissionPolicyEnum.ApproveInArmada, 600), "pi sends its prompts to Armada through the permission gate extension");
                List<string> piApprove = pi.Args(refuse);
                int extension = piApprove.IndexOf("--extension");
                AssertTrue(extension >= 0 && piApprove[extension + 1].EndsWith(PiRuntime.PermissionGateFileName, StringComparison.Ordinal), "pi ApproveInArmada loads the gate: " + String.Join(" ", piApprove));
                AssertFalse(piApprove.Contains("--exclude-tools"), "the gate decides shell calls instead of removing them");
                AssertFalse(CliPermissionLaunch.Apply(pi, CliPermissionPolicyEnum.Refuse, 600), "pi Refuse has no prompt hook");
                List<string> piRefuse = pi.Args(refuse);
                int excluded = piRefuse.IndexOf("--exclude-tools");
                AssertTrue(excluded >= 0 && piRefuse[excluded + 1] == "bash,powershell", "pi Refuse is --exclude-tools bash,powershell: " + String.Join(" ", piRefuse));
                AssertFalse(piRefuse.Contains("--extension"), "Refuse clears the gate of a reused runtime");
                AssertFalse(pi.Args(bypass).Contains("--exclude-tools"), "pi Bypass keeps every tool (pi never prompts)");

                ArmadaSettings askSettings = new ArmadaSettings();
                askSettings.Permissions.AskDefaultPolicy = CliPermissionPolicyEnum.ApproveInArmada;
                CliPermissionResolution piAsk = CliPermissionPolicyResolver.ResolveForAsk(askSettings, new AskThread(), new Captain("p") { Runtime = AgentRuntimeEnum.Pi }, true);
                AssertEqual(CliPermissionPolicyEnum.ApproveInArmada, piAsk.Effective, "pi ApproveInArmada is honored");
                AssertNull(piAsk.FallbackReason, "no RuntimeUnsupported fallback for pi");
                AssertContains("wait for an approver in Armada", piAsk.Note ?? String.Empty, "the user-visible note says approvals go to Armada");
                CliPermissionResolution piNoToken = CliPermissionPolicyResolver.ResolveForAsk(askSettings, new AskThread(), new Captain("p2") { Runtime = AgentRuntimeEnum.Pi }, false);
                AssertEqual(CliPermissionPolicyEnum.Refuse, piNoToken.Effective, "without a scoped token the gate cannot reach Armada, so pi runs as Refuse");
                AssertEqual(CliPermissionFallbackReasonEnum.NoSessionToken, piNoToken.FallbackReason);

                InspectableMux mux = new InspectableMux(Logging());
                Captain muxRefuse = CaptainRuntimeOptions.WithEffectiveAutoApprove(new Captain("m") { Runtime = AgentRuntimeEnum.Mux }, false);
                List<string> muxRefuseArgs = mux.Args(muxRefuse);
                int policy = muxRefuseArgs.IndexOf("--approval-policy");
                AssertTrue(policy >= 0 && muxRefuseArgs[policy + 1] == "deny", "Mux Refuse is --approval-policy deny: " + String.Join(" ", muxRefuseArgs));
                AssertTrue(mux.Args(new Captain("m2") { Runtime = AgentRuntimeEnum.Mux }).Contains("--yolo"), "Mux Bypass is --yolo");
                AssertFalse(CliPermissionLaunch.Apply(mux, CliPermissionPolicyEnum.Refuse, 600), "Mux Refuse configures no prompt tool");
                AssertFalse(mux.Args(muxRefuse).Contains("--permission-prompt-tool"), "no prompt tool under Refuse");
            }));

            cases.Add(Case("mux_flags_for_approve_in_armada", "Mux: ApproveInArmada runs ask with Armada's prompt tool, a timeout above Armada's, the auto-approved tools, and the workspace-write sandbox", () =>
            {
                Captain approve = CaptainRuntimeOptions.WithEffectiveAutoApprove(new Captain("m") { Runtime = AgentRuntimeEnum.Mux }, false);
                InspectableMux mux = new InspectableMux(Logging());
                MuxCapabilities.VersionProbe = () => "mux v1.4.1";
                try
                {
                    AssertFalse(CliPermissionLaunch.Apply(mux, CliPermissionPolicyEnum.ApproveInArmada, 300), "Mux before 1.5.0 gets no prompt tool");
                    AssertFalse(mux.Args(approve).Contains("--permission-prompt-tool"), "no prompt tool flag for an old Mux");
                    MuxCapabilities.VersionProbe = () => "mux v1.5.0";
                    AssertTrue(CliPermissionLaunch.Apply(mux, CliPermissionPolicyEnum.ApproveInArmada, 300), "ApproveInArmada configures the prompt tool");
                }
                finally
                {
                    MuxCapabilities.VersionProbe = null;
                }

                List<string> args = mux.Args(approve);
                string joined = String.Join(" ", args);
                AssertEqual("ask", ValueAfter(args, "--approval-policy"), "ask policy: " + joined);
                AssertEqual("armada.cli_permission_prompt", ValueAfter(args, "--permission-prompt-tool"), "prompt tool");
                AssertEqual("420", ValueAfter(args, "--permission-prompt-timeout"), "two minutes above the prompt timeout");
                AssertEqual(MuxPermissionPrompt.DefaultAutoApproveTools, ValueAfter(args, "--auto-approve-tools"), "auto-approved tools");
                AssertEqual("workspace-write", ValueAfter(args, "--sandbox"), "edits confined to the worktree");
                AssertFalse(args.Contains("--yolo"), "no --yolo");
                AssertEqual("p", args[args.Count - 1], "the prompt stays last");

                Captain captainAuto = CaptainRuntimeOptions.WithEffectiveAutoApprove(new Captain("m2") { Runtime = AgentRuntimeEnum.Mux, RuntimeOptionsJson = "{\"approvalPolicy\":\"auto\"}" }, false);
                AssertEqual("armada.cli_permission_prompt", ValueAfter(mux.Args(captainAuto), "--permission-prompt-tool"), "ApproveInArmada outranks a captain's auto override");
                Captain captainDeny = new Captain("m3") { Runtime = AgentRuntimeEnum.Mux, RuntimeOptionsJson = "{\"approvalPolicy\":\"deny\"}" };
                List<string> denyArgs = mux.Args(captainDeny);
                AssertEqual("deny", ValueAfter(denyArgs, "--approval-policy"), "a captain's deny stays stricter");
                AssertFalse(denyArgs.Contains("--permission-prompt-tool"), "no prompt tool under deny");

                AssertFalse(CliPermissionLaunch.Apply(mux, CliPermissionPolicyEnum.Bypass, 300), "Bypass clears the prompt tool");
                AssertFalse(mux.Args(new Captain("m4") { Runtime = AgentRuntimeEnum.Mux }).Contains("--permission-prompt-tool"), "a reused runtime drops the prompt tool");
            }));

            cases.Add(Case("stream_json_permission_denials_are_typed", "The result event's permission_denials parse into typed entries and mark the matching tool calls", () =>
            {
                string line = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\",\"permission_denials\":[{\"tool_name\":\"Bash\",\"tool_use_id\":\"toolu_1\",\"tool_input\":{\"command\":\"rm -rf build\"}}]}";
                AssertTrue(ClaudeStreamLine.TryParse(line, out ClaudeStreamLine? parsed), "parsed");
                AssertEqual(1, parsed!.PermissionDenials!.Count);
                ClaudePermissionDenial denial = parsed.PermissionDenials[0];
                AssertEqual("Bash", denial.ToolName);
                AssertEqual("toolu_1", denial.ToolUseId);
                AssertEqual("rm -rf build", JsonHelper.Deserialize<CliToolInput>(denial.ToolInput!).Command);

                ToolCallCollector collector = new ToolCallCollector();
                collector.Observe(new CaptainToolActivity { Phase = "started", Id = "toolu_1", Name = "Bash", Arguments = "{\"command\":\"rm -rf build\"}" });
                collector.Observe(new CaptainToolActivity { Phase = "completed", Id = "toolu_1", Ok = false, Result = "refused", ElapsedMs = 5 });
                collector.Observe(new CaptainToolActivity { Phase = "completed", Id = "toolu_1", Name = "Bash", Ok = false, PermissionDenied = true });
                collector.Observe(new CaptainToolActivity { Phase = "started", Id = "toolu_2", Name = "Read" });
                collector.Observe(new CaptainToolActivity { Phase = "completed", Id = "toolu_2", Ok = true, Result = "ok" });
                AskMessageToolCall refused = collector.ToList().Single(c => c.CallId == "toolu_1");
                AssertEqual(true, refused.PermissionDenied, "marked from the typed report");
                AssertEqual(false, refused.Ok);
                AssertEqual("refused", refused.ResultText, "the earlier result text is kept");
                AssertEqual(5L, refused.ElapsedMs, "timing kept");
                AssertNull(collector.ToList().Single(c => c.CallId == "toolu_2").PermissionDenied, "other calls unmarked");

                AssertTrue(ClaudeStreamLine.TryParse("{\"type\":\"result\",\"result\":\"x\"}", out ClaudeStreamLine? none), "parsed without denials");
                AssertNull(none!.PermissionDenials);
            }));

            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "CLI permission policy and runtime flags", cases: cases);
        }

        #endregion

        #region Private-Methods

        private static LoggingModule Logging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => { body(); return Task.CompletedTask; },
                tags: new List<string> { TestTags.Positive });
        }

        #endregion

        #region Nested-Types

        private static string? ValueAfter(List<string> args, string flag)
        {
            int at = args.IndexOf(flag);
            return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
        }

        private sealed class InspectableClaude : ClaudeCodeRuntime
        {
            public InspectableClaude(LoggingModule logging) : base(logging) { }

            public List<string> Args(Captain captain) => BuildArguments(Path.GetTempPath(), "p", null, null, captain);

            public Dictionary<string, string?> Env(Captain captain)
            {
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo("x");
                info.Environment.Remove("MCP_TOOL_TIMEOUT");
                ApplyEnvironment(info, captain);
                return new Dictionary<string, string?>(info.Environment);
            }
        }

        private sealed class InspectableCodex : CodexRuntime
        {
            public InspectableCodex(LoggingModule logging) : base(logging) { }

            public List<string> Args(Captain captain) => BuildArguments(Path.GetTempPath(), "p", null, null, captain);
        }

        private sealed class InspectableGemini : GeminiRuntime
        {
            public InspectableGemini(LoggingModule logging) : base(logging) { }

            public List<string> Args(Captain captain) => BuildArguments(Path.GetTempPath(), "p", null, null, captain);
        }

        private sealed class InspectableCursor : CursorRuntime
        {
            public InspectableCursor(LoggingModule logging) : base(logging) { }

            public List<string> Args(Captain captain) => BuildArguments(Path.GetTempPath(), "p", null, null, captain);
        }

        private sealed class InspectableOpenCode : OpenCodeRuntime
        {
            public InspectableOpenCode(LoggingModule logging) : base(logging) { }

            public List<string> Args(Captain captain) => BuildArguments(Path.GetTempPath(), "p", null, null, captain);
        }

        private sealed class InspectablePi : PiRuntime
        {
            public InspectablePi(LoggingModule logging) : base(logging) { }

            public List<string> Args(Captain captain) => BuildArguments(Path.GetTempPath(), "p", null, null, captain);
        }

        private sealed class InspectableMux : MuxRuntime
        {
            public InspectableMux(LoggingModule logging) : base(logging) { }

            public List<string> Args(Captain captain) => BuildArguments(Path.GetTempPath(), "p", null, null, captain);
        }

        #endregion
    }
}
