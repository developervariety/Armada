namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.IO;
    using System.Net.Http;
    using System.Reflection;
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for runtime model validation and launch passthrough in AgentLifecycleHandler.
    /// </summary>
    public class AgentLifecycleHandlerTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Agent Lifecycle Handler";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            // ----------------------------------------------------------------
            // Mission MCP credential is scoped to the mission owner, never the
            // admiral launch credential (cross-tenant privilege escalation fix).
            // ----------------------------------------------------------------

            await RunTest("A tenant-admin-dispatched mission's captain is scoped to its own tenant, not global admin, and cannot reach another tenant", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // A tenant admin of tenant B dispatches a mission. Its captain must reach only tenant B's
                    // records with tenant B's own privileges, never the admiral launch credential (which maps
                    // to global admin in the default tenant and reaches every operator-only tool).
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    TenantMetadata tenantB = new TenantMetadata("Tenant B");
                    await testDb.Driver.Tenants.CreateAsync(tenantB).ConfigureAwait(false);
                    UserMaster adminB = new UserMaster(tenantB.Id, "admin-b@example.com", "password");
                    adminB.IsTenantAdmin = true;
                    adminB.IsAdmin = false;
                    await testDb.Driver.Users.CreateAsync(adminB).ConfigureAwait(false);

                    TenantMetadata tenantA = new TenantMetadata("Tenant A");
                    await testDb.Driver.Tenants.CreateAsync(tenantA).ConfigureAwait(false);

                    Mission mission = new Mission("Cross-tenant probe")
                    {
                        TenantId = tenantB.Id,
                        UserId = adminB.Id
                    };

                    McpCredentialReference credential = await handler.ResolveMissionMcpCredentialAsync(mission).ConfigureAwait(false);
                    AssertTrue(credential.HasToken, "the mission carries a scoped MCP credential");
                    AssertFalse(credential.Token!.StartsWith("armada-launch-", StringComparison.Ordinal),
                        "the mission credential never uses the retired launch-token format");

                    AuthenticationService auth = CreateAuthenticationService(testDb.Driver, tokens);
                    AuthContext ctx = await auth.AuthenticateAsync("Bearer " + credential.Token, null, null).ConfigureAwait(false);

                    AssertTrue(ctx.IsAuthenticated, "the mission credential authenticates");
                    AssertEqual(tenantB.Id, ctx.TenantId, "the mission is scoped to its owning tenant");
                    AssertEqual(adminB.Id, ctx.UserId, "the mission is scoped to its owning user");
                    AssertFalse(ctx.IsAdmin, "the mission never runs as a global admin, so operator-only tools are unreachable");
                    AssertFalse(string.Equals(ctx.TenantId, Armada.Core.Constants.DefaultTenantId, StringComparison.Ordinal) && ctx.IsAdmin,
                        "the mission never reaches the default tenant as global admin");
                    AssertFalse(string.Equals(ctx.TenantId, tenantA.Id, StringComparison.Ordinal),
                        "the mission cannot read another tenant's records: its scope is its own tenant");

                    string scopedDirectory = "/runner/checkout/.armada/runtime/captain-b";
                    CaptainLaunchIsolationPlan harborPlan = CaptainLaunchIsolationPlanner.PlanForHarbor(
                        AgentRuntimeEnum.ClaudeCode, "https://mcp.example.invalid/mcp", scopedDirectory, credential);
                    AssertEqual(credential.Token!, harborPlan.EnvironmentOverrides[McpLaunchCredential.EnvironmentVariable],
                        "the remote launch carries the authenticated mission owner's scoped token in its environment plan");
                    AssertEqual(1, harborPlan.FilesToWrite.Count, "Claude receives one isolated Harbor MCP config file");
                    string config = harborPlan.FilesToWrite[0].Contents;
                    AssertTrue(config.Contains("https://mcp.example.invalid/mcp", StringComparison.Ordinal),
                        "the runner config targets the explicitly configured HTTPS endpoint");
                    AssertTrue(config.Contains("${" + McpLaunchCredential.EnvironmentVariable + "}", StringComparison.Ordinal),
                        "the config references the scoped token by environment name");
                    AssertFalse(config.Contains(credential.Token!, StringComparison.Ordinal),
                        "the scoped token value is absent from the config written to the checkout");
                    AuthContext? remoteLaunchOwner = tokens.ValidateToken(harborPlan.EnvironmentOverrides[McpLaunchCredential.EnvironmentVariable]);
                    AssertNotNull(remoteLaunchOwner, "the Harbor launch token is a valid mission session token");
                    AssertEqual(adminB.Id, remoteLaunchOwner!.UserId, "the Harbor token retains the exact mission owner");
                }
            });

            await RunTest("Harbor MCP endpoint requires HTTPS except for loopback HTTP", async () =>
            {
                McpCredentialReference credential = McpCredentialReference.ForMission("scoped-test-token");
                string[] rejected = new[]
                {
                    "http://mcp.example.invalid/mcp",
                    "https://user:password@mcp.example.invalid/mcp",
                    "https://mcp.example.invalid/mcp?token=private",
                    "https://mcp.example.invalid/mcp#fragment",
                    "file:///tmp/mcp"
                };
                foreach (string endpoint in rejected)
                {
                    bool refused = false;
                    try
                    {
                        CaptainLaunchIsolationPlanner.PlanForHarbor(AgentRuntimeEnum.ClaudeCode, endpoint,
                            "/runner/checkout/.armada/runtime/captain", credential);
                    }
                    catch (InvalidOperationException exception)
                    {
                        refused = exception.Message == "harbor_captain_mcp_endpoint_invalid";
                    }
                    AssertTrue(refused, "endpoint is refused by name: " + endpoint);
                }

                CaptainLaunchIsolationPlan loopback = CaptainLaunchIsolationPlanner.PlanForHarbor(
                    AgentRuntimeEnum.ClaudeCode, "http://127.0.0.1:7891/mcp",
                    "/runner/checkout/.armada/runtime/captain", credential);
                AssertTrue(loopback.FilesToWrite[0].Contents.Contains("http://127.0.0.1:7891/mcp", StringComparison.Ordinal),
                    "HTTP remains available for an explicit loopback endpoint");
            });

            await RunTest("An autonomous mission runs with the objective owner's scope carried on its voyage, not global admin", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Autonomous dispatch has no interactive caller. When the mission itself carries no owner,
                    // the objective owner carried on its voyage is the scope, never global admin.
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    TenantMetadata tenantC = new TenantMetadata("Tenant C");
                    await testDb.Driver.Tenants.CreateAsync(tenantC).ConfigureAwait(false);
                    UserMaster ownerC = new UserMaster(tenantC.Id, "owner-c@example.com", "password");
                    ownerC.IsTenantAdmin = false;
                    ownerC.IsAdmin = false;
                    await testDb.Driver.Users.CreateAsync(ownerC).ConfigureAwait(false);

                    Voyage voyage = new Voyage("Autonomous voyage")
                    {
                        TenantId = tenantC.Id,
                        UserId = ownerC.Id
                    };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("Autonomous mission")
                    {
                        VoyageId = voyage.Id
                        // No TenantId/UserId: the owner falls back to the voyage (the objective owner).
                    };

                    McpCredentialReference credential = await handler.ResolveMissionMcpCredentialAsync(mission).ConfigureAwait(false);
                    AssertTrue(credential.HasToken, "the autonomous mission carries a scoped MCP credential");
                    AssertFalse(credential.Token!.StartsWith("armada-launch-", StringComparison.Ordinal),
                        "the autonomous mission credential never uses the retired launch-token format");

                    AuthenticationService auth = CreateAuthenticationService(testDb.Driver, tokens);
                    AuthContext ctx = await auth.AuthenticateAsync("Bearer " + credential.Token, null, null).ConfigureAwait(false);
                    AssertEqual(tenantC.Id, ctx.TenantId, "the autonomous mission is scoped to the objective owner's tenant");
                    AssertEqual(ownerC.Id, ctx.UserId, "the autonomous mission is scoped to the objective owner's user");
                    AssertFalse(ctx.IsAdmin, "the autonomous mission is not a global admin");
                }
            });

            await RunTest("A mission on a vessel with no user owner runs as its tenant's default user", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Dispatch copies the vessel's owner onto the voyage and mission, and a tenant-owned vessel
                    // has no user. A record without a user belongs to the default user, so the mission runs as it.
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    Voyage voyage = new Voyage("Tenant-owned voyage") { TenantId = Armada.Core.Constants.DefaultTenantId };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    Mission mission = new Mission("Tenant-owned mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        VoyageId = voyage.Id
                    };

                    McpCredentialReference credential = await handler.ResolveMissionMcpCredentialAsync(mission).ConfigureAwait(false);
                    AssertTrue(credential.HasToken, "a mission on a tenant-owned vessel carries an MCP credential");
                    AssertFalse(credential.Token!.StartsWith("armada-launch-", StringComparison.Ordinal),
                        "the credential never uses the retired launch-token format");

                    AuthenticationService auth = CreateAuthenticationService(testDb.Driver, tokens);
                    AuthContext ctx = await auth.AuthenticateAsync("Bearer " + credential.Token, null, null).ConfigureAwait(false);
                    AssertTrue(ctx.IsAuthenticated, "the credential authenticates");
                    AssertEqual(Armada.Core.Constants.DefaultTenantId, ctx.TenantId, "the mission runs in its own tenant");
                    AssertEqual(Armada.Core.Constants.DefaultUserId, ctx.UserId, "the mission runs as the user its records belong to");
                }
            });

            await RunTest("A mission whose owner is not a user of its tenant carries no MCP credential", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The default user belongs to the default tenant only, and a user of one tenant never lends
                    // its privileges to another tenant's mission.
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    TenantMetadata tenantD = new TenantMetadata("Tenant D");
                    await testDb.Driver.Tenants.CreateAsync(tenantD).ConfigureAwait(false);
                    Mission userless = new Mission("Other tenant, no user") { TenantId = tenantD.Id };
                    McpCredentialReference none = await handler.ResolveMissionMcpCredentialAsync(userless).ConfigureAwait(false);
                    AssertFalse(none.HasToken, "another tenant's user-less mission does not borrow the default user");

                    Mission crossed = new Mission("Other tenant, default-tenant user")
                    {
                        TenantId = tenantD.Id,
                        UserId = Armada.Core.Constants.DefaultUserId
                    };
                    McpCredentialReference crossedCredential = await handler.ResolveMissionMcpCredentialAsync(crossed).ConfigureAwait(false);
                    AssertFalse(crossedCredential.HasToken, "a user of another tenant is never the owner of this tenant's mission");
                }
            });

            await RunTest("The captain tool inventory probes with the same mission owner the launch credential carries", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The inventory describes what a launch delivers, so its Armada probe must present the credential
                    // the launch resolves for the same mission. The mission carries no owner, so both must fall back
                    // to the objective owner carried on its voyage.
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    TenantMetadata tenant = new TenantMetadata("Inventory tenant");
                    await testDb.Driver.Tenants.CreateAsync(tenant).ConfigureAwait(false);
                    UserMaster owner = new UserMaster(tenant.Id, "inventory-owner@example.com", "password");
                    await testDb.Driver.Users.CreateAsync(owner).ConfigureAwait(false);
                    Voyage voyage = new Voyage("Inventory voyage") { TenantId = tenant.Id, UserId = owner.Id };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Inventory mission") { VoyageId = voyage.Id }).ConfigureAwait(false);
                    Captain captain = new Captain("mux-inventory-captain", AgentRuntimeEnum.Mux) { CurrentMissionId = mission.Id };

                    CaptainLaunchIsolationPlan? plan = await handler.PrepareCaptainLaunchIsolationAsync(captain, mission).ConfigureAwait(false);
                    AssertTrue(plan != null && plan.EnvironmentOverrides.ContainsKey(McpLaunchCredential.EnvironmentVariable), "the launch carries a mission credential");
                    AuthContext? launched = tokens.ValidateToken(plan!.EnvironmentOverrides[McpLaunchCredential.EnvironmentVariable]);

                    AuthorizationRecordingHandler http = new AuthorizationRecordingHandler();
                    string profile = Path.Combine(Path.GetTempPath(), "armada_inventory_profile_" + Guid.NewGuid().ToString("N"));
                    using (HttpClient client = new HttpClient(http))
                    {
                        CaptainRuntimeToolCatalogService catalog = new CaptainRuntimeToolCatalogService(CreateLogging(), settings, client, profile, tokens);
                        await catalog.TryDescribeAsync(captain, testDb.Driver).ConfigureAwait(false);
                    }

                    string probed = http.Authorizations.FirstOrDefault(value => value.StartsWith("Bearer ", StringComparison.Ordinal)) ?? String.Empty;
                    AuthContext? inventoried = probed.Length > 0 ? tokens.ValidateToken(probed.Substring("Bearer ".Length)) : null;

                    AssertNotNull(launched, "the launch credential names an owner");
                    AssertNotNull(inventoried, "the inventory probe presents a credential that names an owner");
                    AssertEqual(launched!.TenantId, inventoried!.TenantId, "the inventory probe's tenant matches the launch credential's");
                    AssertEqual(launched.UserId, inventoried.UserId, "the inventory probe's user matches the launch credential's");
                    AssertEqual(owner.Id, launched.UserId, "both resolve the objective owner carried on the voyage");
                }
            });

            await RunTest("A launch registers each provisioned sibling checkout as a workspace root where the runtime accepts one", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    string root = Path.Combine(Path.GetTempPath(), "armada_workspace_roots_" + Guid.NewGuid().ToString("N"));
                    string worktree = Path.Combine(root, "target");
                    string provisioned = Path.Combine(root, "provisioned");
                    Directory.CreateDirectory(worktree);
                    Directory.CreateDirectory(provisioned);

                    try
                    {
                        Vessel vessel = new Vessel("RootsVessel", "https://example.invalid/roots.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                        {
                            new SiblingRepo { RelativePath = "../provisioned" },
                            new SiblingRepo { RelativePath = "../never-provisioned" }
                        });
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                        Mission mission = new Mission("Roots mission")
                        {
                            VesselId = vessel.Id,
                            TenantId = Armada.Core.Constants.DefaultTenantId,
                            UserId = Armada.Core.Constants.DefaultUserId
                        };
                        Dock dock = new Dock(vessel.Id) { WorktreePath = worktree };

                        foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.ClaudeCode, AgentRuntimeEnum.Codex, AgentRuntimeEnum.Cursor })
                        {
                            CaptainLaunchIsolationPlan? plan = await handler.PrepareCaptainLaunchIsolationAsync(new Captain("roots-" + runtime, runtime), mission, dock).ConfigureAwait(false);
                            AssertNotNull(plan, runtime + " launch produces a plan");
                            int index = plan!.ExtraArguments.IndexOf("--add-dir");
                            AssertTrue(index >= 0 && plan.ExtraArguments[index + 1] == Path.GetFullPath(provisioned),
                                runtime + " receives the provisioned sibling as a workspace root");
                            AssertEqual(1, plan.ExtraArguments.Count(argument => argument == "--add-dir"),
                                runtime + " receives no root for a sibling that was not provisioned");
                        }

                        CaptainLaunchIsolationPlan? openCode = await handler.PrepareCaptainLaunchIsolationAsync(new Captain("roots-opencode", AgentRuntimeEnum.OpenCode), mission, dock).ConfigureAwait(false);
                        AssertFalse(openCode != null && openCode.ExtraArguments.Contains("--add-dir"), "a runtime without workspace roots is left unchanged");
                    }
                    finally
                    {
                        try { Directory.Delete(root, true); } catch { }
                    }
                }
            });

            await RunTest("A launch registers each declared evidence root for its vessel as a workspace root and leaves out a missing or other-vessel root", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    string root = Path.Combine(Path.GetTempPath(), "armada_evidence_roots_" + Guid.NewGuid().ToString("N"));
                    string worktree = Path.Combine(root, "target");
                    string everyVessel = Path.Combine(root, "evidence-all");
                    string thisVessel = Path.Combine(root, "evidence-this");
                    string otherVessel = Path.Combine(root, "evidence-other");
                    Directory.CreateDirectory(worktree);
                    Directory.CreateDirectory(everyVessel);
                    Directory.CreateDirectory(thisVessel);
                    Directory.CreateDirectory(otherVessel);

                    try
                    {
                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("EvidenceVessel", "https://example.invalid/evidence.git")).ConfigureAwait(false);
                        settings.CaptainEvidenceRoots = new List<CaptainEvidenceRoot>
                        {
                            new CaptainEvidenceRoot { Path = everyVessel + "/" },
                            new CaptainEvidenceRoot { Path = thisVessel, VesselIds = new List<string> { vessel.Id } },
                            new CaptainEvidenceRoot { Path = otherVessel, VesselIds = new List<string> { "vsl_another" } },
                            new CaptainEvidenceRoot { Path = Path.Combine(root, "never-mounted") }
                        };
                        Mission mission = new Mission("Evidence mission")
                        {
                            VesselId = vessel.Id,
                            TenantId = Armada.Core.Constants.DefaultTenantId,
                            UserId = Armada.Core.Constants.DefaultUserId
                        };
                        Dock dock = new Dock(vessel.Id) { WorktreePath = worktree };

                        foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.ClaudeCode, AgentRuntimeEnum.Codex, AgentRuntimeEnum.Cursor })
                        {
                            CaptainLaunchIsolationPlan? plan = await handler.PrepareCaptainLaunchIsolationAsync(new Captain("evidence-" + runtime, runtime), mission, dock).ConfigureAwait(false);
                            AssertNotNull(plan, runtime + " launch produces a plan");
                            List<string> roots = new List<string>();
                            for (int i = 0; i + 1 < plan!.ExtraArguments.Count; i++)
                                if (plan.ExtraArguments[i] == "--add-dir") roots.Add(plan.ExtraArguments[i + 1]);
                            AssertTrue(roots.Contains(Path.GetFullPath(everyVessel)), runtime + " receives an evidence root declared for every vessel");
                            AssertTrue(roots.Contains(Path.GetFullPath(thisVessel)), runtime + " receives an evidence root declared for its vessel");
                            AssertEqual(2, roots.Count, runtime + " receives no root for another vessel or a missing directory: " + String.Join(", ", roots));
                        }

                        CaptainLaunchIsolationPlan? openCode = await handler.PrepareCaptainLaunchIsolationAsync(new Captain("evidence-opencode", AgentRuntimeEnum.OpenCode), mission, dock).ConfigureAwait(false);
                        AssertFalse(openCode != null && openCode.ExtraArguments.Contains("--add-dir"), "a runtime without workspace roots is left unchanged");
                    }
                    finally
                    {
                        try { Directory.Delete(root, true); } catch { }
                    }
                }
            });

            await RunTest("An evidence root declared by a settings reload reaches the next launch without a restart", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    string root = Path.Combine(Path.GetTempPath(), "armada_evidence_reload_" + Guid.NewGuid().ToString("N"));
                    string worktree = Path.Combine(root, "target");
                    string evidence = Path.Combine(root, "evidence");
                    Directory.CreateDirectory(worktree);
                    Directory.CreateDirectory(evidence);

                    try
                    {
                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("ReloadVessel", "https://example.invalid/reload.git")).ConfigureAwait(false);
                        Mission mission = new Mission("Reload mission")
                        {
                            VesselId = vessel.Id,
                            TenantId = Armada.Core.Constants.DefaultTenantId,
                            UserId = Armada.Core.Constants.DefaultUserId
                        };
                        Dock dock = new Dock(vessel.Id) { WorktreePath = worktree };
                        Captain captain = new Captain("evidence-reload", AgentRuntimeEnum.ClaudeCode);

                        CaptainLaunchIsolationPlan? before = await handler.PrepareCaptainLaunchIsolationAsync(captain, mission, dock).ConfigureAwait(false);
                        AssertFalse(before != null && before.ExtraArguments.Contains(Path.GetFullPath(evidence)), "no evidence root is registered before the reload");

                        ArmadaSettings candidate = new ArmadaSettings();
                        candidate.CaptainEvidenceRoots = new List<CaptainEvidenceRoot>
                        {
                            new CaptainEvidenceRoot { Path = evidence, VesselIds = new List<string> { vessel.Id } }
                        };
                        settings.ApplyHotReloadableFrom(candidate);
                        candidate.CaptainEvidenceRoots[0].VesselIds.Clear();
                        candidate.CaptainEvidenceRoots[0].VesselIds.Add("vsl_another");

                        CaptainLaunchIsolationPlan? after = await handler.PrepareCaptainLaunchIsolationAsync(captain, mission, dock).ConfigureAwait(false);
                        AssertNotNull(after, "the launch after the reload produces a plan");
                        AssertTrue(after!.ExtraArguments.Contains(Path.GetFullPath(evidence)), "the reloaded evidence root is registered: " + String.Join(" ", after.ExtraArguments));
                    }
                    finally
                    {
                        try { Directory.Delete(root, true); } catch { }
                    }
                }
            });

            await RunTest("A mission launch plan never carries the admiral launch credential value in its environment", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    Captain captain = new Captain("cursor-mission-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Owned mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    };

                    CaptainLaunchIsolationPlan? plan = await handler.PrepareCaptainLaunchIsolationAsync(captain, mission).ConfigureAwait(false);
                    AssertTrue(plan != null, "a seeded mission launch produces a plan");
                    AssertTrue(plan!.EnvironmentOverrides.ContainsKey(McpLaunchCredential.EnvironmentVariable),
                        "the launch variable carries the mission credential so the dock configuration resolves");
                    // The value present is a scoped session token, not the retired global-token format.
                    AuthenticationService auth = CreateAuthenticationService(testDb.Driver, tokens);
                    string carried = plan.EnvironmentOverrides[McpLaunchCredential.EnvironmentVariable];
                    AssertFalse(carried.StartsWith("armada-launch-", StringComparison.Ordinal),
                        "the launch variable never holds the retired global-token format");
                    AuthContext carriedContext = await auth.AuthenticateAsync("Bearer " + carried, null, null).ConfigureAwait(false);
                    AssertTrue(carriedContext.IsAuthenticated, "the carried value authenticates as a scoped session token");
                    AssertEqual(Armada.Core.Constants.DefaultTenantId, carriedContext.TenantId, "the carried session token remains in the mission tenant");
                    AssertEqual(Armada.Core.Constants.DefaultUserId, carriedContext.UserId, "the carried session token remains with the mission user");
                    AssertEqual(mission.Id, carriedContext.MissionId, "the carried session token remains bound to this mission");
                    AssertFalse(Armada.Server.Mcp.McpToolAccessPolicy.IsAllowed(carriedContext, "armada_stop_server"),
                        "the mission token cannot use operator control even when its owner is an administrator");
                }
            });

            await RunTest("A legitimate same-tenant mission still launches with working MCP wiring", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The fix must not remove what a normal mission can legitimately do: the captain still
                    // receives a credential its dock and scoped MCP configuration can resolve.
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    Captain claude = new Captain("claude-mission-captain", AgentRuntimeEnum.ClaudeCode);
                    Mission mission = new Mission("Same-tenant mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    };

                    CaptainLaunchIsolationPlan? plan = await handler.PrepareCaptainLaunchIsolationAsync(claude, mission).ConfigureAwait(false);
                    AssertTrue(plan != null, "a same-tenant mission still gets a launch plan");
                    AssertTrue(plan!.EnvironmentOverrides.TryGetValue(McpLaunchCredential.EnvironmentVariable, out string? value) && !string.IsNullOrEmpty(value),
                        "the captain still carries an MCP credential so its tools work");
                    AssertTrue(plan.ExtraArguments.Contains("--strict-mcp-config"),
                        "Claude Code still receives its scoped MCP configuration");
                    AssertTrue(plan.FilesToWrite.Exists(f => f.RelativePath == "armada-mcp.json"),
                        "the scoped MCP config file is still written");
                    AssertTrue(plan.FilesToWrite.TrueForAll(f => !f.Contents.Contains(value!, StringComparison.Ordinal)),
                        "the credential value never lands in a scoped config file, only the variable reference");
                }
            });

            await RunTest("IsProcessExitHandled holds while exit handling is in flight, beyond the retention window", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    StubAdmiralService admiral = new StubAdmiralService();
                    admiral.OnHandleProcessExit = (processId, exitCode, captainId, missionId) => release.Task;
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _, null, admiral);
                    handler.HandledExitRetention = TimeSpan.FromMilliseconds(1);

                    Task handling = handler.HandleAgentProcessExitedAsync(4242, 0, "cpt_test", "msn_test");
                    await Task.Delay(50).ConfigureAwait(false);

                    // The completion handler is still running well past the retention window: the
                    // health check must still see the exit as handled, or it reads the finished
                    // process as a crash and fails a mission that is being completed.
                    AssertTrue(handler.IsProcessExitHandled(4242), "in-flight exit stays handled regardless of retention");

                    release.SetResult(true);
                    await handling.ConfigureAwait(false);
                    await Task.Delay(20).ConfigureAwait(false);

                    AssertFalse(handler.IsProcessExitHandled(4242), "after completion the marker expires with the retention window");
                }
            });

            await RunTest("Runtime exit evidence matches stored microseconds and cannot cross a native launch identity", () =>
            {
                const int processId = 4347;
                DateTime started = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc).AddTicks(7);
                Armada.Core.ProcessSupervisor.RecordLaunchedProcess(processId, started);
                Armada.Core.RuntimeProcessExitEvidence first = Armada.Core.ProcessSupervisor.TrackRuntimeExit(processId);
                try
                {
                    DateTime stored = new DateTime(started.Ticks - started.Ticks % 10, DateTimeKind.Utc);
                    AssertTrue(Object.ReferenceEquals(first, Armada.Core.ProcessSupervisor.GetRuntimeExitEvidence(processId, stored)),
                        "database microsecond storage does not discard a genuine trusted outcome");
                    AssertTrue(Armada.Core.ProcessSupervisor.GetRuntimeExitEvidence(processId, stored.AddTicks(10)) == null,
                        "a different persisted start cannot inherit evidence");
                    Armada.Core.ProcessSupervisor.RecordLaunchedProcess(processId, started.AddSeconds(1));
                    AssertTrue(Armada.Core.ProcessSupervisor.GetRuntimeExitEvidence(processId) == null, "replacement native identity rejects old evidence");
                    Armada.Core.RuntimeProcessExitEvidence next = Armada.Core.ProcessSupervisor.TrackRuntimeExit(processId);
                    first.Complete(74, true);
                    Armada.Core.ProcessSupervisor.ReleaseRuntimeExitEvidence(processId, first);
                    AssertTrue(Object.ReferenceEquals(next, Armada.Core.ProcessSupervisor.GetRuntimeExitEvidence(processId)),
                        "late cleanup of an old launch cannot remove its replacement");
                    AssertFalse(next.TryGetOutcome(out _, out _), "late old completion cannot finalize a new launch");
                }
                finally { Armada.Core.ProcessSupervisor.ForgetLaunchedProcess(processId); }
            });

            await RunTest("A failed native exit delivery remains retryable and retains trusted failure evidence", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                StubAdmiralService admiral = new();
                int calls = 0;
                admiral.OnHandleProcessExit = (pid, code, captainId, missionId) =>
                {
                    AssertEqual(74, code, "the effective exit survives retry");
                    if (Interlocked.Increment(ref calls) == 1) throw new IOException("simulated database storage failure");
                    return Task.CompletedTask;
                };
                AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _, admiralOverride: admiral);
                const int processId = 4345;
                Armada.Core.ProcessSupervisor.RecordLaunchedProcess(processId, DateTime.UtcNow);
                Armada.Core.RuntimeProcessExitEvidence evidence = Armada.Core.ProcessSupervisor.TrackRuntimeExit(processId);
                evidence.Complete(74, true);
                try
                {
                    bool failed = false;
                    try { await handler.HandleAgentProcessExitedAsync(processId, 74, "cpt_retry", "msn_retry").ConfigureAwait(false); }
                    catch (IOException) { failed = true; }
                    AssertTrue(failed, "the storage failure remains visible");
                    AssertFalse(handler.IsProcessExitHandled(processId), "failed durable delivery is not marked handled");
                    AssertTrue(Object.ReferenceEquals(evidence, Armada.Core.ProcessSupervisor.GetRuntimeExitEvidence(processId)),
                        "trusted outcome remains available to recovery");
                    await handler.HandleAgentProcessExitedAsync(processId, 74, "cpt_retry", "msn_retry").ConfigureAwait(false);
                    AssertEqual(2, calls, "one retry delivers the original failure");
                    AssertTrue(handler.IsProcessExitHandled(processId), "successful retry is marked handled");
                    AssertTrue(Armada.Core.ProcessSupervisor.GetRuntimeExitEvidence(processId) == null, "successful retry releases consumed evidence");
                }
                finally { Armada.Core.ProcessSupervisor.ForgetLaunchedProcess(processId); }
            });

            await RunTest("IsProcessExitHandled recognises a completed exit within the retention window and not a foreign PID", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    handler.HandledExitRetention = TimeSpan.FromMinutes(5);

                    await handler.HandleAgentProcessExitedAsync(4343, 0, "cpt_test", "msn_test").ConfigureAwait(false);

                    AssertTrue(handler.IsProcessExitHandled(4343), "a just-completed exit is recognised within retention");
                    AssertFalse(handler.IsProcessExitHandled(4344), "a PID that never exited through the handler is not handled");
                }
            });

            await RunTest("CursorShimScope on Windows uses temp override and restores environment", () =>
            {
                if (!OperatingSystem.IsWindows())
                    return;

                string? originalOverride = Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT");
                string sentinelOverride = Path.Combine(Path.GetTempPath(), "armada_original_cursor_agent.cmd");

                try
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", sentinelOverride);

                    using (CursorShimScope shim = CursorShimScope.Create())
                    {
                        string? overridePath = Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT");
                        AssertNotNull(overridePath, "Windows shim scope must set ARMADA_TEST_CURSOR_AGENT");
                        AssertFalse(
                            String.Equals(sentinelOverride, overridePath, StringComparison.OrdinalIgnoreCase),
                            "Windows shim scope must replace the caller override while active");
                        string appDataNpm = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "npm");
                        AssertFalse(
                            overridePath!.StartsWith(appDataNpm, StringComparison.OrdinalIgnoreCase),
                            "Windows shim scope must not write the active shim under APPDATA npm");
                        AssertTrue(File.Exists(overridePath), "Windows shim override must point to the temp shim file");
                    }

                    AssertEqual(
                        sentinelOverride,
                        Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT"),
                        "Windows shim scope must restore the prior test override on dispose");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", originalOverride);
                }
            });

            await RunTest("ValidateNativeEndpointAdmission accepts an enabled inference endpoint and rejects the wrong kind, disabled, and a model mismatch", () =>
            {
                Captain captain = new Captain("external-judge", AgentRuntimeEnum.ClaudeCode)
                {
                    TenantId = "ten_ep",
                    UserId = "usr_ep",
                    ModelEndpointId = "mep_native",
                    Model = "claude-fable-5"
                };
                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Id = "mep_native",
                    TenantId = "ten_ep",
                    UserId = "usr_ep",
                    Name = "External Fable",
                    Provider = ModelProviderEnum.Anthropic,
                    Kind = ModelEndpointKindEnum.Inference,
                    Scope = ScopeEnum.TenantWide,
                    BaseUrl = "https://api.example.com/v1",
                    ApiKey = "sk-endpoint",
                    Model = "claude-fable-5",
                    Enabled = true
                };

                AssertNull(AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, endpoint), "an enabled inference endpoint the captain owns is admitted");
                AssertNotNull(AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, null), "a missing endpoint is rejected");

                endpoint.Kind = ModelEndpointKindEnum.Embedding;
                AssertContains("inference", AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, endpoint)!, "an embedding endpoint is rejected");
                endpoint.Kind = ModelEndpointKindEnum.Inference;

                endpoint.Enabled = false;
                AssertContains("disabled", AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, endpoint)!, "a disabled endpoint is rejected");
                endpoint.Enabled = true;

                captain.Model = "some-other-model";
                AssertContains("must match", AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, endpoint)!, "a model mismatch is rejected");
                return Task.CompletedTask;
            });

            await RunTest("A native captain's referenced inference endpoint drives the runtime provider resolver", () =>
            {
                Captain captain = new Captain("external-judge", AgentRuntimeEnum.ClaudeCode)
                {
                    TenantId = "ten_ep",
                    ModelEndpointId = "mep_native",
                    Model = "claude-fable-5"
                };
                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Id = "mep_native",
                    TenantId = "ten_ep",
                    Provider = ModelProviderEnum.Anthropic,
                    Kind = ModelEndpointKindEnum.Inference,
                    BaseUrl = "https://api.example.com/v1",
                    ApiKey = "sk-endpoint",
                    Model = "claude-fable-5",
                    Enabled = true
                };

                AgentLifecycleHandler.ApplyNativeEndpointCredentials(captain, endpoint);
                AssertEqual("https://api.example.com/v1", captain.ApiBaseUrl!, "the endpoint base URL is carried onto the launch snapshot");
                AssertEqual("sk-endpoint", captain.ApiKey!, "the endpoint key is carried onto the launch snapshot");

                ResolvedModelProvider? resolved = ModelProviderResolver.Resolve(captain, null, new ModelProvidersSettings());
                AssertNotNull(resolved, "the runtime provider resolver must resolve from the endpoint credentials");
                AssertEqual("https://api.example.com/v1", resolved!.BaseUrl, "the runtime uses the endpoint base URL");
                AssertEqual("sk-endpoint", resolved.ApiKey, "the runtime uses the endpoint key");
                return Task.CompletedTask;
            });

            await RunTest("HandleLaunchAgentAsync omits commit trailers on a read-only mission", async () =>
            {
                // Probe papercut 2026-08-09: the launch prompt supplied git commit trailers on a
                // Research mission whose rules forbid any commit - dead weight that contradicts the
                // brief and costs tokens on every read-only dispatch.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimScope shim = CursorShimScope.Create())
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    string worktreePath = Path.Combine(Path.GetTempPath(), "armada_cursor_readonly_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktreePath);

                    try
                    {
                        Captain captain = new Captain("readonly-captain", AgentRuntimeEnum.Cursor)
                        {
                            Model = "cursor-model"
                        };

                        Mission readOnly = new Mission("Read-only mission")
                        {
                            Persona = "Worker",
                            Mode = MissionModeEnum.Audit,
                            BranchName = "feature/readonly"
                        };

                        Dock dock = new Dock
                        {
                            BranchName = "feature/readonly",
                            WorktreePath = worktreePath
                        };
                        string logFilePath = Path.Combine(settings.LogDirectory, "missions", readOnly.Id + ".log");

                        int processId = await handler.HandleLaunchAgentAsync(captain, readOnly, dock).ConfigureAwait(false);
                        string logContents = await WaitForFileContainsAsync(logFilePath, "Mission: Read-only mission").ConfigureAwait(false);

                        AssertTrue(processId > 0, "Launch should return a process id");
                        AssertFalse(logContents.Contains("full manifest", StringComparison.OrdinalIgnoreCase),
                            "a read-only mission must not receive commit-trailer instructions");
                        AssertFalse(logContents.Contains("with these trailers", StringComparison.OrdinalIgnoreCase),
                            "the commit-trailer block must be absent from a read-only launch prompt");
                    }
                    finally
                    {
                        try { Directory.Delete(worktreePath, true); } catch { }
                    }
                }
            });

            await RunTest("HandleLaunchAgentAsync persists returned process id before returning", async () =>
            {                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimScope shim = CursorShimScope.Create())
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    string worktreePath = Path.Combine(Path.GetTempPath(), "armada_cursor_pid_launch_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktreePath);

                    try
                    {
                        Mission mission = new Mission("Launch PID mission")
                        {
                            Persona = "Worker",
                            BranchName = "feature/pid-registration",
                            Status = MissionStatusEnum.Assigned,
                            AssignmentState = MissionAssignmentStateEnum.Assigned
                        };

                        Captain captain = new Captain("launch-pid-captain", AgentRuntimeEnum.Cursor)
                        {
                            Model = "slow-launch-model",
                            State = CaptainStateEnum.Working,
                            CurrentMissionId = mission.Id
                        };

                        mission.CaptainId = captain.Id;

                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        Dock dock = new Dock
                        {
                            BranchName = "feature/pid-registration",
                            WorktreePath = worktreePath
                        };

                        int processId = await handler.HandleLaunchAgentAsync(captain, mission, dock).ConfigureAwait(false);

                        Captain? persistedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        Mission? persistedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

                        AssertTrue(processId > 0, "Launch should return a process id");
                        AssertNotNull(persistedCaptain, "Captain row should still exist");
                        AssertNotNull(persistedMission, "Mission row should still exist");
                        AssertEqual(processId, persistedCaptain!.ProcessId, "Captain row should have the returned process id before launch returns");
                        AssertEqual(processId, persistedMission!.ProcessId, "Mission row should have the returned process id before launch returns");
                    }
                    finally
                    {
                        try { Directory.Delete(worktreePath, true); } catch { }
                    }
                }
            });

            await RunTest("PersistStartedProcessIdAsync ExistingDifferentMissionProcessId LeavesRowsUnchanged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Mission mission = new Mission("Existing PID mission")
                    {
                        Status = MissionStatusEnum.InProgress,
                        ProcessId = 111111
                    };

                    Captain captain = new Captain("existing-pid-captain", AgentRuntimeEnum.Cursor)
                    {
                        State = CaptainStateEnum.Working,
                        CurrentMissionId = mission.Id,
                        ProcessId = null
                    };

                    mission.CaptainId = captain.Id;

                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    string launchKey = captain.Id + ":" + mission.Id;
                    RegisterPendingLaunch(handler, launchKey, captain.Id, mission.Id);
                    await InvokePersistStartedProcessIdAsync(handler, 222222, launchKey).ConfigureAwait(false);

                    Captain? persistedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Mission? persistedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

                    AssertNotNull(persistedCaptain, "Captain row should still exist");
                    AssertNotNull(persistedMission, "Mission row should still exist");
                    AssertNull(persistedCaptain!.ProcessId, "Persistence must not record a new PID when the mission already has a different PID");
                    AssertEqual(111111, persistedMission!.ProcessId, "Persistence must not overwrite an existing different mission PID");
                    AssertNull(persistedMission.StartedUtc, "Rejected PID persistence should not mark the mission started");
                }
            });

            await RunTest("HandleAgentHeartbeat updates mission and voyage timestamps", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);

                    Captain captain = new Captain("heartbeat-captain", AgentRuntimeEnum.Cursor);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("Heartbeat voyage", "Telemetry proof");
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("Heartbeat mission")
                    {
                        VoyageId = voyage.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? beforeMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Voyage? beforeVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertNotNull(beforeMission);
                    AssertNotNull(beforeVoyage);

                    RegisterTrackedProcess(handler, 424242, captain.Id, mission.Id);

                    await Task.Delay(20).ConfigureAwait(false);
                    handler.HandleAgentHeartbeat(424242, "still running");

                    await WaitForConditionAsync(async () =>
                    {
                        Captain? refreshedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        Mission? refreshedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        Voyage? refreshedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                        return refreshedCaptain?.LastHeartbeatUtc.HasValue == true
                            && refreshedMission != null
                            && refreshedMission.LastUpdateUtc > beforeMission!.LastUpdateUtc
                            && refreshedVoyage != null
                            && refreshedVoyage.LastUpdateUtc > beforeVoyage!.LastUpdateUtc;
                    }).ConfigureAwait(false);
                }
            });

            // A silent-but-running process must keep telemetry fresh WITHOUT advancing the captain's
            // output heartbeat: stall detection measures that value's age, so refreshing it here is
            // what stops a silent agent from ever being detected as stalled. This test used to assert
            // LastHeartbeatUtc was set, which pinned exactly that masking behaviour as intended.
            await RunTest("Silent running process refreshes liveness telemetry without masking a stall", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    settings.HeartbeatIntervalSeconds = 5;
                    // Several liveness ticks fit well inside the wait below; the production floor is five seconds.
                    handler.ProcessLivenessInterval = TimeSpan.FromMilliseconds(200);

                    Captain captain = new Captain("silent-heartbeat-captain", AgentRuntimeEnum.Cursor);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("Silent heartbeat voyage", "Telemetry proof");
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("Silent heartbeat mission")
                    {
                        VoyageId = voyage.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? beforeMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Voyage? beforeVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertNotNull(beforeMission);
                    AssertNotNull(beforeVoyage);

                    using Process process = StartSilentProcess();
                    RegisterTrackedProcess(handler, process.Id, captain.Id, mission.Id);
                    StartTrackedProcessHeartbeat(handler, process.Id, captain.Id, mission.Id);

                    try
                    {
                        await WaitForConditionAsync(async () =>
                        {
                            Captain? refreshedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                            Mission? refreshedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                            Voyage? refreshedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                            return refreshedCaptain?.LastProcessAliveUtc.HasValue == true
                                && refreshedMission != null
                                && refreshedMission.LastUpdateUtc > beforeMission!.LastUpdateUtc
                                && refreshedVoyage != null
                                && refreshedVoyage.LastUpdateUtc > beforeVoyage!.LastUpdateUtc;
                        }, TimeSpan.FromSeconds(8)).ConfigureAwait(false);

                        // The captain produced no output, so the value stall detection reads must
                        // still be unset. If the liveness loop advanced it, a silent agent would
                        // look freshly active for as long as its process stayed up.
                        Captain? silentCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        AssertNotNull(silentCaptain);
                        Assert(
                            !silentCaptain!.LastHeartbeatUtc.HasValue,
                            "A silent process must not advance the captain output heartbeat");
                    }
                    finally
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill(entireProcessTree: true);
                                process.WaitForExit(5000);
                            }
                        }
                        catch { }
                    }
                }
            });

            await RunTest("StartProcessLivenessHeartbeat_WhenCtsDisposedDuringDelay_DoesNotFaultTask", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    settings.HeartbeatIntervalSeconds = 5;

                    Captain captain = new Captain("dispose-heartbeat-captain", AgentRuntimeEnum.Cursor);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Mission mission = new Mission("Dispose heartbeat mission");
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Exception? unobservedException = null;
                    EventHandler<UnobservedTaskExceptionEventArgs> unobservedHandler = (_, args) =>
                    {
                        if (unobservedException == null)
                        {
                            unobservedException = args.Exception;
                        }

                        args.SetObserved();
                    };

                    TaskScheduler.UnobservedTaskException += unobservedHandler;
                    using Process process = StartSilentProcess();
                    try
                    {
                        RegisterTrackedProcess(handler, process.Id, captain.Id, mission.Id);
                        StartTrackedProcessHeartbeat(handler, process.Id, captain.Id, mission.Id);

                        StopTrackedProcessHeartbeat(handler, process.Id);

                        await WaitForConditionAsync(async () =>
                        {
                            await Task.CompletedTask.ConfigureAwait(false);
                            return !HasProcessHeartbeatLoop(handler, process.Id);
                        }, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                        await Task.Delay(200).ConfigureAwait(false);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        AssertNull(unobservedException, "Disposing heartbeat CTS during delay should not surface an unobserved exception");
                    }
                    finally
                    {
                        TaskScheduler.UnobservedTaskException -= unobservedHandler;
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill(entireProcessTree: true);
                                process.WaitForExit(5000);
                            }
                        }
                        catch { }
                    }
                }
            });

            await RunTest("A process exit that stops the liveness heartbeat while it is starting never faults the launch", async () =>
            {
                // The exit handler stops the heartbeat on its own thread, so it can remove and dispose the
                // loop's cancellation source at any instant after the launch path publishes it. Starting must
                // not touch the source after publishing it.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    settings.HeartbeatIntervalSeconds = 3600;
                    const int processId = 737373;
                    const int iterations = 50000;

                    MethodInfo startMethod = typeof(AgentLifecycleHandler).GetMethod("StartProcessLivenessHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Could not find StartProcessLivenessHeartbeat method");
                    MethodInfo stopMethod = typeof(AgentLifecycleHandler).GetMethod("StopProcessLivenessHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Could not find StopProcessLivenessHeartbeat method");
                    Action<int, string, string> start = startMethod.CreateDelegate<Action<int, string, string>>(handler);
                    Action<int> stop = stopMethod.CreateDelegate<Action<int>>(handler);

                    int faults = 0;
                    string? firstFault = null;
                    using (CancellationTokenSource stopping = new CancellationTokenSource())
                    {
                        Task stopper = Task.Factory.StartNew(() =>
                        {
                            while (!stopping.IsCancellationRequested) stop(processId);
                        }, TaskCreationOptions.LongRunning);

                        Task starter = Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < iterations; i++)
                            {
                                try { start(processId, "race-captain", "race-mission"); }
                                catch (ObjectDisposedException ex)
                                {
                                    faults++;
                                    if (firstFault == null) firstFault = ex.Message;
                                }
                            }
                        }, TaskCreationOptions.LongRunning);

                        await starter.ConfigureAwait(false);
                        stopping.Cancel();
                        await stopper.ConfigureAwait(false);
                    }

                    stop(processId);
                    AssertEqual(0, faults, "starting the heartbeat raced its stop " + faults + " times out of " + iterations + (firstFault == null ? "" : ": " + firstFault));
                }
            });

            await RunTest("A stopped liveness heartbeat loop never removes the loop a later launch registered for the same process id", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    settings.HeartbeatIntervalSeconds = 3600;
                    const int rounds = 200;
                    int lost = 0;

                    for (int i = 0; i < rounds; i++)
                    {
                        int processId = 848000 + i;
                        StartTrackedProcessHeartbeat(handler, processId, "reuse-captain", "reuse-mission-a");
                        StopTrackedProcessHeartbeat(handler, processId);
                        StartTrackedProcessHeartbeat(handler, processId, "reuse-captain", "reuse-mission-b");
                    }

                    // Every stopped first loop has finished once its cancellation is observed.
                    await Task.Delay(500).ConfigureAwait(false);

                    for (int i = 0; i < rounds; i++)
                    {
                        int processId = 848000 + i;
                        if (!HasProcessHeartbeatLoop(handler, processId)) lost++;
                        StopTrackedProcessHeartbeat(handler, processId);
                    }

                    AssertEqual(0, lost, "a stopped loop removed the registration of the loop started after it");
                }
            });

            await RunTest("HandleAgentOutput bounds streamed output and retains tail with truncation marker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    int processId = 919191;
                    string missionId = "msn_bounded_output_capture";
                    RegisterTrackedProcess(handler, processId, "captain-bounded", missionId);

                    // Each line is ~1 KiB; feeding 4096 of them is ~4 MiB total -- well past the 256 KiB cap.
                    string filler = new string('a', 1024);
                    int totalLines = 4096;
                    for (int i = 0; i < totalLines; i++)
                    {
                        handler.HandleAgentOutput(processId, "line " + i + " " + filler);
                    }

                    string? output = handler.GetAndClearMissionOutput(missionId);

                    AssertNotNull(output, "Streamed output should still be available after bounded appends");
                    AssertTrue(output!.Length < 1024 * 1024, "Bounded output must not retain multi-MiB transcripts");
                    AssertContains("[ARMADA: streamed output truncated to retain tail]", output, "Truncation marker should be inserted when buffer is clipped");
                    AssertContains("line " + (totalLines - 1), output, "Most recent tail should be retained");
                    AssertFalse(output.Contains("line 0 ", StringComparison.Ordinal), "Older head content should be dropped to preserve memory cap");
                }
            });

            await RunTest("HandleAgentOutput stores bounded Captain notes with mission scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    LoggingModule logging = CreateLogging();
                    CoordinationService coordination = new CoordinationService(logging, testDb.Driver);
                    handler.SetCoordinationService(coordination);

                    TenantMetadata tenant = new TenantMetadata("Captain note tenant");
                    await testDb.Driver.Tenants.CreateAsync(tenant).ConfigureAwait(false);
                    Captain captain = new Captain("Captain note author", AgentRuntimeEnum.Cursor) { TenantId = tenant.Id };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("Captain note vessel", "https://example.test/captain-note.git")
                    {
                        TenantId = tenant.Id
                    }).ConfigureAwait(false);
                    Voyage voyage = new Voyage("Captain note voyage") { TenantId = tenant.Id };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    Mission mission = new Mission("Captain note mission")
                    {
                        TenantId = tenant.Id,
                        VoyageId = voyage.Id,
                        VesselId = vessel.Id,
                        CaptainId = captain.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 919192;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    string secret = new string('a', 24);
                    handler.HandleAgentOutput(processId, "[ARMADA:NOTE] claim uses api_key=" + secret);
                    await WaitForConditionAsync(async () =>
                    {
                        List<CoordinationMessage> current = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                        return current.Count == 1;
                    }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

                    handler.HandleAgentOutput(processId, "[ARMADA:NOTE] second note in same output record\n[ARMADA:NOTE] third note in same output record");
                    await WaitForConditionAsync(async () =>
                    {
                        List<CoordinationMessage> current = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                        return current.Count == 3;
                    }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

                    handler.HandleAgentOutput(processId, "[ARMADA:NOTE]" + "malformed");
                    handler.HandleAgentOutput(processId, "[ARMADA:NOTE] " + new string('x', 501));
                    for (int i = 0; i < 21; i++)
                        handler.HandleAgentOutput(processId, "[ARMADA:NOTE] outcome " + i);

                    await WaitForConditionAsync(async () =>
                    {
                        List<CoordinationMessage> current = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                        return current.Count == 20;
                    }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

                    List<CoordinationMessage> notes = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    AssertEqual(20, notes.Count, "only twenty Captain notes are accepted for one mission");
                    CoordinationMessage? authoredNote = notes.Find(message => message.Content.StartsWith("claim uses", StringComparison.Ordinal));
                    AssertNotNull(authoredNote, "the normal lifecycle output path writes the claim note");
                    AssertEqual(CoordinationAuthorTypeEnum.Captain, authoredNote!.AuthorType);
                    AssertEqual(captain.Id, authoredNote.AuthorId);
                    AssertEqual(captain.Name, authoredNote.AuthorName);
                    AssertEqual(mission.Id, authoredNote.MissionId);
                    AssertEqual(voyage.Id, authoredNote.VoyageId);
                    AssertEqual(mission.VesselId, authoredNote.VesselId);
                    AssertEqual(tenant.Id, authoredNote.TenantId);
                    AssertTrue(notes.Exists(message => message.Content == "second note in same output record"), "each note marker on a multiline record is routed");
                    AssertTrue(notes.Exists(message => message.Content == "third note in same output record"), "all note markers on a multiline record are routed");
                    AssertNull(authoredNote.ToParticipantKey, "captain notes are broadcast board records, not private captain-to-captain messages");
                    AssertFalse(authoredNote.Content.Contains(secret, StringComparison.Ordinal), "credential-shaped values are removed before storage");
                    AssertFalse(notes.Exists(message => message.Content.Contains(new string('x', 501), StringComparison.Ordinal)), "over-limit notes are rejected");
                    AssertFalse(notes.Exists(message => message.Content == "malformed"), "malformed markers are ignored by the parser");
                    List<Signal> signals = await testDb.Driver.Signals.EnumerateRecentAsync(200).ConfigureAwait(false);
                    AssertFalse(signals.Exists(signal => signal.Payload.Contains(secret, StringComparison.Ordinal)), "raw note content is not copied to the progress-signal store");
                    Captain unassignedCaptain = new Captain("Unassigned captain", AgentRuntimeEnum.Cursor);
                    AssertNull(await coordination.PostCaptainNoteAsync(unassignedCaptain, mission, "forged note").ConfigureAwait(false), "a captain cannot write for a mission assigned to another captain");
                    AssertNull(await coordination.PostCaptainNoteAsync(captain, mission, "  ").ConfigureAwait(false), "empty note content is rejected");
                }
            });

            await RunTest("HandleAgentOutput bounds single oversized runtime chunk", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    int processId = 929292;
                    string missionId = "msn_single_oversized_output_capture";
                    RegisterTrackedProcess(handler, processId, "captain-single-oversized", missionId);

                    string oversizedChunk = "single oversized chunk " + new string('b', 2 * 1024 * 1024);
                    handler.HandleAgentOutput(processId, oversizedChunk);

                    string? output = handler.GetAndClearMissionOutput(missionId);

                    AssertNotNull(output, "Streamed output should still be available after a single oversized append");
                    AssertTrue(output!.Length < 1024 * 1024, "A single runtime output chunk must still obey the mission output cap");
                    AssertContains("[ARMADA: streamed output truncated to retain tail]", output, "Truncation marker should be inserted for a single oversized chunk");
                    AssertFalse(output.Contains("single oversized chunk", StringComparison.Ordinal), "Older head content from the oversized chunk should be dropped");
                }
            });

            await RunTest("DiscardUnclaimedMissionOutput removes streamed buffer and final-message artifact", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    string missionId = "msn_discard_unclaimed_output";
                    string artifactDirectory = Path.Combine(Path.GetTempPath(), "armada_discard_output_" + Guid.NewGuid().ToString("N"));
                    string artifactPath = Path.Combine(artifactDirectory, missionId + ".txt");
                    Directory.CreateDirectory(artifactDirectory);

                    try
                    {
                        SeedMissionOutput(handler, missionId, "leftover streamed output");
                        RegisterFinalMessageArtifact(handler, missionId, artifactPath);
                        await File.WriteAllTextAsync(artifactPath, "leftover final message").ConfigureAwait(false);

                        handler.DiscardUnclaimedMissionOutput(missionId);

                        // After discard, retrieval should observe nothing -- buffer cleared and file removed.
                        string? output = handler.GetAndClearMissionOutput(missionId);
                        AssertNull(output, "DiscardUnclaimedMissionOutput should leave no streamed output behind");
                        AssertFalse(File.Exists(artifactPath), "DiscardUnclaimedMissionOutput should delete the final-message artifact");
                    }
                    finally
                    {
                        try { Directory.Delete(artifactDirectory, true); } catch { }
                    }
                }
            });

            await RunTest("HandleAgentOutput stores a papercut in the mission owner's scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    string tenantId = Armada.Core.Constants.DefaultTenantId;
                    string userId = Armada.Core.Constants.DefaultUserId;

                    Vessel vessel = new Vessel("PapercutScopeVessel", "https://github.com/test/papercut-scope");
                    Voyage voyage = new Voyage("Papercut scope voyage");
                    Captain captain = new Captain("papercut-scope-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Papercut scope mission");
                    mission.TenantId = tenantId;
                    mission.UserId = userId;
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.Persona = "Worker";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 838383;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    handler.HandleAgentOutput(
                        processId,
                        "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"Scope check\",\"path\":\"README.md\"}");

                    List<ArmadaEvent> stored = await WaitForPapercutEventsAsync(testDb.Driver, 1).ConfigureAwait(false);
                    AssertEqual(1, stored.Count, "The marker line should produce exactly one papercut event");

                    EnumerationQuery query = new EnumerationQuery { MissionId = mission.Id, EventType = PapercutParser.EventType, PageNumber = 1, PageSize = 10 };
                    EnumerationResult<ArmadaEvent> owner = await testDb.Driver.Events.EnumerateAsync(tenantId, userId, query).ConfigureAwait(false);
                    AssertEqual(1, owner.Objects.Count, "The mission owner's scoped read must find the papercut");
                    EnumerationResult<ArmadaEvent> other = await testDb.Driver.Events.EnumerateAsync(tenantId, "usr_other_owner", query).ConfigureAwait(false);
                    AssertEqual(0, other.Objects.Count, "Another user's scoped read must not find the papercut");
                }
            });

            await RunTest("HandleAgentOutput stores a papercut marker as an event", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);

                    Vessel vessel = new Vessel("PapercutVessel", "https://github.com/test/papercut");
                    Voyage voyage = new Voyage("Papercut voyage");
                    Captain captain = new Captain("papercut-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Papercut mission");
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.Persona = "Worker";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 828282;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    handler.HandleAgentOutput(
                        processId,
                        "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Medium\",\"title\":\"README names a build command that does not exist\",\"path\":\"README.md\"}");

                    List<ArmadaEvent> stored = await WaitForPapercutEventsAsync(testDb.Driver, 1).ConfigureAwait(false);

                    AssertEqual(1, stored.Count, "The marker line should produce exactly one papercut event");

                    Papercut? papercut = PapercutService.TryFromEvent(stored[0]);
                    AssertNotNull(papercut, "The stored event should read back as a papercut");
                    AssertEqual(PapercutCategoryEnum.MissingDoc, papercut!.Category);
                    AssertEqual(PapercutSeverityEnum.Medium, papercut.Severity);
                    AssertEqual("README.md", papercut.Path);

                    // The admiral supplies the context, never the captain.
                    AssertEqual(mission.Id, papercut.MissionId);
                    AssertEqual(captain.Id, papercut.CaptainId);
                    AssertEqual(vessel.Id, papercut.VesselId);
                    AssertEqual(voyage.Id, papercut.VoyageId);
                    AssertEqual("Cursor", papercut.Runtime);

                    // A papercut is not progress: it must not reach the progress signal stream.
                    List<Signal> signals = await testDb.Driver.Signals.EnumerateRecentAsync(50).ConfigureAwait(false);
                    AssertEqual(0, signals.Count, "A papercut must not be recorded as a progress signal");
                }
            });

            await RunTest("A delayed papercut cannot enter a later launch or spend its report limit", async () =>
            {
                foreach (bool changedReplay in new[] { false, true })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                        Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("delayed-feedback-owner")).ConfigureAwait(false);
                        Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Delayed prior feedback")
                        { Status = MissionStatusEnum.InProgress, Persona = "Worker", CaptainId = captain.Id }).ConfigureAwait(false);
                        long previousOwner = handler.TakeMissionStateOwnership(mission.Id);
                        handler.RegisterMissionProcess(989831, captain.Id, mission.Id, previousOwner);
                        DelayedMissionRead delay = DelayedMissionRead.Install(testDb.Driver, mission.Id);
                        FieldInfo ownersField = typeof(AgentLifecycleHandler).GetField("_MissionStateOwners", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        FieldInfo pendingField = typeof(AgentLifecycleHandler).GetField("_DeferredOutputWriteCounts", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        object owners = ownersField.GetValue(handler)!;
                        bool queuedThroughCompletionBoundary;
                        try
                        {
                            handler.HandleAgentOutput(989831,
                                "[ARMADA:PAPERCUT] {\"category\":\"Other\",\"severity\":\"Low\",\"title\":\"Prior launch feedback\"}");
                            await delay.Started.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                            lock (owners)
                                queuedThroughCompletionBoundary = ((Dictionary<long, int>)pendingField.GetValue(handler)!).ContainsKey(previousOwner);
                            using (Microsoft.Data.Sqlite.SqliteConnection connection = new Microsoft.Data.Sqlite.SqliteConnection(testDb.ConnectionString))
                            {
                                await connection.OpenAsync().ConfigureAwait(false);
                                using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
                                {
                                    command.CommandText = "UPDATE missions SET research_replay_id=@generation WHERE id=@id;";
                                    command.Parameters.AddWithValue("@generation", changedReplay ? (object)"rpl_later_feedback" : DBNull.Value);
                                    command.Parameters.AddWithValue("@id", mission.Id);
                                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                                }
                            }
                            long currentOwner = handler.TakeMissionStateOwnership(mission.Id, changedReplay ? "rpl_later_feedback" : null);
                            handler.RegisterMissionProcess(989832, captain.Id, mission.Id, currentOwner);
                        }
                        finally { delay.Release(); }
                        // Drain either implementation without a timing sleep, so the pre-fix path records the actual stale event.
                        if (queuedThroughCompletionBoundary)
                        {
                            await WaitForConditionAsync(() =>
                            {
                                lock (owners) return Task.FromResult(!((Dictionary<long, int>)pendingField.GetValue(handler)!).ContainsKey(previousOwner));
                            }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        }
                        else await WaitForPapercutEventsAsync(testDb.Driver, 1).ConfigureAwait(false);
                        List<ArmadaEvent> stale = await testDb.Driver.Events.EnumerateByTypeAsync(PapercutParser.EventType, 50).ConfigureAwait(false);
                        AssertEqual(0, stale.Count, "A prior launch's delayed feedback must not be attributed to the replacement attempt");
                        ConcurrentDictionary<string, int> counts = (ConcurrentDictionary<string, int>)typeof(AgentLifecycleHandler)
                            .GetField("_MissionPapercutCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
                        AssertFalse(counts.ContainsKey(mission.Id), "Rejected prior feedback must not spend the current launch's report limit");
                        handler.HandleAgentOutput(989832,
                            "[ARMADA:PAPERCUT] {\"category\":\"Other\",\"severity\":\"Low\",\"title\":\"Current launch feedback\"}");
                        List<ArmadaEvent> current = await WaitForPapercutEventsAsync(testDb.Driver, 1).ConfigureAwait(false);
                        AssertEqual("Current launch feedback", PapercutService.TryFromEvent(current[0])!.Title);
                        AssertEqual(1, counts[mission.Id], "The current launch keeps its own report limit");
                        await WaitForConditionAsync(() =>
                        {
                            lock (owners) return Task.FromResult(((Dictionary<long, int>)pendingField.GetValue(handler)!).Count == 0);
                        }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("A launch replacement waits for feedback that passed its ownership check", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("feedback-publication-owner")).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Feedback publication")
                    { Status = MissionStatusEnum.InProgress, Persona = "Worker", CaptainId = captain.Id }).ConfigureAwait(false);
                    long previousOwner = handler.TakeMissionStateOwnership(mission.Id);
                    handler.RegisterMissionProcess(989833, captain.Id, mission.Id, previousOwner);
                    DelayedMissionRead delay = DelayedMissionRead.Install(testDb.Driver, mission.Id, 2);
                    Task<long>? replacement = null;
                    bool replacementWaited = false;
                    try
                    {
                        handler.HandleAgentOutput(989833,
                            "[ARMADA:PAPERCUT] {\"category\":\"Other\",\"severity\":\"Low\",\"title\":\"Prior feedback being published\"}");
                        await delay.Started.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        replacement = ReplaceLaunchAsync();
                        replacementWaited = !replacement.IsCompleted;
                    }
                    finally { delay.Release(); }
                    long currentOwner = await replacement!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    handler.RegisterMissionProcess(989834, captain.Id, mission.Id, currentOwner);
                    List<ArmadaEvent> prior = await WaitForPapercutEventsAsync(testDb.Driver, 1).ConfigureAwait(false);
                    AssertTrue(replacementWaited, "Launch ownership must not change while checked feedback is still being published");
                    AssertEqual("Prior feedback being published", PapercutService.TryFromEvent(prior[0])!.Title);
                    ConcurrentDictionary<string, int> counts = (ConcurrentDictionary<string, int>)typeof(AgentLifecycleHandler)
                        .GetField("_MissionPapercutCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
                    AssertFalse(counts.ContainsKey(mission.Id), "Replacing the launch starts a fresh report limit after prior publication");
                    handler.HandleAgentOutput(989834,
                        "[ARMADA:PAPERCUT] {\"category\":\"Other\",\"severity\":\"Low\",\"title\":\"Replacement feedback\"}");
                    await WaitForPapercutEventsAsync(testDb.Driver, 2).ConfigureAwait(false);
                    AssertEqual(1, counts[mission.Id], "Only replacement feedback spends the new launch's report limit");
                    FieldInfo ownersField = typeof(AgentLifecycleHandler).GetField("_MissionStateOwners", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    FieldInfo pendingField = typeof(AgentLifecycleHandler).GetField("_DeferredOutputWriteCounts", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    object owners = ownersField.GetValue(handler)!;
                    await WaitForConditionAsync(() =>
                    {
                        lock (owners) return Task.FromResult(((Dictionary<long, int>)pendingField.GetValue(handler)!).Count == 0);
                    }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

                    async Task<long> ReplaceLaunchAsync()
                    {
                        using (IDisposable launchBoundary = await MissionExecutionBoundary.LaunchWrites.AcquireAsync(mission.Id).ConfigureAwait(false))
                            return handler.TakeMissionStateOwnership(mission.Id);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("HandleAgentOutput ignores a papercut from a judge mission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);

                    Vessel vessel = new Vessel("PapercutJudgeVessel", "https://github.com/test/papercut-judge");
                    Captain captain = new Captain("papercut-judge-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Judge mission");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Judge";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 838383;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    handler.HandleAgentOutput(
                        processId,
                        "[ARMADA:PAPERCUT] {\"category\":\"RepoFriction\",\"severity\":\"Low\",\"title\":\"the code under review is hard to follow\"}");

                    await Task.Delay(750).ConfigureAwait(false);

                    List<ArmadaEvent> stored = await testDb.Driver.Events
                        .EnumerateByTypeAsync(PapercutParser.EventType, 50)
                        .ConfigureAwait(false);

                    AssertEqual(0, stored.Count, "A judge reports through its verdict, not through papercuts");
                }
            });

            await RunTest("HandleAgentOutput caps stored papercuts per mission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);

                    Vessel vessel = new Vessel("PapercutCapVessel", "https://github.com/test/papercut-cap");
                    Captain captain = new Captain("papercut-cap-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Capped mission");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 848484;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    for (int i = 0; i < 18; i++)
                    {
                        handler.HandleAgentOutput(
                            processId,
                            "[ARMADA:PAPERCUT] {\"category\":\"Other\",\"severity\":\"Low\",\"title\":\"complaint number " + i + "\"}");
                    }

                    List<ArmadaEvent> stored = await WaitForPapercutEventsAsync(testDb.Driver, 10).ConfigureAwait(false);
                    await Task.Delay(500).ConfigureAwait(false);

                    stored = await testDb.Driver.Events
                        .EnumerateByTypeAsync(PapercutParser.EventType, 50)
                        .ConfigureAwait(false);

                    AssertEqual(10, stored.Count, "The per-mission cap must bound how many reports one mission can store");
                }
            });

            await RunTest("HandleAgentOutput_ToolActivitySignal_RecordsProviderProgress", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    ProviderProgressTracker tracker = new ProviderProgressTracker();
                    handler.SetProviderProgress(tracker);

                    Vessel vessel = new Vessel("ActivityVessel", "https://github.com/test/activity");
                    Voyage voyage = new Voyage("Activity voyage");
                    Captain captain = new Captain("activity-captain", AgentRuntimeEnum.ClaudeCode);
                    Mission mission = new Mission("Activity mission");
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.Persona = "Judge";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 929292;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    // A Judge running a long foreground tool call (a test suite) emits activity lines
                    // while its token-usage narration is quiet. That activity must refresh provider
                    // progress so the captain is not misclassified as a provider_silent_stall.
                    handler.HandleAgentOutput(processId, "[ARMADA:ACTIVITY] tool bash dotnet test src/Foo.Tests (ok)");

                    bool recorded = tracker.TryGet(captain.Id, out DateTime? progressUtc);
                    AssertTrue(recorded && progressUtc.HasValue,
                        "A tool-activity signal must refresh provider progress, or an actively-working captain is nudged mid-work");
                }
            });

            foreach (string mode in new[] { "capture-failed", "provider-74", "diagnostic-disposed" })
            {
                await RunTest("Trusted exit delivery: " + mode, async () =>
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    LoggingModule logging = CreateLogging();
                    TaskCompletionSource<int?> exitSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    int calls = 0;
                    StubAdmiralService admiral = new StubAdmiralService();
                    admiral.OnHandleProcessExit = (pid, code, cpt, msn) =>
                    {
                        Interlocked.Increment(ref calls);
                        exitSeen.TrySetResult(code);
                        return Task.CompletedTask;
                    };
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _, admiralOverride: admiral, loggingOverride: logging);
                    IntentionalProcessStops stops = new IntentionalProcessStops();
                    handler.SetIntentionalStops(stops);
                    int processId = mode == "capture-failed" ? 939391 : mode == "provider-74" ? 939392 : 939394;
                    Mission mission = await CreateWorkingMissionAsync(testDb.Driver, "exit-" + mode, processId).ConfigureAwait(false);
                    RegisterTrackedProcess(handler, processId, mission.CaptainId!, mission.Id);
                    Armada.Core.ProcessSupervisor.RecordLaunchedProcess(processId, DateTime.UtcNow);
                    Armada.Core.RuntimeProcessExitEvidence evidence = Armada.Core.ProcessSupervisor.TrackRuntimeExit(processId);
                    evidence.Complete(mode == "diagnostic-disposed" ? 1 : 74, mode == "capture-failed");
                    try
                    {
                        if (mode != "diagnostic-disposed")
                            AssertTrue(stops.TryRegister(processId, mission.CaptainId!, mission.Id, IntentionalStopKindEnum.Completion));
                        else
                        {
                            logging.Dispose();
                            bool threw = false;
                            try { logging.Info("prove the diagnostic sink is disposed"); }
                            catch (ObjectDisposedException) { threw = true; }
                            AssertTrue(threw, "the real disposed logger must throw before testing containment");
                        }
                        handler.HandleAgentProcessExited(processId, mode == "diagnostic-disposed" ? 1 : 74);
                        Task finished = await Task.WhenAny(exitSeen.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                        AssertTrue(finished == exitSeen.Task, "the effective exit reaches durable handling despite diagnostic failure: " + mode);
                        AssertEqual(mode == "capture-failed" ? 74 : mode == "provider-74" ? 0 : 1, exitSeen.Task.Result,
                            "only trusted capture failure defeats completion-stop success: " + mode);
                        await WaitForProcessExitCompletedAsync(handler, processId).ConfigureAwait(false);
                        AssertEqual(1, calls, "the exit is delivered exactly once");
                        AssertTrue(Armada.Core.ProcessSupervisor.GetRuntimeExitEvidence(processId) == null,
                            "durably delivered launch evidence is released");
                    }
                    finally
                    {
                        Armada.Core.ProcessSupervisor.ForgetLaunchedProcess(processId);
                        logging.Dispose();
                    }
                });
            }

            await RunTest("Trusted log capture failure defeats a saved success verdict in the real admiral", async () =>
            {
                foreach (bool captureFailed in new[] { false, true })
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git);
                    AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captains, missions,
                        new VoyageService(logging, testDb.Driver), docks, git: git);
                    int processId = captureFailed ? 939395 : 939396;
                    Mission mission = await CreateWorkingMissionAsync(testDb.Driver, "verdict-" + captureFailed, processId).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.Combine(settings.LogDirectory, "missions"));
                    File.WriteAllText(Path.Combine(settings.LogDirectory, "missions", mission.Id + ".log"), "[ARMADA:RESULT] COMPLETE\n");
                    Armada.Core.ProcessSupervisor.RecordLaunchedProcess(processId, DateTime.UtcNow);
                    Armada.Core.ProcessSupervisor.TrackRuntimeExit(processId).Complete(74, captureFailed);
                    try
                    {
                        await admiral.HandleProcessExitAsync(processId, 74, mission.CaptainId!, mission.Id).ConfigureAwait(false);
                        Mission? result = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(captureFailed ? MissionStatusEnum.Failed : MissionStatusEnum.WorkProduced, result!.Status,
                            "trusted persistence failure takes precedence, while ordinary provider exit74 retains explicit-success policy");
                        if (captureFailed) AssertContains("log persistence", result.FailureReason ?? "", "the durable failure names the lost evidence");
                    }
                    finally
                    {
                        Armada.Core.ProcessSupervisor.ForgetLaunchedProcess(processId);
                        Directory.Delete(settings.LogDirectory, true);
                    }
                }
            });

            await RunTest("A process that outlives its first terminal marker is stopped after the grace period and completes cleanly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StopRecordingRuntime runtime = new StopRecordingRuntime();
                    TaskCompletionSource<int?> exitSeen = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    StubAdmiralService admiral = new StubAdmiralService();
                    admiral.OnHandleProcessExit = (pid, code, cpt, msn) =>
                    {
                        exitSeen.TrySetResult(code);
                        return Task.CompletedTask;
                    };
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, null, admiral,
                        new StopRecordingRuntimeFactory(CreateLogging(), runtime));
                    settings.AutonomousRecovery.TerminalMarkerGraceSeconds = 60;
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);

                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("verdict-captain", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Judge mission") { Persona = "Judge", CaptainId = captain.Id }).ConfigureAwait(false);

                    int processId = 939393;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    handler.HandleAgentOutput(processId, "## Verdict");
                    AssertFalse(markers.TryGet(mission.Id, out _), "Prose is not a terminal marker.");

                    handler.HandleAgentOutput(processId, "[ARMADA:VERDICT] PASS");
                    handler.HandleAgentOutput(processId, "[ARMADA:VERDICT] NEEDS_REVISION");
                    AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? first), "The verdict line is recorded.");
                    AssertEqual("PASS", first!.Value, "A later re-review never replaces the first verdict.");

                    bool stoppedEarly = await handler.EnforceTerminalMarkerGraceAsync(
                        processId, captain.Id, mission.Id, first.FirstSeenUtc.AddSeconds(30)).ConfigureAwait(false);
                    AssertFalse(stoppedEarly, "Inside the grace period the process may still exit on its own.");
                    AssertEqual(0, runtime.StopCalls.Count);

                    bool stopped = await handler.EnforceTerminalMarkerGraceAsync(
                        processId, captain.Id, mission.Id, first.FirstSeenUtc.AddSeconds(61)).ConfigureAwait(false);
                    AssertTrue(stopped, "After the grace period the handler stops the process.");
                    AssertEqual(1, runtime.StopCalls.Count);
                    AssertEqual(processId, runtime.StopCalls[0]);

                    // The stop kills the process, which reports a non-zero exit. It must still complete.
                    handler.HandleAgentProcessExited(processId, 137);
                    Task finished = await Task.WhenAny(exitSeen.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                    AssertTrue(finished == exitSeen.Task, "The exit reaches the admiral.");
                    AssertEqual(0, exitSeen.Task.Result, "A process stopped after its terminal marker completes as a clean exit.");
                    await WaitForProcessExitCompletedAsync(handler, processId).ConfigureAwait(false);
                    AssertFalse(markers.TryGet(mission.Id, out _), "The marker is cleared once the process has exited.");
                }
            });

            await RunTest("A captain that ends BLOCKED and keeps running is finished: no stall nudge, stopped after the grace period", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StopRecordingRuntime runtime = new StopRecordingRuntime();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, null, new StubAdmiralService(),
                        new StopRecordingRuntimeFactory(CreateLogging(), runtime));
                    settings.AutonomousRecovery.TerminalMarkerGraceSeconds = 60;
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);

                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("blocked-captain", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Worker mission") { Persona = "Worker", CaptainId = captain.Id }).ConfigureAwait(false);

                    int processId = 939494;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    handler.HandleAgentOutput(processId, "If the owner must decide, end with [ARMADA:RESULT] BLOCKED.");
                    AssertFalse(markers.TryGet(mission.Id, out _), "A marker in the middle of a line does not end the stage.");

                    handler.HandleAgentOutput(processId, "[ARMADA:RESULT] BLOCKED: which retention window is authoritative, 30 or 90 days?");
                    AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? first), "A blocked result ends the stage, so the stall nudge is withheld.");
                    AssertEqual("BLOCKED", first!.Value, "The record keeps the marker word, not the question.");
                    AssertFalse(ProgressParser.HasTerminalMarker("[ARMADA:RESULT] BLOCKED"), "A blocked result is still not a completion claim.");

                    bool stoppedEarly = await handler.EnforceTerminalMarkerGraceAsync(
                        processId, captain.Id, mission.Id, first.FirstSeenUtc.AddSeconds(30)).ConfigureAwait(false);
                    AssertFalse(stoppedEarly, "Inside the grace period the process may still exit on its own.");

                    bool stopped = await handler.EnforceTerminalMarkerGraceAsync(
                        processId, captain.Id, mission.Id, first.FirstSeenUtc.AddSeconds(61)).ConfigureAwait(false);
                    AssertTrue(stopped, "After the grace period the handler stops the waiting process.");
                    AssertEqual(1, runtime.StopCalls.Count);
                }
            });

            await RunTest("A refused stop after the terminal marker grace is reported as refused, not stopped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StopRecordingRuntime runtime = new StopRecordingRuntime();
                    runtime.NextStopResult = AgentStopResult.Refused(AgentStopResult.IdentityUnverifiedReason, "no stored start time");
                    List<string> events = new List<string>();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, null, new StubAdmiralService(),
                        new StopRecordingRuntimeFactory(CreateLogging(), runtime), null, events);
                    settings.AutonomousRecovery.TerminalMarkerGraceSeconds = 60;
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);

                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("refused-captain", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Judge mission") { Persona = "Judge", CaptainId = captain.Id }).ConfigureAwait(false);
                    int processId = 939394;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    handler.HandleAgentOutput(processId, "[ARMADA:VERDICT] PASS");
                    AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? marker), "The verdict line is recorded.");

                    bool stopped = await handler.EnforceTerminalMarkerGraceAsync(
                        processId, captain.Id, mission.Id, marker!.FirstSeenUtc.AddSeconds(61)).ConfigureAwait(false);
                    AssertFalse(stopped, "A refused stop is not reported as a stop.");
                    AssertEqual(1, runtime.StopCalls.Count);
                    lock (events) AssertTrue(events.Contains("captain.stop_refused"), "The refusal is recorded as captain.stop_refused.");

                    // The refused stop released its intentional-stop registration, so the next tick tries again.
                    bool retried = await handler.EnforceTerminalMarkerGraceAsync(
                        processId, captain.Id, mission.Id, marker.FirstSeenUtc.AddSeconds(62)).ConfigureAwait(false);
                    AssertFalse(retried, "The retry is refused the same way.");
                    AssertEqual(2, runtime.StopCalls.Count, "A refused stop does not leave the process marked as intentionally stopped.");
                }
            });

            await RunTest("A captain stop that stops the process records captain.stopped; a refused one records only the refusal", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StopRecordingRuntime runtime = new StopRecordingRuntime();
                    List<string> events = new List<string>();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings _, null, new StubAdmiralService(),
                        new StopRecordingRuntimeFactory(CreateLogging(), runtime), null, events);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("stopped-captain", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                    captain.ProcessId = 939395;

                    runtime.NextStopResult = AgentStopResult.Stopped();
                    await handler.HandleStopAgentAsync(captain).ConfigureAwait(false);
                    lock (events)
                    {
                        AssertTrue(events.Contains("captain.stopped"), "A stopped process is recorded as captain.stopped.");
                        AssertFalse(events.Contains("captain.stop_refused"), "A stopped process is not a refusal.");
                        events.Clear();
                    }

                    runtime.NextStopResult = AgentStopResult.Refused(AgentStopResult.KillFailedReason, "kill failed");
                    await handler.HandleStopAgentAsync(captain).ConfigureAwait(false);
                    lock (events)
                    {
                        AssertTrue(events.Contains("captain.stop_refused"), "A refused stop is recorded as captain.stop_refused.");
                        AssertFalse(events.Contains("captain.stopped"), "A refused stop is never recorded as stopped.");
                        events.Clear();
                    }

                    runtime.NextStopResult = AgentStopResult.NotRunning();
                    await handler.HandleStopAgentAsync(captain).ConfigureAwait(false);
                    lock (events) AssertFalse(events.Contains("captain.stopped"), "A process that was already gone records no stop.");
                    AssertEqual(3, runtime.StopCalls.Count, "Each captain stop reaches the runtime.");
                }
            });

            // ----------------------------------------------------------------
            // A captain stop's kill exit is the stop, never an out-of-memory kill
            // ----------------------------------------------------------------

            await RunTest("A captain stop's kill exit is recorded as the stop and not requeued as an out-of-memory kill", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int stoppedPid = 989811;
                    IntentionalProcessStops stops = new IntentionalProcessStops();
                    AdmiralService admiral = CreateRealAdmiral(testDb.Driver, stops);
                    StopRecordingRuntime runtime = new StopRecordingRuntime();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings _, null, admiral,
                        new StopRecordingRuntimeFactory(CreateLogging(), runtime));
                    handler.SetIntentionalStops(stops);
                    Mission mission = await CreateWorkingMissionAsync(testDb.Driver, "stopped-captain-kill", stoppedPid).ConfigureAwait(false);
                    Captain captain = (await testDb.Driver.Captains.ReadAsync(mission.CaptainId!).ConfigureAwait(false))!;
                    RegisterTrackedProcess(handler, stoppedPid, captain.Id, mission.Id);

                    // The stop kills the process; a recall's own stop that follows finds it already gone.
                    runtime.NextStopResult = AgentStopResult.Stopped();
                    await handler.HandleStopAgentAsync(captain).ConfigureAwait(false);
                    runtime.NextStopResult = AgentStopResult.NotRunning();
                    await handler.HandleStopAgentAsync(captain).ConfigureAwait(false);

                    handler.HandleAgentProcessExited(stoppedPid, 137);
                    await WaitForProcessExitCompletedAsync(handler, stoppedPid).ConfigureAwait(false);

                    Mission after = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.InProgress, after.Status, "the stop's requester, not the kill exit, decides the mission's next state");
                    AssertFalse((after.FailureReason ?? String.Empty).StartsWith("OOM:", StringComparison.Ordinal), "a requested stop is never recorded as an out-of-memory kill");
                    List<ArmadaEvent> requeued = await testDb.Driver.Events.EnumerateByTypeAsync("mission.retry_requeued", 10).ConfigureAwait(false);
                    AssertEqual(0, requeued.Count, "a requested stop is not requeued as a transient resource failure");
                    List<ArmadaEvent> ignored = await testDb.Driver.Events.EnumerateByTypeAsync("captain.process_exit_ignored", 10).ConfigureAwait(false);
                    AssertEqual(1, ignored.Count, "the kill exit is recorded as the stop");
                    AssertContains("captain stop", ignored[0].Message, "the record names the stop that killed the process");
                    AssertEqual(0, stops.Count, "the exit consumes the stop's registration");
                }
            });

            await RunTest("A captain stop that stopped nothing leaves a later kill exit classified as an out-of-memory kill", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int killedPid = 989812;
                    IntentionalProcessStops stops = new IntentionalProcessStops();
                    AdmiralService admiral = CreateRealAdmiral(testDb.Driver, stops);
                    StopRecordingRuntime runtime = new StopRecordingRuntime();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings _, null, admiral,
                        new StopRecordingRuntimeFactory(CreateLogging(), runtime));
                    handler.SetIntentionalStops(stops);
                    Mission mission = await CreateWorkingMissionAsync(testDb.Driver, "unstopped-captain-kill", killedPid).ConfigureAwait(false);
                    Captain captain = (await testDb.Driver.Captains.ReadAsync(mission.CaptainId!).ConfigureAwait(false))!;
                    RegisterTrackedProcess(handler, killedPid, captain.Id, mission.Id);

                    // The stop found no running process, so the kill that ends the process is not this stop's.
                    runtime.NextStopResult = AgentStopResult.NotRunning();
                    await handler.HandleStopAgentAsync(captain).ConfigureAwait(false);
                    AssertEqual(0, stops.Count, "a stop that stopped nothing withdraws its registration");

                    handler.HandleAgentProcessExited(killedPid, 137);
                    await WaitForProcessExitCompletedAsync(handler, killedPid).ConfigureAwait(false);

                    Mission after = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Pending, after.Status, "an exit 137 no Armada stop explains is requeued");
                    AssertTrue((after.FailureReason ?? String.Empty).StartsWith("OOM:", StringComparison.Ordinal), "an exit 137 no Armada stop explains is classified as an out-of-memory kill");
                    List<ArmadaEvent> requeued = await testDb.Driver.Events.EnumerateByTypeAsync("mission.retry_requeued", 10).ConfigureAwait(false);
                    AssertEqual(1, requeued.Count, "the out-of-memory kill is requeued as a transient resource failure");
                }
            });

            // ----------------------------------------------------------------
            // A superseded process's exit never clears the relaunched process's per-mission state
            // ----------------------------------------------------------------

            await RunTest("A superseded process's exit leaves the relaunched process's output, terminal marker and heartbeat throttle in place", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int supersededPid = 989801;
                    const int relaunchedPid = 989802;
                    IntentionalProcessStops stops = new IntentionalProcessStops();
                    AdmiralService admiral = CreateRealAdmiral(testDb.Driver, stops);
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _, null, admiral);
                    handler.SetIntentionalStops(stops);
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);

                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("relaunch-captain", AgentRuntimeEnum.ClaudeCode)
                    {
                        State = CaptainStateEnum.Working,
                        ProcessId = relaunchedPid
                    }).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Relaunched mission")
                    {
                        Status = MissionStatusEnum.InProgress,
                        CaptainId = captain.Id,
                        ProcessId = relaunchedPid
                    }).ConfigureAwait(false);
                    captain.CurrentMissionId = mission.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    // The admiral stopped the old process to relaunch the mission; the new process is the captain's current one.
                    AssertTrue(stops.TryRegister(supersededPid, captain.Id, mission.Id, IntentionalStopKindEnum.Superseded), "the old process is registered as superseded");
                    RegisterTrackedProcess(handler, supersededPid, captain.Id, mission.Id);
                    RegisterTrackedProcess(handler, relaunchedPid, captain.Id, mission.Id);

                    handler.HandleAgentOutput(relaunchedPid, "relaunched process early output");
                    handler.HandleAgentHeartbeat(relaunchedPid, "relaunched process early output");
                    handler.HandleAgentOutput(relaunchedPid, "[ARMADA:RESULT] COMPLETE");
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 1).ConfigureAwait(false);
                    AssertTrue(markers.TryGet(mission.Id, out _), "the relaunched process's terminal marker is recorded");
                    AssertTrue(HasMissionHeartbeatThrottle(handler, mission.Id), "the relaunched process's heartbeat throttle is recorded");

                    // The old process's kill arrives after the relaunch started writing.
                    handler.HandleAgentProcessExited(supersededPid, 137);
                    await WaitForProcessExitCompletedAsync(handler, supersededPid).ConfigureAwait(false);

                    AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? marker), "the relaunched process's terminal marker survives the superseded exit");
                    AssertEqual("COMPLETE", marker!.Value);
                    AssertTrue(HasMissionHeartbeatThrottle(handler, mission.Id), "the relaunched process's heartbeat throttle survives the superseded exit");
                    string output = handler.GetAndClearMissionOutput(mission.Id) ?? "";
                    AssertContains("relaunched process early output", output, "the relaunched process's output survives the superseded exit");

                    Mission after = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.InProgress, after.Status, "the superseded exit is not the mission's outcome");
                }
            });

            await RunTest("Output and terminal markers from a prior process cannot contaminate a later launch of the same mission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("output-generation-owner")
                    { State = CaptainStateEnum.Working, ProcessId = 989822 }).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Same-ID later launch")
                    { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id, ProcessId = 989822 }).ConfigureAwait(false);
                    RegisterTrackedProcess(handler, 989821, captain.Id, mission.Id);
                    handler.HandleAgentOutput(989821, "Prior attempt output.");
                    RegisterTrackedProcess(handler, 989822, captain.Id, mission.Id);
                    handler.HandleAgentOutput(989822, "Current attempt output.");
                    handler.HandleAgentOutput(989821, "Stale output must stay outside the current report.");
                    handler.HandleAgentOutput(989821, "[ARMADA:RESULT] COMPLETE");
                    AssertFalse(markers.TryGet(mission.Id, out _), "An old launch cannot supply the current terminal marker");
                    string output = handler.GetAndClearMissionOutput(mission.Id) ?? String.Empty;
                    AssertContains("Current attempt output", output);
                    AssertFalse(output.Contains("Stale output", StringComparison.Ordinal));
                    AssertFalse(output.Contains("Prior attempt output", StringComparison.Ordinal));
                    handler.HandleAgentOutput(989822, "[ARMADA:RESULT] COMPLETE");
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 1).ConfigureAwait(false);
                    AssertTrue(markers.TryGet(mission.Id, out _), "The current launch still supplies its own terminal marker");
                }
            }).ConfigureAwait(false);

            await RunTest("An old runtime subscriber cannot write after the operating system PID is reused", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    TerminalMarkerTracker markers = new TerminalMarkerTracker(); handler.SetTerminalMarkers(markers);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("reused-output-pid")).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Reused process identity")
                    { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id }).ConfigureAwait(false);
                    long previous = handler.TakeMissionStateOwnership(mission.Id);
                    handler.RegisterMissionProcess(989826, captain.Id, mission.Id, previous);
                    long current = handler.TakeMissionStateOwnership(mission.Id);
                    handler.RegisterMissionProcess(989826, captain.Id, mission.Id, current);
                    handler.HandleAgentOutputForLaunch(989826, "[ARMADA:RESULT] COMPLETE", previous);
                    AssertFalse(markers.TryGet(mission.Id, out _));
                    AssertNull(handler.GetAndClearMissionOutput(mission.Id));
                    handler.HandleAgentOutputForLaunch(989826, "Current process content", current);
                    AssertContains("Current process content", handler.GetAndClearMissionOutput(mission.Id) ?? String.Empty);
                }
            }).ConfigureAwait(false);

            await RunTest("A delayed process-start callback cannot bind a later pending launch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    string launchKey = "captain:mission";
                    RegisterPendingLaunch(handler, launchKey, "captain", "mission");
                    ConcurrentDictionary<string, long> owners = (ConcurrentDictionary<string, long>)typeof(AgentLifecycleHandler)
                        .GetField("_PendingLaunchOwners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
                    long previous = owners[launchKey];
                    RegisterPendingLaunch(handler, launchKey, "captain", "mission");
                    long current = owners[launchKey];
                    MethodInfo started = typeof(AgentLifecycleHandler).GetMethod("HandleProcessStarted", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    started.Invoke(handler, new object[] { 989827, launchKey, previous });
                    handler.HandleAgentOutputForLaunch(989827, "Old started callback", current);
                    AssertNull(handler.GetAndClearMissionOutput("mission"));
                    started.Invoke(handler, new object[] { 989828, launchKey, current });
                    handler.HandleAgentOutputForLaunch(989828, "Current started callback", current);
                    AssertContains("Current started callback", handler.GetAndClearMissionOutput("mission") ?? String.Empty);
                }
            }).ConfigureAwait(false);

            await RunTest("A replay launch can report progress before its PID persistence finishes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("early-replay-progress")
                    { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Early replay progress")
                    { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id }).ConfigureAwait(false);
                    const string replayId = "rpl_early_progress";
                    using (Microsoft.Data.Sqlite.SqliteConnection connection = new Microsoft.Data.Sqlite.SqliteConnection(testDb.ConnectionString))
                    {
                        await connection.OpenAsync().ConfigureAwait(false);
                        using (Microsoft.Data.Sqlite.SqliteCommand update = connection.CreateCommand())
                        {
                            update.CommandText = "UPDATE missions SET research_replay_id=@generation WHERE id=@id;";
                            update.Parameters.AddWithValue("@generation", replayId); update.Parameters.AddWithValue("@id", mission.Id);
                            await update.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }
                    handler.RegisterMissionProcess(989823, captain.Id, mission.Id, handler.TakeMissionStateOwnership(mission.Id, replayId));
                    AssertNull((await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!.ProcessId);
                    handler.HandleAgentOutput(989823, "[ARMADA:STATUS] Testing");
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 1).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Testing, (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!.Status);
                }
            }).ConfigureAwait(false);

            await RunTest("Only the just-completed launch can deliver a final note queued behind completion", async () =>
            {
                foreach (bool interveningLaunch in new[] { false, true })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        AdmiralService admiral = CreateRealAdmiral(testDb.Driver, new IntentionalProcessStops());
                        AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _, null, admiral);
                        CoordinationService coordination = new CoordinationService(CreateLogging(), testDb.Driver);
                        handler.SetCoordinationService(coordination);
                        Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("final-note-generation")
                        { State = CaptainStateEnum.Working, ProcessId = 989824 }).ConfigureAwait(false);
                        Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Final note generation")
                        { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id, ProcessId = 989824 }).ConfigureAwait(false);
                        RegisterTrackedProcess(handler, 989824, captain.Id, mission.Id);
                        using (await MissionExecutionBoundary.Locks.AcquireAsync(mission.Id).ConfigureAwait(false))
                        {
                            handler.HandleAgentOutput(989824, "[ARMADA:NOTE] Final verified same-generation result.");
                            int completingPid = 989824;
                            if (interveningLaunch)
                            {
                                completingPid = 989825;
                                RegisterTrackedProcess(handler, completingPid, captain.Id, mission.Id);
                                captain.ProcessId = completingPid;
                                await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                            }
                            mission.Status = MissionStatusEnum.Complete; mission.ProcessId = null; mission.CompletedUtc = DateTime.UtcNow;
                            await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                            handler.HandleAgentProcessExited(completingPid, 0);
                            await WaitForProcessExitCompletedAsync(handler, completingPid).ConfigureAwait(false);
                        }
                        FieldInfo ownersField = typeof(AgentLifecycleHandler).GetField("_MissionStateOwners", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        FieldInfo pendingField = typeof(AgentLifecycleHandler).GetField("_DeferredOutputWriteCounts", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        await WaitForConditionAsync(() =>
                        {
                            object owners = ownersField.GetValue(handler)!;
                            lock (owners)
                                return Task.FromResult(((Dictionary<long, int>)pendingField.GetValue(handler)!).Count == 0);
                        }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        List<CoordinationMessage> notes = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                        AssertEqual(interveningLaunch ? 0 : 1, notes.Count,
                            "A completed later retry cannot authorize an older queued note");
                        if (!interveningLaunch) AssertEqual("Final verified same-generation result.", notes[0].Content);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("The current process's exit clears its mission's output, terminal marker and heartbeat throttle", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int currentPid = 989803;
                    AdmiralService admiral = CreateRealAdmiral(testDb.Driver, new IntentionalProcessStops());
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _, null, admiral);
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);

                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("current-exit-captain", AgentRuntimeEnum.ClaudeCode)
                    {
                        State = CaptainStateEnum.Working,
                        ProcessId = currentPid
                    }).ConfigureAwait(false);
                    // A terminal mission keeps the admiral from acting on the exit, so only the cleanup is observed.
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Finished mission")
                    {
                        Status = MissionStatusEnum.Complete,
                        CaptainId = captain.Id,
                        ProcessId = currentPid
                    }).ConfigureAwait(false);

                    RegisterTrackedProcess(handler, currentPid, captain.Id, mission.Id);
                    handler.HandleAgentOutput(currentPid, "current process output");
                    handler.HandleAgentHeartbeat(currentPid, "current process output");
                    handler.HandleAgentOutput(currentPid, "[ARMADA:RESULT] COMPLETE");
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 1).ConfigureAwait(false);

                    handler.HandleAgentProcessExited(currentPid, 0);
                    await WaitForProcessExitCompletedAsync(handler, currentPid).ConfigureAwait(false);

                    AssertFalse(markers.TryGet(mission.Id, out _), "the current process's exit clears the terminal marker");
                    AssertFalse(HasMissionHeartbeatThrottle(handler, mission.Id), "the current process's exit clears the heartbeat throttle");
                    AssertNull(handler.GetAndClearMissionOutput(mission.Id), "the current process's exit discards the unclaimed output");
                }
            });

            await RunTest("A relaunch made while the admiral handles the current process's exit keeps its output and terminal marker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SyntheticLaunchRuntime runtime = new SyntheticLaunchRuntime { NextProcessId = 989811 };
                    StubAdmiralService admiral = new StubAdmiralService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _, null, admiral,
                        new StopRecordingRuntimeFactory(CreateLogging(), runtime));
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);
                    string worktreePath = Path.Combine(Path.GetTempPath(), "armada_inner_relaunch_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktreePath);

                    try
                    {
                        Mission mission = new Mission("Inner relaunch mission")
                        {
                            Persona = "Worker",
                            BranchName = "feature/inner-relaunch",
                            Status = MissionStatusEnum.InProgress
                        };
                        Captain captain = new Captain("inner-relaunch-captain", AgentRuntimeEnum.Cursor)
                        {
                            Model = "inner-relaunch-model",
                            State = CaptainStateEnum.Working,
                            CurrentMissionId = mission.Id
                        };
                        mission.CaptainId = captain.Id;
                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                        Dock dock = new Dock { BranchName = "feature/inner-relaunch", WorktreePath = worktreePath };

                        int firstPid = await handler.HandleLaunchAgentAsync(captain, mission, dock).ConfigureAwait(false);
                        handler.HandleAgentOutput(firstPid, "first process output");

                        // Handling the first process's exit re-dispatches the mission, and the new process
                        // writes output and its terminal marker before the exit handling returns.
                        int relaunchedPid = 0;
                        admiral.OnHandleProcessExit = async (pid, code, cpt, msn) =>
                        {
                            if (pid != firstPid) return;
                            runtime.NextProcessId = 989812;
                            relaunchedPid = await handler.HandleLaunchAgentAsync(captain, mission, dock).ConfigureAwait(false);
                            handler.HandleAgentOutput(relaunchedPid, "relaunched process output");
                            handler.HandleAgentOutput(relaunchedPid, "[ARMADA:RESULT] COMPLETE");
                        };

                        handler.HandleAgentProcessExited(firstPid, 1);
                        await WaitForProcessExitCompletedAsync(handler, firstPid).ConfigureAwait(false);
                        await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 1).ConfigureAwait(false);

                        AssertEqual(989812, relaunchedPid, "the mission was relaunched inside the exit handling");
                        AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? marker), "the relaunched process's terminal marker survives the exit that relaunched it");
                        AssertEqual("COMPLETE", marker!.Value);
                        string output = handler.GetAndClearMissionOutput(mission.Id) ?? "";
                        AssertContains("relaunched process output", output, "the relaunched process's output survives the exit that relaunched it");
                        AssertFalse(output.Contains("first process output", StringComparison.Ordinal), "the first process's output is not carried into the relaunch");
                    }
                    finally
                    {
                        try { Directory.Delete(worktreePath, true); } catch { }
                    }
                }
            });

            await RunTest("A launch starts its mission with no output, terminal marker or heartbeat throttle left by an earlier process", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimScope shim = CursorShimScope.Create())
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);
                    string worktreePath = Path.Combine(Path.GetTempPath(), "armada_relaunch_state_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktreePath);

                    try
                    {
                        Mission mission = new Mission("Relaunch state mission")
                        {
                            Persona = "Worker",
                            BranchName = "feature/relaunch-state",
                            Status = MissionStatusEnum.InProgress
                        };
                        Captain captain = new Captain("relaunch-state-captain", AgentRuntimeEnum.Cursor)
                        {
                            Model = "relaunch-model",
                            State = CaptainStateEnum.Working,
                            CurrentMissionId = mission.Id
                        };
                        mission.CaptainId = captain.Id;
                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        // An earlier process for the mission wrote output and a terminal marker, and its exit never cleared them.
                        const int earlierPid = 989804;
                        RegisterTrackedProcess(handler, earlierPid, captain.Id, mission.Id);
                        handler.HandleAgentOutput(earlierPid, "earlier process output");
                        handler.HandleAgentHeartbeat(earlierPid, "earlier process output");
                        handler.HandleAgentOutput(earlierPid, "[ARMADA:RESULT] COMPLETE");
                        await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 1).ConfigureAwait(false);
                        AssertTrue(markers.TryGet(mission.Id, out _), "the earlier process's marker is recorded");

                        Dock dock = new Dock { BranchName = "feature/relaunch-state", WorktreePath = worktreePath };
                        DateTime launchStartUtc = DateTime.UtcNow;
                        await handler.HandleLaunchAgentAsync(captain, mission, dock).ConfigureAwait(false);

                        AssertFalse(markers.TryGet(mission.Id, out _), "a launch never inherits an earlier process's terminal marker");
                        // The launched shim prints a line, which may stamp a fresh throttle for the new process
                        // before this read; only a stamp older than the launch is inherited.
                        DateTime? throttle = MissionHeartbeatThrottle(handler, mission.Id);
                        AssertTrue(throttle == null || throttle.Value >= launchStartUtc, "a launch never inherits an earlier process's heartbeat throttle");
                        string output = handler.GetAndClearMissionOutput(mission.Id) ?? "";
                        AssertFalse(output.Contains("earlier process output", StringComparison.Ordinal), "a launch never inherits an earlier process's output");
                    }
                    finally
                    {
                        try { Directory.Delete(worktreePath, true); } catch { }
                    }
                }
            });

            // ----------------------------------------------------------------
            // Agent status markers report progress only; completion runs its checks
            // ----------------------------------------------------------------

            await RunTest("Agent status markers cannot move a mission to a post-work or terminal status", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("status-captain", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(
                        new Mission("Status marker mission") { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id }).ConfigureAwait(false);

                    int processId = 949494;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    MissionStatusEnum[] refused = new[]
                    {
                        MissionStatusEnum.WorkProduced, MissionStatusEnum.Complete, MissionStatusEnum.Failed,
                        MissionStatusEnum.Cancelled, MissionStatusEnum.PullRequestOpen, MissionStatusEnum.LandingFailed
                    };
                    int expectedSignals = 0;
                    foreach (MissionStatusEnum status in refused)
                    {
                        handler.HandleAgentOutput(processId, "[ARMADA:STATUS] " + status);
                        expectedSignals++;
                        await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, expectedSignals).ConfigureAwait(false);
                        Mission? afterRefused = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.InProgress, afterRefused!.Status, "An agent status marker must not set " + status);
                    }

                    // A progress status is still applied, and from Testing a terminal marker is still refused.
                    handler.HandleAgentOutput(processId, "[ARMADA:STATUS] Testing");
                    expectedSignals++;
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, expectedSignals).ConfigureAwait(false);
                    Mission? testing = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Testing, testing!.Status, "A progress status marker is applied");

                    handler.HandleAgentOutput(processId, "[ARMADA:STATUS] Complete");
                    expectedSignals++;
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, expectedSignals).ConfigureAwait(false);
                    Mission? stillTesting = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Testing, stillTesting!.Status, "Testing to Complete is not an agent-reportable transition");
                }
            });

            await RunTest("Completion still runs after a captain emits a Complete status marker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService,
                        resourcePressureAdmission: global::Test.Shared.Infrastructure.TestResourcePressure.Unconstrained(settings));

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("status-vessel", "https://github.com/test/status.git")).ConfigureAwait(false);
                    Captain captain = new Captain("completion-captain", AgentRuntimeEnum.ClaudeCode);
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/completion-captain/msn_status";
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                    Mission mission = new Mission("Completion gate mission");
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.CaptainId = captain.Id;
                    mission.DockId = dock.Id;
                    mission.VesselId = vessel.Id;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    captain.CurrentMissionId = mission.Id;
                    captain.CurrentDockId = dock.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    int processId = 959595;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    handler.HandleAgentOutput(processId, "[ARMADA:STATUS] Complete");
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 1).ConfigureAwait(false);

                    // The exit path's completion handler skips a mission already in a post-work or
                    // terminal status, so the marker must leave the mission where completion can act.
                    await missionService.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                    Mission? completed = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, completed!.Status, "Completion runs and hands the work to landing");
                    Captain? released = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, released!.State, "Completion releases the captain");
                }
            });

            // ----------------------------------------------------------------
            // A stage whose final result is [ARMADA:RESULT] BLOCKED is held for
            // the owner: it never completes, hands off, lands, or gets a rescue
            // ----------------------------------------------------------------

            await RunTest("An Implementation Worker that ends BLOCKED fails with its question, cancels the next stage, fails the voyage, and gets no rescue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    BlockedStageRig rig = await BlockedStageRig.CreateAsync(this, testDb, "Worker", MissionModeEnum.Implementation, withJudgeStage: true).ConfigureAwait(false);

                    await rig.RunCaptainAsync(
                        "Read the brief and the storage layer.",
                        "The brief asks for a retention window but names two different values (30 and 90 days).",
                        "[ARMADA:RESULT] BLOCKED",
                        "Which retention window is authoritative, 30 days or 90 days?").ConfigureAwait(false);

                    Mission worker = (await testDb.Driver.Missions.ReadAsync(rig.Mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Failed, worker.Status, "A blocked stage does not complete");
                    AssertTrue(CaptainBlockedResult.IsBlockedFailure(worker.FailureReason), "The failure names the blocked class: " + worker.FailureReason);
                    AssertContains("Which retention window is authoritative, 30 days or 90 days?", worker.FailureReason ?? String.Empty, "The question is preserved on the mission");

                    Mission judge = (await testDb.Driver.Missions.ReadAsync(rig.JudgeStage!.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Cancelled, judge.Status, "The next stage is not handed off");
                    AssertFalse((judge.Description ?? String.Empty).Contains("retention window", StringComparison.Ordinal), "The blocked stage's output never reaches the next brief");

                    Voyage voyage = (await testDb.Driver.Voyages.ReadAsync(rig.Voyage.Id).ConfigureAwait(false))!;
                    AssertEqual(VoyageStatusEnum.Failed, voyage.Status, "The voyage does not complete as a success");

                    await rig.AssertOwnerSeesQuestionAndNoRescueAsync("Which retention window is authoritative, 30 days or 90 days?").ConfigureAwait(false);
                }
            });

            await RunTest("A Research Worker that ends BLOCKED is not a completed report: it fails with its question and gets no rescue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    BlockedStageRig rig = await BlockedStageRig.CreateAsync(this, testDb, "Worker", MissionModeEnum.Research, withJudgeStage: false).ConfigureAwait(false);

                    await rig.RunCaptainAsync(
                        "## Findings",
                        "The mission required calling one read-only status tool, and no such tool is callable in this session.",
                        "Should the report proceed without the tool call, or is the missing tool delivery the finding?",
                        "",
                        "[ARMADA:RESULT] BLOCKED").ConfigureAwait(false);

                    Mission research = (await testDb.Driver.Missions.ReadAsync(rig.Mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Failed, research.Status, "A blocked report is not a completed report");
                    AssertTrue(CaptainBlockedResult.IsBlockedFailure(research.FailureReason), "The failure names the blocked class: " + research.FailureReason);
                    AssertContains("Should the report proceed without the tool call", research.FailureReason ?? String.Empty,
                        "A question written above the marker is preserved");

                    Voyage voyage = (await testDb.Driver.Voyages.ReadAsync(rig.Voyage.Id).ConfigureAwait(false))!;
                    AssertEqual(VoyageStatusEnum.Failed, voyage.Status, "The voyage does not complete as a success");

                    await rig.AssertOwnerSeesQuestionAndNoRescueAsync("Should the report proceed without the tool call").ConfigureAwait(false);
                }
            });

            await RunTest("A Judge that ends BLOCKED is not re-run for a missing verdict: it fails with its question and gets no rescue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    BlockedStageRig rig = await BlockedStageRig.CreateAsync(this, testDb, "Judge", MissionModeEnum.Implementation, withJudgeStage: false).ConfigureAwait(false);

                    await rig.RunCaptainAsync(
                        "Reviewed the diff against the brief.",
                        "The change drops a column the brief says to keep. Was dropping the legacy column approved by the owner?",
                        "[ARMADA:RESULT] BLOCKED").ConfigureAwait(false);

                    Mission judge = (await testDb.Driver.Missions.ReadAsync(rig.Mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Failed, judge.Status, "A blocked Judge is not requeued as a missing verdict");
                    AssertTrue(String.IsNullOrEmpty(judge.RetrySkipCaptainIds), "No in-place re-run was scheduled");
                    AssertTrue(CaptainBlockedResult.IsBlockedFailure(judge.FailureReason), "The failure names the blocked class: " + judge.FailureReason);
                    AssertContains("Was dropping the legacy column approved by the owner?", judge.FailureReason ?? String.Empty, "The question is preserved on the mission");

                    await rig.AssertOwnerSeesQuestionAndNoRescueAsync("Was dropping the legacy column approved by the owner?").ConfigureAwait(false);
                }
            });

            await RunTest("BLOCKED in prose, or followed by a later COMPLETE, is not a blocked stage: the Worker completes and hands off", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    BlockedStageRig rig = await BlockedStageRig.CreateAsync(this, testDb, "Worker", MissionModeEnum.Implementation, withJudgeStage: true).ConfigureAwait(false);

                    await rig.RunCaptainAsync(
                        "The brief says to end with [ARMADA:RESULT] BLOCKED if the owner must decide; nothing here needs that.",
                        "[ARMADA:RESULT] BLOCKED",
                        "Resolved: the brief's second value is a typo, confirmed in the design doc.",
                        "[ARMADA:RESULT] COMPLETE").ConfigureAwait(false);

                    Mission worker = (await testDb.Driver.Missions.ReadAsync(rig.Mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.WorkProduced, worker.Status, "The final result is COMPLETE, so the stage completes");
                    AssertFalse(CaptainBlockedResult.IsBlockedFailure(worker.FailureReason), "No blocked failure is recorded");

                    Mission judge = (await testDb.Driver.Missions.ReadAsync(rig.JudgeStage!.Id).ConfigureAwait(false))!;
                    AssertTrue(judge.Status != MissionStatusEnum.Cancelled, "The next stage is handed off, not cancelled");
                    AssertEqual(0, rig.OwnerNotes.Posts.Count, "No owner question is raised");
                    AssertEqual(0, (await rig.ActiveIncidentsAsync().ConfigureAwait(false)).Count, "No incident is opened");
                }
            });

            // ----------------------------------------------------------------
            // A report-only Worker that ends COMPLETE while its report states
            // its acceptance criteria are not met fails before any reviewer
            // ----------------------------------------------------------------

            await RunTest("A Research Worker that ends COMPLETE but states its acceptance criteria are not met fails and its reviewers are cancelled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    BlockedStageRig rig = await BlockedStageRig.CreateAsync(this, testDb, "Worker", MissionModeEnum.Research, withJudgeStage: true).ConfigureAwait(false);

                    await rig.RunCaptainAsync(
                        "## Research report",
                        "Every history lookup returned an error, so I stopped before the source study.",
                        "No matrix was completed. The acceptance criteria are therefore **not met**.",
                        "",
                        "[ARMADA:RESULT] COMPLETE",
                        "History access could not be established; the source study was not started.").ConfigureAwait(false);

                    Mission worker = (await testDb.Driver.Missions.ReadAsync(rig.Mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Failed, worker.Status, "An unfinished report by the captain's own account does not complete");
                    AssertTrue(ReportIncompleteResult.IsIncompleteReportFailure(worker.FailureReason), "The failure names the incomplete-report class: " + worker.FailureReason);
                    AssertContains("The acceptance criteria are therefore not met.", worker.FailureReason ?? String.Empty, "The captain's statement is quoted");

                    Mission judge = (await testDb.Driver.Missions.ReadAsync(rig.JudgeStage!.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Cancelled, judge.Status, "The reviewer stage is cancelled, not run");

                    Voyage voyage = (await testDb.Driver.Voyages.ReadAsync(rig.Voyage.Id).ConfigureAwait(false))!;
                    AssertEqual(VoyageStatusEnum.Failed, voyage.Status, "The voyage does not complete as a success");
                }
            });

            await RunTest("A completed Research report waits for its actual downstream Judge without waiving unfinished work", async () =>
            {
                foreach (string scenario in new[] { "pending-review", "unfinished-work", "unrelated-review", "cancelled-review", "foreign-review" })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        BlockedStageRig rig = await BlockedStageRig.CreateAsync(this, testDb, "Worker", MissionModeEnum.Research, withJudgeStage: true).ConfigureAwait(false);
                        if (scenario == "unrelated-review")
                        {
                            rig.JudgeStage!.DependsOnMissionId = null;
                            await testDb.Driver.Missions.UpdateAsync(rig.JudgeStage).ConfigureAwait(false);
                        }
                        if (scenario == "foreign-review")
                        {
                            UserMaster reviewer = new UserMaster(rig.JudgeStage!.TenantId!, "other-review-owner@armada.test", "password");
                            await testDb.Driver.Users.CreateAsync(reviewer).ConfigureAwait(false);
                            rig.JudgeStage.UserId = reviewer.Id;
                            await testDb.Driver.Missions.UpdateAsync(rig.JudgeStage).ConfigureAwait(false);
                        }
                        if (scenario == "cancelled-review")
                        {
                            rig.JudgeStage!.Status = MissionStatusEnum.Cancelled;
                            await testDb.Driver.Missions.UpdateAsync(rig.JudgeStage).ConfigureAwait(false);
                        }
                        await rig.RunCaptainAsync(
                            "## Research report",
                            scenario == "unfinished-work" ? "Acceptance criterion 2 is NOT MET because the required matrix is incomplete." : "The required matrix and source study are complete.",
                            "Acceptance criteria 1 and 2 are MET; criterion 3 is NOT MET because the Judge review is still pending.",
                            "[ARMADA:RESULT] COMPLETE").ConfigureAwait(false);

                        Mission worker = (await testDb.Driver.Missions.ReadAsync(rig.Mission.Id).ConfigureAwait(false))!;
                        Mission judge = (await testDb.Driver.Missions.ReadAsync(rig.JudgeStage!.Id).ConfigureAwait(false))!;
                        Voyage voyage = (await testDb.Driver.Voyages.ReadAsync(rig.Voyage.Id).ConfigureAwait(false))!;
                        if (scenario == "pending-review")
                        {
                            AssertTrue(worker.Status is MissionStatusEnum.WorkProduced or MissionStatusEnum.Complete, "completed current-stage work reaches review: " + worker.FailureReason);
                            AssertTrue(judge.Status is MissionStatusEnum.Pending or MissionStatusEnum.Assigned, "the actual downstream Judge remains eligible to decide its obligation");
                            AssertTrue(voyage.Status != VoyageStatusEnum.Failed, "pending review cannot fail its own voyage");
                        }
                        else
                        {
                            AssertEqual(MissionStatusEnum.Failed, worker.Status, "unmet work or an unavailable reviewer still blocks: " + scenario);
                            AssertTrue(ReportIncompleteResult.IsIncompleteReportFailure(worker.FailureReason), "the incomplete report keeps its named reason");
                            if (scenario == "foreign-review") AssertFalse(judge.Status is MissionStatusEnum.Assigned or MissionStatusEnum.InProgress, "a foreign reviewer cannot start for this blocked work");
                            else AssertEqual(scenario == "unrelated-review" ? MissionStatusEnum.Pending : MissionStatusEnum.Cancelled, judge.Status, "only dependent reviewers are cancelled when work is blocked");
                        }
                    }
                }
            });

            await RunTest("A complete precursor reaches its configured future role reviews without accepting unfinished work", async () =>
            {
                foreach (string scenario in new[] { "pending-reviews", "unfinished-current", "unfinished-earlier", "wrong-count", "cancelled-middle", "foreign-middle" })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        BlockedStageRig rig = await BlockedStageRig.CreateAsync(this, testDb, "Worker", MissionModeEnum.Research, withJudgeStage: true).ConfigureAwait(false);
                        string parentId = rig.Mission.Id;
                        List<Mission> futureStages = new List<Mission>();
                        foreach (string persona in new[] { "PortingReferenceAnalyst", "TestEngineer", "Linter", "Worker" })
                        {
                            Mission stage = new Mission("Review report", "Independently review the report.")
                            {
                                TenantId = rig.Mission.TenantId,
                                UserId = rig.Mission.UserId,
                                VesselId = rig.Vessel.Id,
                                VoyageId = rig.Voyage.Id,
                                Mode = MissionModeEnum.Research,
                                Persona = persona,
                                Status = MissionStatusEnum.Pending,
                                DependsOnMissionId = parentId
                            };
                            stage = await testDb.Driver.Missions.CreateAsync(stage).ConfigureAwait(false);
                            futureStages.Add(stage);
                            parentId = stage.Id;
                        }
                        rig.JudgeStage!.DependsOnMissionId = parentId;
                        await testDb.Driver.Missions.UpdateAsync(rig.JudgeStage).ConfigureAwait(false);
                        if (scenario == "cancelled-middle")
                        {
                            futureStages[1].Status = MissionStatusEnum.Cancelled;
                            await testDb.Driver.Missions.UpdateAsync(futureStages[1]).ConfigureAwait(false);
                        }
                        if (scenario == "foreign-middle")
                        {
                            UserMaster reviewer = new UserMaster(rig.Mission.TenantId!, "other-report-owner@armada.test", "password");
                            await testDb.Driver.Users.CreateAsync(reviewer).ConfigureAwait(false);
                            futureStages[1].UserId = reviewer.Id;
                            await testDb.Driver.Missions.UpdateAsync(futureStages[1]).ConfigureAwait(false);
                        }
                        await rig.RunCaptainAsync(
                            "## Research report",
                            scenario == "unfinished-current" ? "Acceptance criterion 2 is NOT MET because the source study is unfinished." : "The current source study and matrix are complete.",
                            scenario == "unfinished-earlier" ? "Acceptance criterion 2 is NOT MET because the earlier analyst omitted required source pages." : "Current-stage criteria 1 and 2 are MET.",
                            "Acceptance criterion 3 is honestly NOT MET at this Worker precursor stage because the " + (scenario == "wrong-count" ? "three" : "four") + " later role reviews do not yet exist.",
                            "[ARMADA:RESULT] COMPLETE").ConfigureAwait(false);
                        Mission worker = (await testDb.Driver.Missions.ReadAsync(rig.Mission.Id).ConfigureAwait(false))!;
                        Voyage voyage = (await testDb.Driver.Voyages.ReadAsync(rig.Voyage.Id).ConfigureAwait(false))!;
                        if (scenario == "pending-reviews")
                        {
                            AssertTrue(worker.Status is MissionStatusEnum.WorkProduced or MissionStatusEnum.Complete, "Complete current work reaches its configured review chain: " + worker.FailureReason);
                            foreach (Mission stage in futureStages)
                            {
                                Mission actual = (await testDb.Driver.Missions.ReadAsync(stage.Id).ConfigureAwait(false))!;
                                AssertTrue(actual.Status is MissionStatusEnum.Pending or MissionStatusEnum.Assigned, "Each independent future duty remains eligible");
                            }
                            Mission judge = (await testDb.Driver.Missions.ReadAsync(rig.JudgeStage.Id).ConfigureAwait(false))!;
                            AssertEqual(MissionStatusEnum.Pending, judge.Status, "Final acceptance still belongs to the actual Judge");
                            AssertTrue(voyage.Status != VoyageStatusEnum.Failed && voyage.Status != VoyageStatusEnum.Complete, "Future reviews neither fail nor accept the voyage prematurely");
                        }
                        else
                        {
                            AssertEqual(MissionStatusEnum.Failed, worker.Status, "Unfinished work or unverified future reviews remain blocking: " + scenario);
                            AssertTrue(ReportIncompleteResult.IsIncompleteReportFailure(worker.FailureReason), "The failure retains the incomplete-report reason");
                        }
                    }
                }
            });

            await RunTest("A report that meets its criteria, or an Implementation Worker that says criteria are not met, still completes and hands off", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    BlockedStageRig research = await BlockedStageRig.CreateAsync(this, testDb, "Worker", MissionModeEnum.Research, withJudgeStage: true).ConfigureAwait(false);
                    await research.RunCaptainAsync(
                        "## Research report",
                        "The parser does not meet the documented limit; that is the finding. The acceptance criteria are met.",
                        "[ARMADA:RESULT] COMPLETE").ConfigureAwait(false);

                    Mission report = (await testDb.Driver.Missions.ReadAsync(research.Mission.Id).ConfigureAwait(false))!;
                    AssertTrue(report.Status != MissionStatusEnum.Failed, "A report that meets its criteria is not failed: " + report.FailureReason);
                    Mission reportJudge = (await testDb.Driver.Missions.ReadAsync(research.JudgeStage!.Id).ConfigureAwait(false))!;
                    AssertTrue(reportJudge.Status != MissionStatusEnum.Cancelled, "Its reviewer is handed off, not cancelled");
                }

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    BlockedStageRig impl = await BlockedStageRig.CreateAsync(this, testDb, "Worker", MissionModeEnum.Implementation, withJudgeStage: true).ConfigureAwait(false);
                    await impl.RunCaptainAsync(
                        "Implemented the first half; the acceptance criteria are not met yet.",
                        "[ARMADA:RESULT] COMPLETE",
                        "Remaining work: the second half of the migration.").ConfigureAwait(false);

                    Mission worker = (await testDb.Driver.Missions.ReadAsync(impl.Mission.Id).ConfigureAwait(false))!;
                    AssertFalse(ReportIncompleteResult.IsIncompleteReportFailure(worker.FailureReason), "An Implementation Worker continues through its reviewers");
                    Mission judge = (await testDb.Driver.Missions.ReadAsync(impl.JudgeStage!.Id).ConfigureAwait(false))!;
                    AssertTrue(judge.Status != MissionStatusEnum.Cancelled, "Its reviewer is handed off, not cancelled");
                }
            });

            // ----------------------------------------------------------------
            // One output record can carry several markers; each one is routed
            // ----------------------------------------------------------------

            await RunTest("A message before the result in one record still records the terminal marker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("record-captain", AgentRuntimeEnum.Codex)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(
                        new Mission("Record mission") { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id }).ConfigureAwait(false);

                    int processId = 969696;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    handler.HandleAgentOutput(processId, "[ARMADA:MESSAGE] Wired the rows and ran the suite\n\n[ARMADA:RESULT] COMPLETE\nSummary follows.");

                    AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? first), "The result marker after a message is recorded");
                    AssertEqual("COMPLETE", first!.Value);
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 2).ConfigureAwait(false);
                }
            });

            await RunTest("A papercut and a verdict in one record are both routed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("papercut-verdict-captain", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(
                        new Mission("Papercut verdict mission") { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id, Persona = "Worker" }).ConfigureAwait(false);

                    int processId = 979797;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    handler.HandleAgentOutput(
                        processId,
                        "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"Record check\"}\n[ARMADA:VERDICT] PASS");

                    AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? first), "The verdict after a papercut is recorded");
                    AssertEqual("PASS", first!.Value);
                    List<ArmadaEvent> stored = await WaitForPapercutEventsAsync(testDb.Driver, 1).ConfigureAwait(false);
                    AssertEqual(1, stored.Count, "The papercut is stored");
                }
            });

            await RunTest("MissionProcessOwnership_RequiresRegisteredCaptainGeneration", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = new Captain("manual-proof-captain", AgentRuntimeEnum.ClaudeCode)
                    {
                        State = CaptainStateEnum.Working,
                        ProcessId = Environment.ProcessId
                    };
                    Mission mission = new Mission("manual-proof-process")
                    {
                        CaptainId = captain.Id,
                        Status = MissionStatusEnum.InProgress,
                        ProcessId = Environment.ProcessId,
                        StartedUtc = DateTime.UtcNow
                    };
                    captain.CurrentMissionId = mission.Id;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    AssertFalse(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "An unregistered process generation must fail closed");
                    RegisterTrackedProcess(handler, Environment.ProcessId, captain.Id, mission.Id);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A live process must be active only with matching captain and mission ownership");

                    captain.State = CaptainStateEnum.Quarantined;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A live owned process remains active while its captain is quarantined");

                    mission.ProcessId = null;
                    captain.ProcessId = null;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A registered live process remains active when persistence has not recorded its PID");

                    mission.ProcessId = Environment.ProcessId + 1;
                    captain.ProcessId = Environment.ProcessId + 1;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A registered live process remains active when persisted PIDs are stale");

                    int handledProcessId = Environment.ProcessId + 100000;
                    RegisterTrackedProcess(handler, handledProcessId, captain.Id, mission.Id);
                    MarkProcessExitHandled(handler, handledProcessId);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A handled stale mapping must not hide a second live mapping for the mission");
                }
            });

            await RunTest("MissionProcessOwnership_UnknownRuntimeLivenessFailsClosed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = new Captain("manual-unknown-runtime", AgentRuntimeEnum.Custom)
                    {
                        State = CaptainStateEnum.Working,
                        ProcessId = Environment.ProcessId
                    };
                    Mission mission = new Mission("manual-unknown-runtime-process")
                    {
                        CaptainId = captain.Id,
                        Status = MissionStatusEnum.InProgress,
                        ProcessId = Environment.ProcessId,
                        StartedUtc = DateTime.UtcNow
                    };
                    captain.CurrentMissionId = mission.Id;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    RegisterTrackedProcess(handler, Environment.ProcessId, captain.Id, mission.Id);

                    InvalidOperationException? captured = null;
                    try
                    {
                        await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        captured = ex;
                    }
                    AssertNotNull(captured, "unsupported runtime liveness must be observable");
                    AssertEqual("manual_completion_process_liveness_unknown", captured!.Message,
                        "unsupported runtime has a stable fail-closed reason");
                }
            });

            await RunTest("MissionProcessOwnership_ApiEndpointSyntheticProcessFollowsItsLoop", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = new Captain("manual-api-endpoint", AgentRuntimeEnum.ApiEndpoint)
                    {
                        State = CaptainStateEnum.Working
                    };
                    Mission mission = new Mission("manual-api-endpoint-process")
                    {
                        CaptainId = captain.Id,
                        Status = MissionStatusEnum.InProgress,
                        StartedUtc = DateTime.UtcNow
                    };
                    captain.CurrentMissionId = mission.Id;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int syntheticProcessId = Int32.MaxValue - 17;
                    RegisterTrackedProcess(handler, syntheticProcessId, captain.Id, mission.Id);
                    Armada.Core.ProcessSupervisor.RegisterSyntheticProcess(syntheticProcessId);
                    try
                    {
                        AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                            "A running in-process API loop must count as a live mission process");
                    }
                    finally
                    {
                        Armada.Core.ProcessSupervisor.UnregisterSyntheticProcess(syntheticProcessId);
                    }

                    AssertFalse(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "An API loop that has exited must not keep the mission process active");
                }
            });
        }

        private AgentLifecycleHandler CreateHandler(DatabaseDriver database, out ArmadaSettings settings, TimeSpan? modelValidationTimeout = null, IAdmiralService? admiralOverride = null, AgentRuntimeFactory? runtimeFactoryOverride = null, ISessionTokenService? sessionTokens = null, List<string>? emittedEventTypes = null, LoggingModule? loggingOverride = null)
        {
            LoggingModule logging = loggingOverride ?? CreateLogging();
            settings = CreateSettings();
            AgentRuntimeFactory runtimeFactory = runtimeFactoryOverride ?? new AgentRuntimeFactory(logging);
            IAdmiralService admiral = admiralOverride ?? new StubAdmiralService();
            IMessageTemplateService templateService = new MessageTemplateService(logging);

            return new AgentLifecycleHandler(
                logging,
                database,
                settings,
                runtimeFactory,
                admiral,
                templateService,
                null,
                null,
                (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) =>
                {
                    if (emittedEventTypes != null) lock (emittedEventTypes) emittedEventTypes.Add(eventType);
                    return Task.CompletedTask;
                },
                modelValidationTimeout,
                sessionTokens);
        }

        private static AuthenticationService CreateAuthenticationService(DatabaseDriver database, ISessionTokenService sessionTokens)
        {
            LoggingModule logging = CreateLogging();
            return new AuthenticationService(database, sessionTokens, new ArmadaSettings(), logging);
        }

        private sealed class AuthorizationRecordingHandler : HttpMessageHandler
        {
            public List<string> Authorizations { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Authorizations.Add(request.Headers.Authorization?.ToString() ?? String.Empty);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent(String.Empty) });
            }
        }

        private sealed class StopRecordingRuntimeFactory : AgentRuntimeFactory
        {
            private readonly Armada.Runtimes.Interfaces.IAgentRuntime _Runtime;

            public StopRecordingRuntimeFactory(LoggingModule logging, Armada.Runtimes.Interfaces.IAgentRuntime runtime)
                : base(logging)
            {
                _Runtime = runtime;
            }

            public override Armada.Runtimes.Interfaces.IAgentRuntime Create(AgentRuntimeEnum runtimeType) => _Runtime;
        }

        /// <summary>
        /// Runtime whose start returns a chosen process id and never raises events, so a test drives the
        /// process's output and exit itself.
        /// </summary>
        private sealed class SyntheticLaunchRuntime : Armada.Runtimes.Interfaces.IAgentRuntime
        {
            public int NextProcessId { get; set; }

            public string Name => "SyntheticLaunch";

            public bool SupportsResume => false;

            public bool SupportsPlanningSessions => false;

            public event Action<int, string>? OnOutputReceived { add { } remove { } }

            public event Action<int, string>? OnStdoutReceived { add { } remove { } }

            public event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived { add { } remove { } }

            public event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived { add { } remove { } }

            public event Action<int>? OnProcessStarted { add { } remove { } }

            public event Action<int, int?>? OnProcessExited { add { } remove { } }

            public Task<int> StartAsync(
                string workingDirectory,
                string prompt,
                Dictionary<string, string>? environment = null,
                string? logFilePath = null,
                string? finalMessageFilePath = null,
                string? model = null,
                Captain? captain = null,
                bool showThinking = false,
                CancellationToken token = default,
                CaptainLaunchIsolationPlan? isolationPlan = null)
                => Task.FromResult(NextProcessId);

            public Task<AgentStopResult> StopAsync(int processId, CancellationToken token = default) => Task.FromResult(AgentStopResult.Stopped());

            public Task<bool> IsRunningAsync(int processId, CancellationToken token = default) => Task.FromResult(true);
        }

        private sealed class StopRecordingRuntime : Armada.Runtimes.Interfaces.IAgentRuntime
        {
            public List<int> StopCalls { get; } = new List<int>();

            public AgentStopResult NextStopResult { get; set; } = AgentStopResult.Stopped();

            public string Name => "StopRecording";

            public bool SupportsResume => false;

            public bool SupportsPlanningSessions => false;

            public event Action<int, string>? OnOutputReceived { add { } remove { } }

            public event Action<int, string>? OnStdoutReceived { add { } remove { } }

            public event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived { add { } remove { } }

            public event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived { add { } remove { } }

            public event Action<int>? OnProcessStarted { add { } remove { } }

            public event Action<int, int?>? OnProcessExited { add { } remove { } }

            public Task<int> StartAsync(
                string workingDirectory,
                string prompt,
                Dictionary<string, string>? environment = null,
                string? logFilePath = null,
                string? finalMessageFilePath = null,
                string? model = null,
                Captain? captain = null,
                bool showThinking = false,
                CancellationToken token = default,
                CaptainLaunchIsolationPlan? isolationPlan = null)
                => throw new NotSupportedException("This runtime only records stops.");

            public Task<AgentStopResult> StopAsync(int processId, CancellationToken token = default)
            {
                lock (StopCalls) StopCalls.Add(processId);
                return Task.FromResult(NextStopResult);
            }

            public Task<bool> IsRunningAsync(int processId, CancellationToken token = default) => Task.FromResult(true);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_lifecycle_logs_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private static async Task<string> WaitForFileContainsAsync(string path, string expectedSubstring)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    string contents = await ReadSharedTextAsync(path).ConfigureAwait(false);
                    if (contents.Contains(expectedSubstring, StringComparison.Ordinal))
                    {
                        return contents;
                    }
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for expected content in file: " + path);
        }

        private static async Task<string> ReadSharedTextAsync(string path)
        {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new StreamReader(stream);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Wait for stored papercut events. The handler writes them on a background task, so a read
        /// taken immediately after the output line races the write.
        /// </summary>
        private static async Task<List<ArmadaEvent>> WaitForPapercutEventsAsync(DatabaseDriver database, int expected)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            List<ArmadaEvent> stored = new List<ArmadaEvent>();

            while (DateTime.UtcNow < deadline)
            {
                stored = await database.Events
                    .EnumerateByTypeAsync(PapercutParser.EventType, 50)
                    .ConfigureAwait(false);

                if (stored.Count >= expected) return stored;
                await Task.Delay(100).ConfigureAwait(false);
            }

            return stored;
        }

        /// <summary>
        /// Track a process as a launch would: it takes ownership of the mission's per-process state, then is
        /// mapped to its captain and mission.
        /// </summary>
        private static void RegisterTrackedProcess(AgentLifecycleHandler handler, int processId, string captainId, string missionId)
        {
            handler.RegisterMissionProcess(processId, captainId, missionId, handler.TakeMissionStateOwnership(missionId));
        }

        /// <summary>
        /// Store a Working captain running an in-progress mission on the given process.
        /// </summary>
        private static async Task<Mission> CreateWorkingMissionAsync(DatabaseDriver database, string captainName, int processId)
        {
            Captain captain = await database.Captains.CreateAsync(new Captain(captainName, AgentRuntimeEnum.ClaudeCode)
            {
                State = CaptainStateEnum.Working,
                ProcessId = processId
            }).ConfigureAwait(false);
            Mission mission = await database.Missions.CreateAsync(new Mission("Mission of " + captainName)
            {
                Status = MissionStatusEnum.InProgress,
                CaptainId = captain.Id,
                ProcessId = processId
            }).ConfigureAwait(false);
            captain.CurrentMissionId = mission.Id;
            await database.Captains.UpdateAsync(captain).ConfigureAwait(false);
            return mission;
        }

        private static AdmiralService CreateRealAdmiral(DatabaseDriver database, IntentionalProcessStops stops)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings();
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, database, settings, git);
            CaptainService captains = new CaptainService(logging, database, settings, git, docks);
            MissionService missions = new MissionService(logging, database, settings, docks, captains, git: git);
            return new AdmiralService(logging, database, settings, captains, missions, new VoyageService(logging, database), docks,
                git: git, intentionalStops: stops);
        }

        private static DateTime? MissionHeartbeatThrottle(AgentLifecycleHandler handler, string missionId)
        {
            FieldInfo field = typeof(AgentLifecycleHandler).GetField("_MissionHeartbeatWrites", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _MissionHeartbeatWrites field");
            ConcurrentDictionary<string, DateTime> writes = (ConcurrentDictionary<string, DateTime>)(field.GetValue(handler)
                ?? throw new InvalidOperationException("Mission heartbeat map was null"));
            return writes.TryGetValue(missionId, out DateTime stamp) ? stamp : null;
        }

        private static bool HasMissionHeartbeatThrottle(AgentLifecycleHandler handler, string missionId)
        {
            FieldInfo field = typeof(AgentLifecycleHandler).GetField("_MissionHeartbeatWrites", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _MissionHeartbeatWrites field");
            ConcurrentDictionary<string, DateTime> writes = (ConcurrentDictionary<string, DateTime>)(field.GetValue(handler)
                ?? throw new InvalidOperationException("Mission heartbeat map was null"));
            return writes.ContainsKey(missionId);
        }

        /// <summary>
        /// Wait until the handler has finished its asynchronous handling of a process exit, including the
        /// cleanup that runs after the admiral returns.
        /// </summary>
        private static async Task WaitForProcessExitCompletedAsync(AgentLifecycleHandler handler, int processId)
        {
            FieldInfo handledField = typeof(AgentLifecycleHandler).GetField("_HandledProcessExits", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find handled process map");
            FieldInfo inFlightField = typeof(AgentLifecycleHandler).GetField("_InFlightProcessExits", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find in-flight process map");
            ConcurrentDictionary<int, DateTime> handled = (ConcurrentDictionary<int, DateTime>)handledField.GetValue(handler)!;
            ConcurrentDictionary<int, byte> inFlight = (ConcurrentDictionary<int, byte>)inFlightField.GetValue(handler)!;

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (handled.ContainsKey(processId) && !inFlight.ContainsKey(processId)) return;
                await Task.Delay(20).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for the exit of process " + processId + " to be handled");
        }

        private static void MarkProcessExitHandled(AgentLifecycleHandler handler, int processId)
        {
            FieldInfo handledField = typeof(AgentLifecycleHandler).GetField("_HandledProcessExits", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find handled process map");
            ConcurrentDictionary<int, DateTime> handled = (ConcurrentDictionary<int, DateTime>)(handledField.GetValue(handler)
                ?? throw new InvalidOperationException("Handled process map was null"));
            handled[processId] = DateTime.UtcNow;
        }

        private static void RegisterPendingLaunch(AgentLifecycleHandler handler, string launchKey, string captainId, string missionId)
        {
            FieldInfo launchesField = typeof(AgentLifecycleHandler).GetField("_PendingLaunches", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _PendingLaunches field");

            object launches = launchesField.GetValue(handler)
                ?? throw new InvalidOperationException("Pending launches map was null");
            Type launchValueType = launches.GetType().GetGenericArguments()[1];
            object launchValue = Activator.CreateInstance(launchValueType, captainId, missionId)
                ?? throw new InvalidOperationException("Pending launch value could not be created");
            MethodInfo setItem = launches.GetType().GetProperty("Item")?.SetMethod
                ?? throw new InvalidOperationException("Could not find pending launch indexer");

            setItem.Invoke(launches, new object[] { launchKey, launchValue });
            long owner = handler.TakeMissionStateOwnership(missionId);
            FieldInfo ownersField = typeof(AgentLifecycleHandler).GetField("_PendingLaunchOwners", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((ConcurrentDictionary<string, long>)ownersField.GetValue(handler)!)[launchKey] = owner;
        }

        private static async Task InvokePersistStartedProcessIdAsync(AgentLifecycleHandler handler, int processId, string launchKey)
        {
            MethodInfo method = typeof(AgentLifecycleHandler).GetMethod("PersistStartedProcessIdAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find PersistStartedProcessIdAsync method");
            Task task = (Task)(method.Invoke(handler, new object[] { processId, launchKey, ((ConcurrentDictionary<string, long>)typeof(AgentLifecycleHandler).GetField("_PendingLaunchOwners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!)[launchKey] })
                ?? throw new InvalidOperationException("PersistStartedProcessIdAsync did not return a task"));

            await task.ConfigureAwait(false);
        }

        private static void SeedMissionOutput(AgentLifecycleHandler handler, string missionId, string output)
        {
            FieldInfo outputField = typeof(AgentLifecycleHandler).GetField("_MissionOutput", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _MissionOutput field");

            System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.StringBuilder> outputMap =
                (System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.StringBuilder>)(outputField.GetValue(handler)
                ?? throw new InvalidOperationException("Mission output map was null"));

            outputMap[missionId] = new System.Text.StringBuilder(output);
        }

        private static void RegisterFinalMessageArtifact(AgentLifecycleHandler handler, string missionId, string artifactPath)
        {
            FieldInfo artifactField = typeof(AgentLifecycleHandler).GetField("_MissionFinalMessageFiles", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _MissionFinalMessageFiles field");

            System.Collections.Concurrent.ConcurrentDictionary<string, string> artifactMap =
                (System.Collections.Concurrent.ConcurrentDictionary<string, string>)(artifactField.GetValue(handler)
                ?? throw new InvalidOperationException("Mission final message map was null"));

            artifactMap[missionId] = artifactPath;
        }

        private static void StartTrackedProcessHeartbeat(AgentLifecycleHandler handler, int processId, string captainId, string missionId)
        {
            MethodInfo method = typeof(AgentLifecycleHandler).GetMethod("StartProcessLivenessHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find StartProcessLivenessHeartbeat method");
            method.Invoke(handler, new object[] { processId, captainId, missionId });
        }

        private static void StopTrackedProcessHeartbeat(AgentLifecycleHandler handler, int processId)
        {
            MethodInfo method = typeof(AgentLifecycleHandler).GetMethod("StopProcessLivenessHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find StopProcessLivenessHeartbeat method");
            method.Invoke(handler, new object[] { processId });
        }

        private static bool HasProcessHeartbeatLoop(AgentLifecycleHandler handler, int processId)
        {
            FieldInfo loopsField = typeof(AgentLifecycleHandler).GetField("_ProcessHeartbeatLoops", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _ProcessHeartbeatLoops field");

            System.Collections.Concurrent.ConcurrentDictionary<int, System.Threading.CancellationTokenSource> loops =
                (System.Collections.Concurrent.ConcurrentDictionary<int, System.Threading.CancellationTokenSource>)(loopsField.GetValue(handler)
                ?? throw new InvalidOperationException("Process heartbeat loop map was null"));

            return loops.ContainsKey(processId);
        }

        private static Process StartSilentProcess()
        {
            ProcessStartInfo startInfo;
            if (OperatingSystem.IsWindows())
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping 127.0.0.1 -n 10 >nul",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c \"sleep 10\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start silent heartbeat test process");
        }

        private static async Task WaitForProgressSignalsAsync(DatabaseDriver database, string captainId, int expected)
        {
            await WaitForConditionAsync(async () =>
            {
                List<Signal> recent = await database.Signals.EnumerateRecentAsync(200).ConfigureAwait(false);
                return recent.Count(signal => signal.FromCaptainId == captainId) >= expected;
            }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        private static async Task WaitForConditionAsync(Func<Task<bool>> predicate, TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(3));

            while (DateTime.UtcNow < deadline)
            {
                if (await predicate().ConfigureAwait(false))
                    return;

                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for asynchronous condition");
        }

        /// <summary>
        /// One captain stage run through the real lifecycle: the agent lifecycle handler buffers the captain's
        /// output, the mission service completes the stage on exit, and the real recovery orchestrator receives
        /// the outcome. The owner-note poster records what reached the owner.
        /// </summary>
        private sealed class BlockedStageRig
        {
            private const string _TenantId = "ten_blocked_stage";
            private const string _UserId = "usr_blocked_stage";
            private static int _NextProcessId = 979000;

            private readonly AgentLifecycleHandlerTests _Suite;
            private readonly TestDatabase _Db;

            public Mission Mission { get; private set; } = null!;
            public Mission? JudgeStage { get; private set; }
            public Voyage Voyage { get; private set; } = null!;
            public Vessel Vessel { get; private set; } = null!;
            public Captain Captain { get; private set; } = null!;
            public AgentLifecycleHandler Handler { get; private set; } = null!;
            public MissionService MissionService { get; private set; } = null!;
            public FakeOwnerDecisionNotePoster OwnerNotes { get; } = new FakeOwnerDecisionNotePoster();

            private BlockedStageRig(AgentLifecycleHandlerTests suite, TestDatabase db)
            {
                _Suite = suite;
                _Db = db;
            }

            public static async Task<BlockedStageRig> CreateAsync(AgentLifecycleHandlerTests suite, TestDatabase testDb, string persona, MissionModeEnum mode, bool withJudgeStage)
            {
                BlockedStageRig rig = new BlockedStageRig(suite, testDb);
                DatabaseDriver db = testDb.Driver;

                await db.Tenants.CreateAsync(new TenantMetadata { Id = _TenantId, Name = _TenantId }).ConfigureAwait(false);
                await db.Users.CreateAsync(new UserMaster
                {
                    Id = _UserId,
                    TenantId = _TenantId,
                    Email = _UserId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);

                rig.Handler = suite.CreateHandler(db, out ArmadaSettings settings);
                LoggingModule logging = CreateLogging();
                StubGitService git = new StubGitService();
                IDockService dockService = new DockService(logging, db, settings, git);
                ICaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
                rig.MissionService = new MissionService(logging, db, settings, dockService, captainService,
                    resourcePressureAdmission: global::Test.Shared.Infrastructure.TestResourcePressure.Unconstrained(settings));
                rig.MissionService.HandoffOwnerNotePoster = rig.OwnerNotes;
                rig.MissionService.OnGetMissionOutput = rig.Handler.GetAndClearMissionOutput;

                AutonomousRecoveryOrchestrator recovery = new AutonomousRecoveryOrchestrator(
                    db, RecordingAdmiral.Create().Service, new IncidentService(db), new RunbookService(db, logging), settings, logging);
                rig.MissionService.OnMissionOutcome = (Mission outcome, bool willLand) => recovery.HandleMissionOutcomeAsync(outcome, willLand);

                rig.Vessel = await db.Vessels.CreateAsync(new Vessel("blocked-vessel", "https://github.com/test/blocked.git")
                {
                    TenantId = _TenantId,
                    UserId = _UserId
                }).ConfigureAwait(false);
                rig.Voyage = await db.Voyages.CreateAsync(new Voyage("blocked-voyage") { TenantId = _TenantId, UserId = _UserId }).ConfigureAwait(false);

                Captain captain = new Captain("blocked-captain", AgentRuntimeEnum.ClaudeCode);
                captain.TenantId = _TenantId;
                captain.UserId = _UserId;
                captain.State = CaptainStateEnum.Working;
                rig.Captain = await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                Dock dock = new Dock(rig.Vessel.Id);
                dock.TenantId = _TenantId;
                dock.UserId = _UserId;
                dock.CaptainId = rig.Captain.Id;
                dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                dock.BranchName = "armada/blocked-captain/stage";
                dock.Active = true;
                dock = await db.Docks.CreateAsync(dock).ConfigureAwait(false);

                Mission mission = new Mission("Blocked " + persona + " stage", "Do the stage's work.");
                mission.TenantId = _TenantId;
                mission.UserId = _UserId;
                mission.Persona = persona;
                mission.Mode = mode;
                mission.Status = MissionStatusEnum.InProgress;
                mission.CaptainId = rig.Captain.Id;
                mission.DockId = dock.Id;
                mission.VesselId = rig.Vessel.Id;
                mission.VoyageId = rig.Voyage.Id;
                mission.BranchName = dock.BranchName;
                rig.Mission = await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                if (withJudgeStage)
                {
                    Mission judge = new Mission("[Judge] Review", "Review the prior stage.");
                    judge.TenantId = _TenantId;
                    judge.UserId = _UserId;
                    judge.Persona = "Judge";
                    judge.Mode = mode;
                    judge.Status = MissionStatusEnum.Pending;
                    judge.VesselId = rig.Vessel.Id;
                    judge.VoyageId = rig.Voyage.Id;
                    judge.DependsOnMissionId = rig.Mission.Id;
                    rig.JudgeStage = await db.Missions.CreateAsync(judge).ConfigureAwait(false);
                }

                rig.Captain.CurrentMissionId = rig.Mission.Id;
                rig.Captain.CurrentDockId = dock.Id;
                await db.Captains.UpdateAsync(rig.Captain).ConfigureAwait(false);
                return rig;
            }

            /// <summary>
            /// The captain writes its final answer as one output record, then its process exits and the
            /// completion path runs, as it does for a real captain.
            /// </summary>
            public async Task RunCaptainAsync(params string[] lines)
            {
                int processId = Interlocked.Increment(ref _NextProcessId);
                RegisterTrackedProcess(Handler, processId, Captain.Id, Mission.Id);
                string record = String.Join("\n", lines);
                Handler.HandleAgentOutput(processId, record);
                await WaitForProgressSignalsAsync(_Db.Driver, Captain.Id, ProgressParser.ParseAll(record).Count).ConfigureAwait(false);

                await MissionService.HandleCompletionAsync(Captain, Mission.Id).ConfigureAwait(false);
            }

            public async Task<List<Incident>> ActiveIncidentsAsync()
            {
                AuthContext auth = AuthContext.Authenticated(_TenantId, _UserId, false, true, "UnitTest");
                return await new IncidentService(_Db.Driver).EnumerateActiveAsync(auth, new IncidentQuery { MissionId = Mission.Id }).ConfigureAwait(false);
            }

            /// <summary>
            /// The owner sees the question on one open incident and one owner-addressed note, recovery read the
            /// blocked stage and dispatched nothing.
            /// </summary>
            public async Task AssertOwnerSeesQuestionAndNoRescueAsync(string question)
            {
                List<Incident> incidents = await ActiveIncidentsAsync().ConfigureAwait(false);
                _Suite.AssertEqual(1, incidents.Count, "One open incident carries the blocked question");
                _Suite.AssertContains(question, incidents[0].RecoveryNotes ?? String.Empty, "The incident carries the captain's question");
                _Suite.AssertContains("Autonomous policy stopped before rescue dispatch: " + CaptainBlockedResult.FailureReasonPrefix,
                    incidents[0].RecoveryNotes ?? String.Empty, "Recovery read the blocked stage and held the rescue");

                _Suite.AssertEqual(1, OwnerNotes.Posts.Count, "One owner-addressed note is posted");
                _Suite.AssertContains(question, OwnerNotes.Posts[0], "The owner note carries the captain's question");
                _Suite.AssertEqual(Vessel.Id, OwnerNotes.VesselIds[0], "The owner note is tagged with the vessel");

                List<Mission> vesselMissions = await _Db.Driver.Missions.EnumerateByVesselAsync(Vessel.Id).ConfigureAwait(false);
                _Suite.AssertFalse(vesselMissions.Any(item => item.ParentMissionId == Mission.Id), "No rescue re-runs the blocked stage");
            }
        }

        private sealed class StubAdmiralService : IAdmiralService
        {
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
            {
                return Task.FromResult<Pipeline?>(null);
            }

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task RecallAllAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }
            public Task StopAllAgentProcessesAsync(CancellationToken token = default) => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Func<int, int?, string, string, Task>? OnHandleProcessExit { get; set; }

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
            {
                if (OnHandleProcessExit != null) return OnHandleProcessExit(processId, exitCode, captainId, missionId);
                return Task.CompletedTask;
            }
        }

        private sealed class CursorShimScope : IDisposable
        {
            public string ArgsFile { get; }

            private readonly string _tempDirectory;
            private readonly string _originalPath;
            // True when ARMADA_TEST_CURSOR_AGENT was set (Windows path); restored in Dispose.
            private readonly bool _setWindowsAgentOverride;
            private readonly string? _originalCursorAgentOverride;

            private CursorShimScope(
                string tempDirectory,
                string argsFile,
                string originalPath,
                bool setWindowsAgentOverride,
                string? originalCursorAgentOverride)
            {
                _tempDirectory = tempDirectory;
                ArgsFile = argsFile;
                _originalPath = originalPath;
                _setWindowsAgentOverride = setWindowsAgentOverride;
                _originalCursorAgentOverride = originalCursorAgentOverride;
            }

            public static CursorShimScope Create()
            {
                string tempDirectory = Path.Combine(Path.GetTempPath(), "armada_cursor_shim_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);

                string argsFile = Path.Combine(tempDirectory, "cursor-args.txt");
                string originalPath = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
                string? originalCursorAgentOverride = Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT");
                bool setWindowsAgentOverride = false;

                Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_ARGS_FILE", argsFile);

                if (OperatingSystem.IsWindows())
                {
                    // Write the test shim inside the temp directory and point
                    // ARMADA_TEST_CURSOR_AGENT at it. This avoids touching %APPDATA%\npm
                    // (a user-global path that leaks stale shims on abnormal test termination)
                    // and prevents the shim from racing with the official Cursor install path
                    // that CursorRuntime now prefers over npm.
                    string shimPath = Path.Combine(tempDirectory, "cursor-agent.cmd");
                    File.WriteAllText(shimPath, BuildWindowsShim());
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", shimPath);
                    setWindowsAgentOverride = true;
                }
                else
                {
                    string shimPath = Path.Combine(tempDirectory, "cursor-agent");
                    File.WriteAllText(shimPath, BuildUnixShim());
                    File.SetUnixFileMode(
                        shimPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    Environment.SetEnvironmentVariable("PATH", tempDirectory + Path.PathSeparator + originalPath);
                }

                return new CursorShimScope(
                    tempDirectory,
                    argsFile,
                    originalPath,
                    setWindowsAgentOverride,
                    originalCursorAgentOverride);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_ARGS_FILE", null);
                Environment.SetEnvironmentVariable("PATH", _originalPath);

                if (_setWindowsAgentOverride)
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", _originalCursorAgentOverride);
                }

                try { Directory.Delete(_tempDirectory, true); } catch { }
            }

            private static string BuildWindowsShim()
            {
                return "@echo off\r\n" +
                    "setlocal EnableExtensions EnableDelayedExpansion\r\n" +
                    "set \"ARGS_FILE=%ARMADA_TEST_CURSOR_ARGS_FILE%\"\r\n" +
                    "set \"ALL_ARGS=%*\"\r\n" +
                    ">> \"%ARGS_FILE%\" echo(!ALL_ARGS!\r\n" +
                    "set \"MODEL=\"\r\n" +
                    ":loop\r\n" +
                    "if \"%~1\"==\"\" goto done\r\n" +
                    ">> \"%ARGS_FILE%\" echo %~1\r\n" +
                    "if /I \"%~1\"==\"--model\" set \"MODEL=%~2\"\r\n" +
                    "shift\r\n" +
                    "goto loop\r\n" +
                    ":done\r\n" +
                    "if /I \"%MODEL%\"==\"bad-model\" (\r\n" +
                    "  >&2 echo unknown model '%MODEL%'\r\n" +
                    "  exit /b 3\r\n" +
                    ")\r\n" +
                    "if /I \"%MODEL%\"==\"hang-model\" (\r\n" +
                    "  ping 127.0.0.1 -n 5 >nul\r\n" +
                    "  exit /b 0\r\n" +
                    ")\r\n" +
                    "if /I \"%MODEL%\"==\"slow-launch-model\" (\r\n" +
                    "  ping 127.0.0.1 -n 3 >nul\r\n" +
                    "  exit /b 0\r\n" +
                    ")\r\n" +
                    "echo ok\r\n" +
                    "exit /b 0\r\n";
            }

            private static string BuildUnixShim()
            {
                return "#!/usr/bin/env sh\n" +
                    "args_file=\"$ARMADA_TEST_CURSOR_ARGS_FILE\"\n" +
                    "printf '%s\\n' \"$*\" >> \"$args_file\"\n" +
                    "prev=\"\"\n" +
                    "model=\"\"\n" +
                    "for arg in \"$@\"; do\n" +
                    "  printf '%s\\n' \"$arg\" >> \"$args_file\"\n" +
                    "  if [ \"$prev\" = \"--model\" ]; then\n" +
                    "    model=\"$arg\"\n" +
                    "  fi\n" +
                    "  prev=\"$arg\"\n" +
                    "done\n" +
                    "if [ \"$model\" = \"bad-model\" ]; then\n" +
                    "  printf '%s\\n' \"unknown model '$model'\" >&2\n" +
                    "  exit 3\n" +
                    "fi\n" +
                    "if [ \"$model\" = \"hang-model\" ]; then\n" +
                    // Only has to outlast the 1 s ceiling the timeout test drives, with margin for a
                    // loaded host; every second beyond that is time the test waits on a result it has.
                    "  sleep 3\n" +
                    "  exit 0\n" +
                    "fi\n" +
                    "if [ \"$model\" = \"slow-launch-model\" ]; then\n" +
                    "  sleep 2\n" +
                    "  exit 0\n" +
                    "fi\n" +
                    "printf '%s\\n' ok\n" +
                    "exit 0\n";
            }
        }
    }
}
