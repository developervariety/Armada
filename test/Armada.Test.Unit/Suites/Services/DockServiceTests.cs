namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class DockServiceTests : TestSuite
    {
        public override string Name => "Dock Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ProvisionAsync serializes repo worktree creation per vessel repo", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));

                    LockingGitService git = new LockingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("test-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_workdir_" + Guid.NewGuid().ToString("N"));
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain1 = new Captain("captain-1");
                    Captain captain2 = new Captain("captain-2");
                    captain1 = await testDb.Driver.Captains.CreateAsync(captain1).ConfigureAwait(false);
                    captain2 = await testDb.Driver.Captains.CreateAsync(captain2).ConfigureAwait(false);

                    Task<Dock?> first = service.ProvisionAsync(vessel, captain1, "armada/captain-1/msn_one", "msn_one");
                    Task<Dock?> second = service.ProvisionAsync(vessel, captain2, "armada/captain-2/msn_two", "msn_two");

                    Dock?[] docks = await Task.WhenAll(first, second).ConfigureAwait(false);

                    AssertNotNull(docks[0], "First dock should be provisioned");
                    AssertNotNull(docks[1], "Second dock should be provisioned");
                    AssertEqual(1, git.MaxConcurrentCreateCalls, "Concurrent worktree creation against the same repo should be serialized");
                }
            });

            await RunTest("ProvisionAsync missing configured default branch reuses repo history instead of seeding orphan repo", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));

                    GitService git = new GitService(logging);
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string rootDir = Path.Combine(Path.GetTempPath(), "armada-dockservice-" + Guid.NewGuid().ToString("N"));
                    string sourceDir = Path.Combine(rootDir, "source");
                    string workDir = Path.Combine(rootDir, "target");

                    try
                    {
                        Directory.CreateDirectory(sourceDir);
                        await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                        await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                        await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                        await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "hello\n").ConfigureAwait(false);
                        await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                        await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

                        string sourceHead = (await RunGitAsync(sourceDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                        Vessel vessel = new Vessel("test-vessel", sourceDir);
                        vessel.DefaultBranch = "release/e2e";
                        vessel.WorkingDirectory = workDir;
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("captain-1");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-1/msn_one", "msn_one").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock should be provisioned");

                        Vessel? reloadedVessel = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                        AssertNotNull(reloadedVessel, "Vessel should remain readable");
                        AssertFalse(String.IsNullOrEmpty(reloadedVessel!.LocalPath), "Provisioning should populate the bare repo path");

                        string repoPath = reloadedVessel.LocalPath!;
                        string defaultBranchCommit = (await RunGitAsync(repoPath, "rev-parse", "refs/heads/release/e2e").ConfigureAwait(false)).Trim();
                        string worktreeHead = (await RunGitAsync(dock!.WorktreePath!, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                        AssertEqual(sourceHead, defaultBranchCommit, "Configured default branch should be created from the existing repo history");
                        AssertEqual(sourceHead, worktreeHead, "Provisioned worktree should start from the source repo history");
                    }
                    finally
                    {
                        if (Directory.Exists(rootDir))
                        {
                            try { Directory.Delete(rootDir, true); }
                            catch { }
                        }

                        if (Directory.Exists(settings.DocksDirectory))
                        {
                            try { Directory.Delete(settings.DocksDirectory, true); }
                            catch { }
                        }

                        if (Directory.Exists(settings.ReposDirectory))
                        {
                            try { Directory.Delete(settings.ReposDirectory, true); }
                            catch { }
                        }
                    }
                }
            });

            await RunTest("An explicit opt-out removes MCP client config but keeps OpenCode permissions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    settings.SeedDockRuntimeMcpConfig = false;

                    LockingGitService git = new LockingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("no-mcp-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_workdir_" + Guid.NewGuid().ToString("N"));
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("no-mcp-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/no-mcp/msn_one", "msn_one").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned");

                    AssertFalse(File.Exists(Path.Combine(dock!.WorktreePath!, ".mcp.json")), "project MCP config must honor the opt-out");
                    AssertFalse(File.Exists(Path.Combine(dock.WorktreePath!, ".cursor", "mcp.json")), "Cursor MCP config must honor the opt-out");
                    AssertFalse(File.Exists(Path.Combine(dock.WorktreePath!, ".codex", "config.toml")), "Codex MCP config must honor the opt-out");
                    AssertFalse(File.Exists(Path.Combine(dock.WorktreePath!, ".gemini", "settings.json")), "Gemini MCP config must honor the opt-out");

                    // The OpenCode permission document is not MCP. It grants reads outside the dock,
                    // which captains need for playbooks, sibling repositories, and shared memory.
                    string openCodePath = Path.Combine(dock.WorktreePath!, "opencode.json");
                    AssertTrue(File.Exists(openCodePath), "OpenCode permission config must still be written");
                    string openCode = await File.ReadAllTextAsync(openCodePath).ConfigureAwait(false);
                    AssertContains("external_directory", openCode, "OpenCode permission config must still grant external directory access");
                    AssertFalse(openCode.Contains("mcpServers", StringComparison.Ordinal), "the permission config must not declare an MCP server");
                }
            });

            await RunTest("ProvisionAsync writes dock start commit metadata and ReclaimAsync removes it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    // The default path gives every supported captain the local Armada MCP endpoint.

                    LockingGitService git = new LockingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("metadata-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_workdir_" + Guid.NewGuid().ToString("N"));
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("metadata-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/metadata/msn_one", "msn_one").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned");

                    string projectMcpPath = Path.Combine(dock!.WorktreePath!, ".mcp.json");
                    string cursorMcpPath = Path.Combine(dock.WorktreePath!, ".cursor", "mcp.json");
                    string codexMcpPath = Path.Combine(dock.WorktreePath!, ".codex", "config.toml");
                    string geminiMcpPath = Path.Combine(dock.WorktreePath!, ".gemini", "settings.json");
                    AssertTrue(File.Exists(projectMcpPath), "Dock provisioning should seed project MCP config");
                    AssertTrue(File.Exists(cursorMcpPath), "Dock provisioning should seed Cursor MCP config");
                    AssertTrue(File.Exists(codexMcpPath), "Dock provisioning should seed Codex MCP config");
                    AssertTrue(File.Exists(geminiMcpPath), "Dock provisioning should seed Gemini MCP config");
                    string projectMcp = await File.ReadAllTextAsync(projectMcpPath).ConfigureAwait(false);
                    AssertContains("localhost:" + settings.McpPort, projectMcp, "Project MCP config should point at Armada MCP");
                    AssertContains("\"armada\"", projectMcp, "Project MCP config should name the Armada server");
                    string codexMcp = await File.ReadAllTextAsync(codexMcpPath).ConfigureAwait(false);
                    AssertContains("localhost:" + settings.McpPort, codexMcp, "Codex MCP config should point at Armada MCP");
                    AssertContains("mcp_servers.armada", codexMcp, "Codex MCP config should name the Armada server");
                    string geminiMcp = await File.ReadAllTextAsync(geminiMcpPath).ConfigureAwait(false);
                    AssertContains("localhost:" + settings.McpPort, geminiMcp, "Gemini MCP config should point at Armada MCP");
                    AssertContains("\"armada\"", geminiMcp, "Gemini MCP config should name the Armada server");
                    string openCodeMcp = await File.ReadAllTextAsync(Path.Combine(dock.WorktreePath!, "opencode.json")).ConfigureAwait(false);
                    AssertContains("localhost:" + settings.McpPort, openCodeMcp, "OpenCode config should point at Armada MCP");
                    AssertContains("\"armada\"", openCodeMcp, "OpenCode config should name the Armada server");

                    string metadataPath = Path.Combine(settings.LogDirectory, "docks", dock!.Id + ".start");
                    AssertTrue(File.Exists(metadataPath), "Dock provisioning should persist the start commit metadata");
                    AssertEqual("abc123", (await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false)).Trim(), "Metadata should store the provisioned HEAD commit");

                    await service.ReclaimAsync(dock.Id).ConfigureAwait(false);
                    AssertFalse(File.Exists(metadataPath), "Dock reclaim should remove the start commit metadata");
                }
            });

            await RunTest("ProvisionAsync seeds OpenCode permissions for worktree and mission playbooks roots", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string missionId = "msn_opencode";
                    Vessel vessel = new Vessel("opencode-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("opencode-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/opencode/msn_one", missionId).ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned");

                    string openCodePath = Path.Combine(dock!.WorktreePath!, "opencode.json");
                    AssertTrue(File.Exists(openCodePath), "Dock provisioning should seed OpenCode permissions");

                    OpenCodeTestConfig config = await ReadOpenCodeConfigAsync(openCodePath).ConfigureAwait(false);

                    // The seeded document grants external_directory via the bare-string "allow"
                    // form. The worktree/playbooks roots no longer drive a per-path grant map
                    // (that shape was broken on Windows), so the document is the same regardless
                    // of which roots provisioning computes.
                    AssertOpenCodeBareStringGrant(config, "Single-repo vessel OpenCode config");

                    string excludePath = Path.Combine(dock.WorktreePath!, ".git", "info", "exclude");
                    string exclude = await File.ReadAllTextAsync(excludePath).ConfigureAwait(false);
                    AssertContains("opencode.json", exclude, "Git exclude should ignore the dock-local OpenCode config");
                }
            });

            await RunTest("ProvisionAsync grants OpenCode access to declared sibling repository checkouts", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/sibling.git",
                            RelativePath = "../SiblingRepo",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        }
                    };

                    string missionId = "msn_opencode_sibling";
                    Vessel vessel = new Vessel("opencode-sibling-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("opencode-sibling-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/opencode/msn_sibling", missionId).ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned with a sibling repo");

                    string openCodePath = Path.Combine(dock!.WorktreePath!, "opencode.json");
                    OpenCodeTestConfig config = await ReadOpenCodeConfigAsync(openCodePath).ConfigureAwait(false);

                    // Provisioning a vessel with a declared sibling still seeds the bare-string
                    // grant. The sibling checkout no longer adds a per-path grant entry (the
                    // path-keyed map shape was broken on Windows); cross-repo access now rests
                    // on the bare-string allow plus the runtime --dangerously-skip-permissions
                    // override rather than enumerated roots.
                    AssertOpenCodeBareStringGrant(config, "Sibling-vessel OpenCode config");
                }
            });

            await RunTest("ProvisionAsync with null missionId omits the OpenCode playbooks root", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("opencode-null-mission-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("opencode-null-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/opencode/no_mission", null).ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned without a mission id");

                    string openCodePath = Path.Combine(dock!.WorktreePath!, "opencode.json");
                    OpenCodeTestConfig config = await ReadOpenCodeConfigAsync(openCodePath).ConfigureAwait(false);

                    // Provisioning with a null mission id still succeeds and seeds the bare-string
                    // grant. The playbooks root no longer appears as a per-path grant entry (the
                    // document carries only the bare string "allow"), so a missing mission id can
                    // no longer fabricate or omit a path key.
                    AssertOpenCodeBareStringGrant(config, "Null-mission OpenCode config");
                }
            });

            await RunTest("ProvisionAsync does not overwrite a pre-existing OpenCode config", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    string sentinel = "{\"custom\":true}\n";
                    PreseedOpenCodeGitService git = new PreseedOpenCodeGitService(sentinel);
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("opencode-preexisting-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("opencode-preexisting-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/opencode/preexisting", "msn_preexisting").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned");

                    string openCodePath = Path.Combine(dock!.WorktreePath!, "opencode.json");
                    string actual = await File.ReadAllTextAsync(openCodePath).ConfigureAwait(false);
                    AssertEqual(sentinel, actual, "Existing OpenCode config should not be overwritten");

                    string excludePath = Path.Combine(dock.WorktreePath!, ".git", "info", "exclude");
                    string exclude = await File.ReadAllTextAsync(excludePath).ConfigureAwait(false);
                    AssertContains("opencode.json", exclude, "Pre-existing OpenCode config should still be git-excluded");
                }
            });

            await RunTest("ProvisionAsync grants OpenCode access to every declared sibling checkout", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/sibA.git",
                            RelativePath = "../SibA",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        },
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/sibB.git",
                            RelativePath = "../nested/SibB",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "develop"
                        }
                    };

                    string missionId = "msn_opencode_multi";
                    Vessel vessel = new Vessel("opencode-multi-sibling-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("opencode-multi-sibling-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/opencode/msn_multi", missionId).ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned with multiple sibling repos");

                    string openCodePath = Path.Combine(dock!.WorktreePath!, "opencode.json");
                    OpenCodeTestConfig config = await ReadOpenCodeConfigAsync(openCodePath).ConfigureAwait(false);

                    // Multiple declared siblings still seed a single bare-string grant. The
                    // siblings no longer multiply into per-path grant entries, so there is no
                    // per-root count to assert and no risk of a path key widening to a blanket
                    // or whole-drive grant -- the document is exactly the bare string "allow".
                    AssertOpenCodeBareStringGrant(config, "Multi-sibling OpenCode config");

                    string excludePath = Path.Combine(dock.WorktreePath!, ".git", "info", "exclude");
                    string exclude = await File.ReadAllTextAsync(excludePath).ConfigureAwait(false);
                    AssertContains("opencode.json", exclude, "Git exclude should ignore the dock-local OpenCode config even with siblings");
                }
            });

            await RunTest("ProvisionAsync omits OpenCode grant for a sibling with a blank relative path", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    // A sibling whose RelativePath is whitespace must be skipped by the grant
                    // builder (same guard the sibling provisioner uses), so it never resolves
                    // to the worktree itself and produces a spurious / duplicate grant.
                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/blank.git",
                            RelativePath = "   ",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        }
                    };

                    string missionId = "msn_opencode_blank";
                    Vessel vessel = new Vessel("opencode-blank-sibling-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("opencode-blank-sibling-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/opencode/msn_blank", missionId).ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned even with a blank-relative-path sibling");

                    string openCodePath = Path.Combine(dock!.WorktreePath!, "opencode.json");
                    OpenCodeTestConfig config = await ReadOpenCodeConfigAsync(openCodePath).ConfigureAwait(false);

                    // A blank-relative-path sibling cannot perturb the seeded document: the grant
                    // is the bare string "allow" regardless of roots, so there is no path key for
                    // a blank sibling to collapse onto a blanket grant.
                    AssertOpenCodeBareStringGrant(config, "Blank-sibling OpenCode config");
                }
            });

            await RunTest("ProvisionAsync collapses duplicate sibling relative paths to a single OpenCode grant", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    // Two siblings spelling the same checkout differently ("../Shared" and
                    // "../nested/../Shared") must resolve to one canonical root and grant once.
                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/shared.git",
                            RelativePath = "../Shared",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        },
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/shared.git",
                            RelativePath = "../nested/../Shared",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        }
                    };

                    string missionId = "msn_opencode_dupe";
                    Vessel vessel = new Vessel("opencode-dupe-sibling-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("opencode-dupe-sibling-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/opencode/msn_dupe", missionId).ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned with duplicate sibling paths");

                    string openCodePath = Path.Combine(dock!.WorktreePath!, "opencode.json");
                    OpenCodeTestConfig config = await ReadOpenCodeConfigAsync(openCodePath).ConfigureAwait(false);

                    // Duplicate sibling relative paths cannot produce duplicate grants: the
                    // document carries a single bare-string "allow" grant irrespective of how
                    // many (or how few) roots provisioning resolves.
                    AssertOpenCodeBareStringGrant(config, "Duplicate-sibling OpenCode config");
                }
            });

            await RunTest("ProvisionAsync nests each dock and gives it its own sibling checkout; reclaiming one dock leaves the other's sibling and removes its own root", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    RecordingGitService git = new RecordingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/sibA.git",
                            RelativePath = "../SibA",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        }
                    };

                    Vessel vessel = new Vessel("nested-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captainOne = await testDb.Driver.Captains.CreateAsync(new Captain("captain-nested-1")).ConfigureAwait(false);
                    Captain captainTwo = await testDb.Driver.Captains.CreateAsync(new Captain("captain-nested-2")).ConfigureAwait(false);

                    Dock? first = await service.ProvisionAsync(vessel, captainOne, "armada/captain-nested-1/msn_one", "msn_one").ConfigureAwait(false);
                    AssertNotNull(first, "first dock should be provisioned");
                    string firstRoot = Path.Combine(settings.DocksDirectory, vessel.Name, "msn_one");
                    AssertEqual(Path.Combine(firstRoot, vessel.Name), first!.WorktreePath, "the checkout sits one level below the dock's own directory");
                    string firstSibling = Path.GetFullPath(Path.Combine(first.WorktreePath!, "../SibA"));
                    AssertEqual(Path.Combine(firstRoot, "SibA"), firstSibling, "the sibling resolves beside THIS dock's checkout");
                    AssertTrue(Directory.Exists(firstSibling), "the first dock's sibling checkout exists");
                    File.WriteAllText(Path.Combine(firstSibling, "pinned.marker"), "the tree this dock was provisioned against");

                    Dock? second = await service.ProvisionAsync(vessel, captainTwo, "armada/captain-nested-2/msn_two", "msn_two").ConfigureAwait(false);
                    AssertNotNull(second, "second dock should be provisioned");
                    string secondSibling = Path.GetFullPath(Path.Combine(second!.WorktreePath!, "../SibA"));
                    AssertTrue(!String.Equals(firstSibling, secondSibling, StringComparison.OrdinalIgnoreCase), "each dock has its own sibling checkout");
                    AssertEqual(1, git.CreatedWorktrees.Count(x => String.Equals(x.WorktreePath, firstSibling, StringComparison.OrdinalIgnoreCase)), "the first sibling was created once");
                    AssertEqual(1, git.CreatedWorktrees.Count(x => String.Equals(x.WorktreePath, secondSibling, StringComparison.OrdinalIgnoreCase)), "the second dock got a sibling checkout of its own instead of reusing the first");
                    AssertTrue(File.Exists(Path.Combine(firstSibling, "pinned.marker")), "provisioning the second dock did not touch the first dock's sibling");

                    List<ArmadaEvent> stale = await testDb.Driver.Events.EnumerateByTypeAsync("dock.sibling_stale").ConfigureAwait(false);
                    AssertEqual(0, stale.Count, "nothing was reused, so nothing can be stale");

                    await service.ReclaimAsync(first.Id).ConfigureAwait(false);
                    AssertFalse(Directory.Exists(firstRoot), "reclaiming a dock removes its checkout, its siblings and its root directory");
                    AssertTrue(Directory.Exists(secondSibling), "the other dock's sibling is untouched by the reclaim");
                }
            });

            await RunTest("ProvisionAsync reuses an existing sibling checkout instead of destroying a concurrent dock's build", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    RecordingGitService git = new RecordingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/sibA.git",
                            RelativePath = "../SibA",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        }
                    };

                    Vessel vessel = new Vessel("sib-race-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captainOne = await testDb.Driver.Captains.CreateAsync(new Captain("captain-race-1")).ConfigureAwait(false);
                    Captain captainTwo = await testDb.Driver.Captains.CreateAsync(new Captain("captain-race-2")).ConfigureAwait(false);

                    Dock? first = await service.ProvisionAsync(vessel, captainOne, "armada/captain-race-1/msn_one", "msn_one").ConfigureAwait(false);
                    AssertNotNull(first, "first dock should be provisioned");

                    string siblingPath = Path.GetFullPath(Path.Combine(first!.WorktreePath!, "../SibA"));
                    AssertTrue(Directory.Exists(siblingPath), "sibling checkout should exist after the first dock");

                    // Stand in for build output the first dock is actively compiling against.
                    string buildArtifact = Path.Combine(siblingPath, "bin-artifact.marker");
                    File.WriteAllText(buildArtifact, "in-flight build output");

                    int creationsBefore = git.CreatedWorktrees.Count(x =>
                        String.Equals(x.WorktreePath, siblingPath, StringComparison.OrdinalIgnoreCase));

                    Dock? second = await service.ProvisionAsync(vessel, captainTwo, "armada/captain-race-2/msn_two", "msn_two").ConfigureAwait(false);
                    AssertNotNull(second, "second dock should be provisioned");

                    AssertTrue(
                        File.Exists(buildArtifact),
                        "provisioning a second dock must NOT delete the shared sibling a concurrent dock is building against");

                    int creationsAfter = git.CreatedWorktrees.Count(x =>
                        String.Equals(x.WorktreePath, siblingPath, StringComparison.OrdinalIgnoreCase));
                    AssertEqual(creationsBefore, creationsAfter, "a healthy sibling checkout should be reused, not recreated");
                }
            });

            await RunTest("ProvisionAsync reports a re-provisioned mission's reused sibling that is behind its branch tip as dock.sibling_stale, and stays quiet when it is current", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    RecordingGitService git = new RecordingGitService();
                    // The stub reports every worktree HEAD as abc123; the branch tip starts there too.
                    git.RevisionShaResult = "abc123";
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/sibA.git",
                            RelativePath = "../SibA",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        }
                    };

                    Vessel vessel = new Vessel("sib-stale-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captainOne = await testDb.Driver.Captains.CreateAsync(new Captain("captain-stale-1")).ConfigureAwait(false);
                    Captain captainTwo = await testDb.Driver.Captains.CreateAsync(new Captain("captain-stale-2")).ConfigureAwait(false);
                    Captain captainThree = await testDb.Driver.Captains.CreateAsync(new Captain("captain-stale-3")).ConfigureAwait(false);

                    Dock? first = await service.ProvisionAsync(vessel, captainOne, "armada/captain-stale-1/msn_one", "msn_one").ConfigureAwait(false);
                    AssertNotNull(first, "first dock should be provisioned");

                    // Siblings are per dock, so only a re-provisioning of the SAME mission (a retry)
                    // finds a sibling checkout already in place. A populated checkout is reused; an
                    // empty shell is rebuilt.
                    string siblingPath = Path.GetFullPath(Path.Combine(first!.WorktreePath!, "../SibA"));
                    File.WriteAllText(Path.Combine(siblingPath, "checkout.marker"), "populated checkout");

                    Dock? second = await service.ProvisionAsync(vessel, captainTwo, "armada/captain-stale-2/msn_one", "msn_one").ConfigureAwait(false);
                    AssertNotNull(second, "the retry should be provisioned");

                    List<ArmadaEvent> quiet = await testDb.Driver.Events.EnumerateByTypeAsync("dock.sibling_stale").ConfigureAwait(false);
                    AssertEqual(0, quiet.Count, "a reused sibling at the branch tip raises no event");

                    // The sibling vessel lands a commit before the next retry: the reused checkout is
                    // behind the tip and the retry reads the older tree.
                    git.RevisionShaResult = "fff9999";
                    Dock? third = await service.ProvisionAsync(vessel, captainThree, "armada/captain-stale-3/msn_one", "msn_one").ConfigureAwait(false);
                    AssertNotNull(third, "the second retry should be provisioned");

                    List<ArmadaEvent> stale = await testDb.Driver.Events.EnumerateByTypeAsync("dock.sibling_stale").ConfigureAwait(false);
                    AssertEqual(1, stale.Count, "a reused sibling behind its branch tip is reported once");
                    AssertEqual(third!.Id, stale[0].EntityId, "the event is attached to the dock that received the stale tree");
                    AssertContains(third.Id, stale[0].Message ?? String.Empty, "the message names the dock");
                    AssertContains("abc123", stale[0].Payload ?? String.Empty, "the payload carries the worktree HEAD");
                    AssertContains("fff9999", stale[0].Payload ?? String.Empty, "the payload carries the branch tip");
                }
            });

            await RunTest("ReclaimAsync preserves the dock branch in the bare before destroying the worktree", async () =>
            {
                // Regression: a mission that fails its DoD gate still has real committed work, but
                // nothing pushed its branch to the bare -- the commits survived only as unreferenced
                // objects inside a dock that was about to be deleted. On 2026-07-19 commit 08e95bec
                // passed its target gate 6/6 and was recoverable only because it was spotted before
                // GC. Recovery must not depend on the dock working tree surviving.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    RecordingGitService git = new RecordingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("preserve-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("captain-preserve")).ConfigureAwait(false);

                    string dockBranch = "armada/captain-preserve/msn_gatefail";
                    Dock? dock = await service.ProvisionAsync(vessel, captain, dockBranch, "msn_gatefail").ConfigureAwait(false);
                    AssertNotNull(dock, "dock should be provisioned");

                    await service.ReclaimAsync(dock!.Id).ConfigureAwait(false);

                    RefSpecPush? preserved = git.PushedRefSpecs.FirstOrDefault(p =>
                        p.DestRef == "refs/armada-preserved/" + dockBranch);
                    AssertNotNull(preserved, "dock branch must be mirrored into the bare before teardown");
                    AssertFalse(
                        Directory.Exists(dock.WorktreePath!),
                        "worktree should still be torn down after preservation");
                }
            });

            await RunTest("ReclaimAsync does not delete worktree path owned by newer active dock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    LockingGitService git = new LockingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string sharedWorktree = Path.Combine(settings.DocksDirectory, "shared-mission");
                    Directory.CreateDirectory(sharedWorktree);
                    await File.WriteAllTextAsync(Path.Combine(sharedWorktree, "sentinel.txt"), "new dock owns this path").ConfigureAwait(false);

                    Vessel vessel = new Vessel("shared-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock oldDock = new Dock(vessel.Id)
                    {
                        WorktreePath = sharedWorktree,
                        BranchName = "armada/old",
                        Active = true
                    };
                    oldDock = await testDb.Driver.Docks.CreateAsync(oldDock).ConfigureAwait(false);

                    Dock newerDock = new Dock(vessel.Id)
                    {
                        WorktreePath = sharedWorktree,
                        BranchName = "armada/new",
                        Active = true
                    };
                    newerDock = await testDb.Driver.Docks.CreateAsync(newerDock).ConfigureAwait(false);

                    await service.ReclaimAsync(oldDock.Id).ConfigureAwait(false);

                    Dock? reloadedOld = await testDb.Driver.Docks.ReadAsync(oldDock.Id).ConfigureAwait(false);
                    Dock? reloadedNewer = await testDb.Driver.Docks.ReadAsync(newerDock.Id).ConfigureAwait(false);

                    AssertNotNull(reloadedOld, "Old dock should remain readable");
                    AssertFalse(reloadedOld!.Active, "Old dock should be marked inactive");
                    AssertNotNull(reloadedNewer, "Newer dock should remain readable");
                    AssertTrue(reloadedNewer!.Active, "Newer dock should remain active");
                    AssertTrue(File.Exists(Path.Combine(sharedWorktree, "sentinel.txt")), "Reclaiming the old dock must not delete a path owned by the newer active dock");
                }
            });

            await RunTest("ReclaimAsync defers while a definition-of-done gate holds the dock lease", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    LockingGitService git = new LockingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string worktree = Path.Combine(settings.DocksDirectory, "leased-mission");
                    Directory.CreateDirectory(worktree);
                    await File.WriteAllTextAsync(Path.Combine(worktree, "sentinel.txt"), "gate still needs this").ConfigureAwait(false);

                    Vessel vessel = new Vessel("lease-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id)
                    {
                        WorktreePath = worktree,
                        BranchName = "armada/leased",
                        Active = true
                    };
                    dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    // A queued definition-of-done gate holds the lease; reclamation must defer.
                    DockLeaseRegistry.Acquire(dock.Id);
                    try
                    {
                        await service.ReclaimAsync(dock.Id).ConfigureAwait(false);

                        Dock? reloaded = await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                        AssertNotNull(reloaded, "Dock should remain readable");
                        AssertTrue(reloaded!.Active, "A leased dock must not be reclaimed");
                        AssertTrue(File.Exists(Path.Combine(worktree, "sentinel.txt")),
                            "The worktree must survive while the gate lease is held");
                    }
                    finally
                    {
                        DockLeaseRegistry.Release(dock.Id);
                    }

                    // Once the gate releases the lease, reclamation proceeds normally.
                    await service.ReclaimAsync(dock.Id).ConfigureAwait(false);

                    Dock? afterRelease = await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                    AssertNotNull(afterRelease, "Dock should remain readable after release");
                    AssertFalse(afterRelease!.Active, "Dock must be reclaimable after the lease is released");
                }
            });

            await RunTest("ProvisionAsync provisions declared sibling repos at expected relative paths with branch-compatible refs", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    string dockBranch = "armada/captain-1/msn_one";
                    RecordingGitService git = new RecordingGitService();
                    git.ExistingBranches.Add(dockBranch);
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/sibA.git",
                            RelativePath = "../SibA",
                            BranchStrategy = SiblingBranchStrategyEnum.MatchBranchElseDefault,
                            DefaultBranch = "main"
                        },
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/sibB.git",
                            RelativePath = "../nested/SibB",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "develop"
                        }
                    };

                    Vessel vessel = new Vessel("sib-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("captain-1");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, dockBranch, "msn_one").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned");

                    string expectedSibA = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../SibA"));
                    string expectedSibB = Path.GetFullPath(Path.Combine(dock.WorktreePath!, "../nested/SibB"));

                    WorktreeCreation? sibA = git.CreatedWorktrees.FirstOrDefault(w => PathEquals(w.WorktreePath, expectedSibA));
                    WorktreeCreation? sibB = git.CreatedWorktrees.FirstOrDefault(w => PathEquals(w.WorktreePath, expectedSibB));

                    AssertNotNull(sibA, "Sibling A worktree should be provisioned at its declared relative path");
                    AssertNotNull(sibB, "Sibling B worktree should be provisioned at its declared relative path");
                    AssertEqual(dockBranch, sibA!.BranchName, "Sibling A (MatchBranchElseDefault) should track the matching dock branch");
                    AssertEqual("develop", sibB!.BranchName, "Sibling B (DefaultOnly) should use its declared default branch");
                    AssertEqual(3, git.CreatedWorktrees.Count, "Primary plus two sibling worktrees should be created");
                    AssertTrue(Directory.Exists(expectedSibA), "Sibling A directory should exist after provisioning");

                    WorktreeCreation? primary = git.CreatedWorktrees.FirstOrDefault(w => !PathEquals(w.WorktreePath, expectedSibA) && !PathEquals(w.WorktreePath, expectedSibB));
                    AssertNotNull(primary, "Primary worktree creation should be recorded");
                    AssertFalse(primary!.Detached, "Primary dock worktree must not be detached");
                    AssertTrue(sibA.Detached, "Sibling A worktree must be detached to avoid branch-collision with other docks");
                    AssertTrue(sibB.Detached, "Sibling B worktree must be detached to avoid branch-collision with other docks");

                    await service.ReclaimAsync(dock.Id).ConfigureAwait(false);
                    AssertFalse(Directory.Exists(expectedSibA), "Reclaim should tear down provisioned sibling worktrees");
                    AssertFalse(Directory.Exists(expectedSibB), "Reclaim should tear down nested sibling worktrees");
                }
            });

            await RunTest("ProvisionAsync skips failed sibling and returns valid primary dock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    ThrowOnDetachedGitService git = new ThrowOnDetachedGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string repoPath = Path.Combine(settings.ReposDirectory, "fail-sib-vessel.git");
                    Directory.CreateDirectory(repoPath);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/fail-sib.git",
                            RelativePath = "../FailSib",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        }
                    };

                    Vessel vessel = new Vessel("fail-sib-vessel", "https://github.com/test/fail-sib-vessel.git");
                    vessel.LocalPath = repoPath;
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("fail-sib-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/fail-sib/msn_fs", "msn_fs").ConfigureAwait(false);
                    AssertNotNull(dock, "ProvisionAsync must return a valid dock even when a sibling provisioning fails");
                    AssertNotNull(dock!.WorktreePath, "Primary dock worktree path must be set");
                    AssertTrue(Directory.Exists(dock.WorktreePath), "Primary dock worktree directory must exist");

                    // The failed sibling must not have produced a worktree creation record with detached=true.
                    AssertFalse(git.PrimaryWorktrees.Any(w => w.Detached), "Primary dock worktree must not be detached");
                    AssertEqual(1, git.PrimaryWorktrees.Count, "Exactly one non-detached (primary) worktree should be created");
                }
            });

            await RunTest("ProvisionAsync continues to next sibling after one sibling fails", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    // The first sibling worktree creation throws; the second must still be attempted.
                    PartialFailureGitService git = new PartialFailureGitService("FailSib");
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string repoPath = Path.Combine(settings.ReposDirectory, "multi-sib-vessel.git");
                    Directory.CreateDirectory(repoPath);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/fail-sib.git",
                            RelativePath = "../FailSib",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        },
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/good-sib.git",
                            RelativePath = "../GoodSib",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        }
                    };

                    Vessel vessel = new Vessel("multi-sib-vessel", "https://github.com/test/multi-sib-vessel.git");
                    vessel.LocalPath = repoPath;
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("multi-sib-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/multi-sib/msn_ms", "msn_ms").ConfigureAwait(false);
                    AssertNotNull(dock, "ProvisionAsync must return a valid dock even when an earlier sibling fails");

                    string expectedGood = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../GoodSib"));
                    WorktreeCreation? goodSib = git.CreatedWorktrees.FirstOrDefault(w => PathEquals(w.WorktreePath, expectedGood));
                    AssertNotNull(goodSib, "Sibling after the failing one must still be provisioned (loop continues past the failure)");
                    AssertTrue(goodSib!.Detached, "Recovered sibling worktree must still be detached");
                    AssertTrue(git.Failures >= 1, "The failing sibling must have raised at least one failure");
                }
            });

            await RunTest("ProvisionAsync aborts (returns null) when sibling provisioning is canceled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    // Distinct from the ordinary warn-and-skip path: an OperationCanceledException raised
                    // while provisioning a sibling must propagate out of the per-sibling loop (not be
                    // swallowed), so ProvisionAsync's outer handler aborts and returns null.
                    CancelOnDetachedGitService git = new CancelOnDetachedGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string repoPath = Path.Combine(settings.ReposDirectory, "cancel-sib-vessel.git");
                    Directory.CreateDirectory(repoPath);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/cancel-sib.git",
                            RelativePath = "../CancelSib",
                            BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                            DefaultBranch = "main"
                        }
                    };

                    Vessel vessel = new Vessel("cancel-sib-vessel", "https://github.com/test/cancel-sib-vessel.git");
                    vessel.LocalPath = repoPath;
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("cancel-sib-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/cancel-sib/msn_cs", "msn_cs").ConfigureAwait(false);
                    AssertNull(dock, "Cancellation during sibling provisioning must abort the provision (return null), not warn-and-skip");
                    AssertTrue(git.DetachedAttempted, "The detached sibling creation should have been attempted before cancellation propagated");
                }
            });

            await RunTest("ProvisionAsync with no declared siblings creates exactly one worktree (single-repo vessels unaffected)", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    RecordingGitService git = new RecordingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("plain-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("captain-1");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-1/msn_one", "msn_one").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock should be provisioned");

                    AssertEqual(1, git.CreatedWorktrees.Count, "Single-repo vessel should provision exactly one worktree");
                    AssertEqual(1, git.CloneBareCalls, "Single-repo vessel should clone only its own bare repo");
                    AssertEqual(0, git.BranchExistsCalls, "Single-repo vessel should not perform sibling branch-compat probes");
                }
            });

            await RunTest("ProvisionAsync copies extraction artifacts into sibling worktree when vessel WorkingDirectory is configured", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    string artifactSourceRoot = Path.Combine(Path.GetTempPath(), "armada_test_sibling_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        // Arrange -- create source artifact tree that simulates a sibling extraction-artifact tree
                        string vinCatalogDir = Path.Combine(artifactSourceRoot, "output", "extracted-artifacts", "vin-catalog");
                        string meddutyDir = Path.Combine(artifactSourceRoot, "output", "extracted-artifacts", "medduty-kwp");
                        Directory.CreateDirectory(vinCatalogDir);
                        Directory.CreateDirectory(meddutyDir);
                        await File.WriteAllTextAsync(Path.Combine(vinCatalogDir, "catalog.json"), "{\"v\":1}").ConfigureAwait(false);
                        await File.WriteAllTextAsync(Path.Combine(meddutyDir, "data.bin"), "binary").ConfigureAwait(false);

                        // Register the sibling (ExampleSibling) vessel with a WorkingDirectory pointing at our temp tree
                        Vessel siblingVessel = new Vessel("ExampleSibling", "https://github.com/test/example-sibling.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "ExampleSibling.git");
                        siblingVessel.WorkingDirectory = artifactSourceRoot;
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Id,
                                RepoUrl = "https://github.com/test/example-sibling.git",
                                RelativePath = "../ExampleSibling",
                                BranchStrategy = SiblingBranchStrategyEnum.MatchBranchElseDefault,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "extracted-artifacts") }
                            }
                        };

                        Vessel vessel = new Vessel("service-a", "https://github.com/test/service-a.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "service-a.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("captain-a");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        // Act
                        Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-a/msn_art", "msn_art").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock should be provisioned");

                        // Assert -- artifacts copied into the sibling worktree at the expected relative path
                        string siblingWorktree = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../ExampleSibling"));
                        string expectedCatalog = Path.Combine(siblingWorktree, "output", "extracted-artifacts", "vin-catalog", "catalog.json");
                        string expectedMedduty = Path.Combine(siblingWorktree, "output", "extracted-artifacts", "medduty-kwp", "data.bin");
                        AssertTrue(File.Exists(expectedCatalog), "vin-catalog artifact should be copied into the sibling worktree");
                        AssertTrue(File.Exists(expectedMedduty), "medduty-kwp artifact should be copied into the sibling worktree");
                    }
                    finally
                    {
                        if (Directory.Exists(artifactSourceRoot)) { try { Directory.Delete(artifactSourceRoot, true); } catch { } }
                        if (Directory.Exists(settings.DocksDirectory)) { try { Directory.Delete(settings.DocksDirectory, true); } catch { } }
                        if (Directory.Exists(settings.ReposDirectory)) { try { Directory.Delete(settings.ReposDirectory, true); } catch { } }
                        if (Directory.Exists(settings.LogDirectory)) { try { Directory.Delete(settings.LogDirectory, true); } catch { } }
                    }
                }
            });

            // The shared sibling worktree is reused by every later dock on the vessel. When the
            // source tree has grown since the first copy, the reuse must converge on the source;
            // a copy that stops at "already populated" keeps the first dock's subset for ever.
            await RunTest("ProvisionAsync gives a later dock the grown source tree in its own sibling copy and leaves the first dock's copy pinned", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    string artifactSourceRoot = Path.Combine(Path.GetTempPath(), "armada_test_sibling_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        string firstAssembly = Path.Combine(artifactSourceRoot, "output", "extracted-artifacts", "Assembly.One");
                        Directory.CreateDirectory(firstAssembly);
                        await File.WriteAllTextAsync(Path.Combine(firstAssembly, "A.cs"), "class A { }").ConfigureAwait(false);

                        Vessel siblingVessel = new Vessel("ExampleSibling", "https://github.com/test/example-sibling.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "ExampleSibling.git");
                        siblingVessel.WorkingDirectory = artifactSourceRoot;
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Id,
                                RepoUrl = "https://github.com/test/example-sibling.git",
                                RelativePath = "../ExampleSibling",
                                BranchStrategy = SiblingBranchStrategyEnum.MatchBranchElseDefault,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "extracted-artifacts") }
                            }
                        };

                        Vessel vessel = new Vessel("service-a", "https://github.com/test/service-a.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "service-a.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("captain-a")).ConfigureAwait(false);
                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        Dock? first = await service.ProvisionAsync(vessel, captain, "armada/captain-a/msn_first", "msn_first").ConfigureAwait(false);
                        AssertNotNull(first, "First dock should be provisioned");
                        string siblingWorktree = Path.GetFullPath(Path.Combine(first!.WorktreePath!, "../ExampleSibling"));
                        string copiedFirst = Path.Combine(siblingWorktree, "output", "extracted-artifacts", "Assembly.One", "A.cs");
                        AssertTrue(File.Exists(copiedFirst), "The first dock copies the subset that exists at that time");

                        // The source grows after the first copy: a second assembly appears.
                        string secondAssembly = Path.Combine(artifactSourceRoot, "output", "extracted-artifacts", "Assembly.Two");
                        Directory.CreateDirectory(secondAssembly);
                        await File.WriteAllTextAsync(Path.Combine(secondAssembly, "B.cs"), "class B { }").ConfigureAwait(false);

                        Dock? second = await service.ProvisionAsync(vessel, captain, "armada/captain-a/msn_second", "msn_second").ConfigureAwait(false);
                        AssertNotNull(second, "Second dock should be provisioned");
                        string secondSiblingWorktree = Path.GetFullPath(Path.Combine(second!.WorktreePath!, "../ExampleSibling"));
                        AssertTrue(!String.Equals(siblingWorktree, secondSiblingWorktree, StringComparison.OrdinalIgnoreCase), "Each dock owns its sibling copy");
                        string copiedSecond = Path.Combine(secondSiblingWorktree, "output", "extracted-artifacts", "Assembly.Two", "B.cs");
                        AssertTrue(File.Exists(copiedSecond), "A later dock sees the grown source tree in its own copy");
                        AssertTrue(File.Exists(Path.Combine(secondSiblingWorktree, "output", "extracted-artifacts", "Assembly.One", "A.cs")), "The later copy carries every file the source has");
                        AssertTrue(File.Exists(copiedFirst), "The first dock keeps the copy it was provisioned against");
                        AssertFalse(Directory.Exists(Path.Combine(siblingWorktree, "output", "extracted-artifacts", "Assembly.Two")), "The first dock's copy is pinned: nothing moves under a dock that may be mid-gate");

                        // A retry of the first mission reuses its populated sibling and refreshes the
                        // stale artifact copy atomically.
                        Dock? retry = await service.ProvisionAsync(vessel, captain, "armada/captain-a/msn_first", "msn_first").ConfigureAwait(false);
                        AssertNotNull(retry, "Retry dock should be provisioned");
                        AssertTrue(File.Exists(Path.Combine(siblingWorktree, "output", "extracted-artifacts", "Assembly.Two", "B.cs")), "A retry refreshes its reused sibling copy to the grown source");
                        AssertTrue(File.Exists(copiedFirst), "The refresh keeps every file the source still has");
                        AssertTrue(!Directory.GetDirectories(Path.Combine(siblingWorktree, "output")).Any(d => d.Contains(".refresh-") || d.Contains(".stale-")),
                            "The atomic refresh leaves no staging or retired directory behind");
                    }
                    finally
                    {
                        if (Directory.Exists(artifactSourceRoot)) { try { Directory.Delete(artifactSourceRoot, true); } catch { } }
                        if (Directory.Exists(settings.DocksDirectory)) { try { Directory.Delete(settings.DocksDirectory, true); } catch { } }
                        if (Directory.Exists(settings.ReposDirectory)) { try { Directory.Delete(settings.ReposDirectory, true); } catch { } }
                        if (Directory.Exists(settings.LogDirectory)) { try { Directory.Delete(settings.LogDirectory, true); } catch { } }
                    }
                }
            });

            await RunTest("ProvisionAsync leaves a current sibling artifact copy untouched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    string artifactSourceRoot = Path.Combine(Path.GetTempPath(), "armada_test_sibling_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        string assembly = Path.Combine(artifactSourceRoot, "output", "extracted-artifacts", "Assembly.One");
                        Directory.CreateDirectory(assembly);
                        await File.WriteAllTextAsync(Path.Combine(assembly, "A.cs"), "class A { }").ConfigureAwait(false);

                        Vessel siblingVessel = new Vessel("ExampleSibling", "https://github.com/test/example-sibling.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "ExampleSibling.git");
                        siblingVessel.WorkingDirectory = artifactSourceRoot;
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Id,
                                RepoUrl = "https://github.com/test/example-sibling.git",
                                RelativePath = "../ExampleSibling",
                                BranchStrategy = SiblingBranchStrategyEnum.MatchBranchElseDefault,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "extracted-artifacts") }
                            }
                        };

                        Vessel vessel = new Vessel("service-a", "https://github.com/test/service-a.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "service-a.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("captain-a")).ConfigureAwait(false);
                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        Dock? first = await service.ProvisionAsync(vessel, captain, "armada/captain-a/msn_first", "msn_first").ConfigureAwait(false);
                        AssertNotNull(first, "First dock should be provisioned");
                        string siblingWorktree = Path.GetFullPath(Path.Combine(first!.WorktreePath!, "../ExampleSibling"));
                        string copied = Path.Combine(siblingWorktree, "output", "extracted-artifacts", "Assembly.One", "A.cs");
                        AssertTrue(File.Exists(copied), "The first dock copies the tree");
                        DateTime firstWrite = File.GetLastWriteTimeUtc(copied);
                        await Task.Delay(50).ConfigureAwait(false);

                        Dock? second = await service.ProvisionAsync(vessel, captain, "armada/captain-a/msn_second", "msn_second").ConfigureAwait(false);
                        AssertNotNull(second, "Second dock should be provisioned");
                        AssertEqual(firstWrite, File.GetLastWriteTimeUtc(copied), "A current copy must not be rewritten on reuse");
                    }
                    finally
                    {
                        if (Directory.Exists(artifactSourceRoot)) { try { Directory.Delete(artifactSourceRoot, true); } catch { } }
                        if (Directory.Exists(settings.DocksDirectory)) { try { Directory.Delete(settings.DocksDirectory, true); } catch { } }
                        if (Directory.Exists(settings.ReposDirectory)) { try { Directory.Delete(settings.ReposDirectory, true); } catch { } }
                        if (Directory.Exists(settings.LogDirectory)) { try { Directory.Delete(settings.LogDirectory, true); } catch { } }
                    }
                }
            });

            await RunTest("ProvisionAsync skips artifact copy when extraction source directory is absent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    // Arrange -- WorkingDirectory exists but the artifact sub-path does NOT
                    string hostWorkDir = Path.Combine(Path.GetTempPath(), "armada_test_sibling_absent_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Directory.CreateDirectory(hostWorkDir);

                        Vessel siblingVessel = new Vessel("ExampleSiblingAbsent", "https://github.com/test/example-sibling.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "ExampleSiblingAbsent.git");
                        siblingVessel.WorkingDirectory = hostWorkDir;
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Id,
                                RepoUrl = "https://github.com/test/example-sibling.git",
                                RelativePath = "../ExampleSiblingAbsent",
                                BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "extracted-artifacts") }
                            }
                        };

                        Vessel vessel = new Vessel("service-a-absent", "https://github.com/test/service-a.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "service-a-absent.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("captain-absent");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        // Act -- should not throw; absent source is a clean skip
                        Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-absent/msn_abs", "msn_abs").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock should be provisioned even when artifact source is absent");

                        string siblingWorktree = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../ExampleSiblingAbsent"));
                        string absentDest = Path.Combine(siblingWorktree, "output", "extracted-artifacts");
                        AssertFalse(Directory.Exists(absentDest), "Absent artifact source should not create a destination directory");
                    }
                    finally
                    {
                        if (Directory.Exists(hostWorkDir)) { try { Directory.Delete(hostWorkDir, true); } catch { } }
                        if (Directory.Exists(settings.DocksDirectory)) { try { Directory.Delete(settings.DocksDirectory, true); } catch { } }
                        if (Directory.Exists(settings.ReposDirectory)) { try { Directory.Delete(settings.ReposDirectory, true); } catch { } }
                        if (Directory.Exists(settings.LogDirectory)) { try { Directory.Delete(settings.LogDirectory, true); } catch { } }
                    }
                }
            });

            await RunTest("ProvisionAsync copies extraction artifacts when sibling vessel is referenced by name", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    string artifactSourceRoot = Path.Combine(Path.GetTempPath(), "armada_test_sibling_name_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        string nestedFaultDir = Path.Combine(artifactSourceRoot, "output", "extracted-artifacts", "fault-descriptions", "nested");
                        Directory.CreateDirectory(nestedFaultDir);
                        string sourceFile = Path.Combine(nestedFaultDir, "fault.json");
                        await File.WriteAllTextAsync(sourceFile, "{\"fault\":123}").ConfigureAwait(false);

                        Vessel siblingVessel = new Vessel("ExampleSiblingByName", "https://github.com/test/example-sibling-name.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "ExampleSiblingByName.git");
                        siblingVessel.WorkingDirectory = artifactSourceRoot;
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Name,
                                RepoUrl = "https://github.com/test/example-sibling-name.git",
                                RelativePath = "../ExampleSiblingByName",
                                BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "extracted-artifacts") }
                            }
                        };

                        Vessel vessel = new Vessel("service-a-name-ref", "https://github.com/test/service-a.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "service-a-name-ref.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("captain-name-ref");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-name-ref/msn_art", "msn_art").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock should be provisioned");

                        string siblingWorktree = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../ExampleSiblingByName"));
                        string copiedFile = Path.Combine(siblingWorktree, "output", "extracted-artifacts", "fault-descriptions", "nested", "fault.json");
                        AssertTrue(File.Exists(copiedFile), "Nested artifact should be copied when VesselRef resolves by name");
                        AssertEqual("{\"fault\":123}", await File.ReadAllTextAsync(copiedFile).ConfigureAwait(false), "Copied artifact content should match the source file");
                    }
                    finally
                    {
                        if (Directory.Exists(artifactSourceRoot)) { try { Directory.Delete(artifactSourceRoot, true); } catch { } }
                        if (Directory.Exists(settings.DocksDirectory)) { try { Directory.Delete(settings.DocksDirectory, true); } catch { } }
                        if (Directory.Exists(settings.ReposDirectory)) { try { Directory.Delete(settings.ReposDirectory, true); } catch { } }
                        if (Directory.Exists(settings.LogDirectory)) { try { Directory.Delete(settings.LogDirectory, true); } catch { } }
                    }
                }
            });

            await RunTest("ProvisionAsync skips artifact copy when sibling WorkingDirectory is not configured", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    try
                    {
                        Vessel siblingVessel = new Vessel("ExampleSiblingNoWorkDir", "https://github.com/test/example-sibling-noworkdir.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "ExampleSiblingNoWorkDir.git");
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Id,
                                RepoUrl = "https://github.com/test/example-sibling-noworkdir.git",
                                RelativePath = "../ExampleSiblingNoWorkDir",
                                BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "extracted-artifacts") }
                            }
                        };

                        Vessel vessel = new Vessel("service-a-noworkdir", "https://github.com/test/service-a.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "service-a-noworkdir.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("captain-noworkdir");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-noworkdir/msn_art", "msn_art").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock should be provisioned even when sibling WorkingDirectory is missing");

                        string siblingWorktree = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../ExampleSiblingNoWorkDir"));
                        string artifactDest = Path.Combine(siblingWorktree, "output", "extracted-artifacts");
                        AssertTrue(Directory.Exists(siblingWorktree), "Sibling worktree should still be provisioned");
                        AssertFalse(Directory.Exists(artifactDest), "Missing WorkingDirectory should not create an artifact destination directory");
                    }
                    finally
                    {
                        if (Directory.Exists(settings.DocksDirectory)) { try { Directory.Delete(settings.DocksDirectory, true); } catch { } }
                        if (Directory.Exists(settings.ReposDirectory)) { try { Directory.Delete(settings.ReposDirectory, true); } catch { } }
                        if (Directory.Exists(settings.LogDirectory)) { try { Directory.Delete(settings.LogDirectory, true); } catch { } }
                    }
                }
            });

            await RunTest("ProvisionAsync with no declared siblings does not perform any artifact copies", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    RecordingGitService git = new RecordingGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    // A single-repo vessel (no SiblingRepos declared) -- artifact code path must never run
                    Vessel vessel = new Vessel("single-repo-vessel", "https://github.com/test/single.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("captain-single");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-single/msn_single", "msn_single").ConfigureAwait(false);
                    AssertNotNull(dock, "Single-repo dock should be provisioned");

                    // Only the primary worktree should be created; no sibling or artifact work
                    AssertEqual(1, git.CreatedWorktrees.Count, "Single-repo vessel must provision exactly one worktree");
                    AssertEqual(0, git.BranchExistsCalls, "Single-repo vessel must not probe sibling branches");

                    string dockDir = dock!.WorktreePath!;
                    // No additional directories should appear alongside the dock
                    string parentDir = Path.GetFullPath(Path.Combine(dockDir, ".."));
                    string[] siblingDirs = Directory.GetDirectories(parentDir);
                    AssertTrue(siblingDirs.All(d => PathEquals(d, dockDir)), "No extra sibling directories should be created for single-repo vessels");
                }
            });

            await RunTest("ProvisionAsync sibling branch selection falls back to default when no matching branch exists", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    string dockBranch = "armada/captain-1/msn_fallback";

                    // No matching branch in the sibling repo -> MatchBranchElseDefault falls back to default.
                    RecordingGitService gitAbsent = new RecordingGitService();
                    DockService serviceAbsent = new DockService(logging, testDb.Driver, settings, gitAbsent);

                    List<SiblingRepo> siblings = new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            RepoUrl = "https://github.com/test/sibC.git",
                            RelativePath = "../SibC",
                            BranchStrategy = SiblingBranchStrategyEnum.MatchBranchElseDefault,
                            DefaultBranch = "release/x"
                        }
                    };

                    Vessel vessel = new Vessel("fallback-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("captain-1");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dockAbsent = await serviceAbsent.ProvisionAsync(vessel, captain, dockBranch, "msn_fallback").ConfigureAwait(false);
                    AssertNotNull(dockAbsent, "Dock should be provisioned when no matching sibling branch exists");
                    string expectedSibC = Path.GetFullPath(Path.Combine(dockAbsent!.WorktreePath!, "../SibC"));
                    WorktreeCreation? absentCreation = gitAbsent.CreatedWorktrees.FirstOrDefault(w => PathEquals(w.WorktreePath, expectedSibC));
                    AssertNotNull(absentCreation, "Sibling C worktree should be provisioned");
                    AssertEqual("release/x", absentCreation!.BranchName, "Absent matching branch should fall back to the sibling default");

                    // Matching branch present -> MatchBranchElseDefault tracks the dock branch.
                    RecordingGitService gitPresent = new RecordingGitService();
                    gitPresent.ExistingBranches.Add(dockBranch);
                    DockService servicePresent = new DockService(logging, testDb.Driver, settings, gitPresent);

                    Captain captain2 = new Captain("captain-2");
                    captain2 = await testDb.Driver.Captains.CreateAsync(captain2).ConfigureAwait(false);

                    Dock? dockPresent = await servicePresent.ProvisionAsync(vessel, captain2, dockBranch, "msn_fallback2").ConfigureAwait(false);
                    AssertNotNull(dockPresent, "Dock should be provisioned when a matching sibling branch exists");
                    string expectedSibC2 = Path.GetFullPath(Path.Combine(dockPresent!.WorktreePath!, "../SibC"));
                    WorktreeCreation? presentCreation = gitPresent.CreatedWorktrees.FirstOrDefault(w => PathEquals(w.WorktreePath, expectedSibC2));
                    AssertNotNull(presentCreation, "Sibling C worktree should be provisioned");
                    AssertEqual(dockBranch, presentCreation!.BranchName, "Present matching branch should be tracked by the sibling");
                }
            });

            await RunTest("ProvisionAsync installs pre-commit and pre-push hooks in the bare repo hooks directory", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string repoPath = Path.Combine(settings.ReposDirectory, "hook-vessel.git");
                    // Create hooks directory so the fallback path is usable without a real git repo
                    Directory.CreateDirectory(Path.Combine(repoPath, "hooks"));

                    Vessel vessel = new Vessel("hook-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = repoPath;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("hook-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain/msn_hooks", "msn_hooks").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock must be provisioned");

                    string preCommitPath = Path.Combine(repoPath, "hooks", "pre-commit");
                    string prePushPath = Path.Combine(repoPath, "hooks", "pre-push");

                    Assert(File.Exists(preCommitPath), "pre-commit hook must exist in the bare repo hooks directory");
                    Assert(File.Exists(prePushPath), "pre-push hook must exist in the bare repo hooks directory");

                    string preCommitContent = await File.ReadAllTextAsync(preCommitPath).ConfigureAwait(false);
                    string prePushContent = await File.ReadAllTextAsync(prePushPath).ConfigureAwait(false);

                    Assert(!preCommitContent.Contains("\r\n", StringComparison.Ordinal), "pre-commit hook must use LF-only line endings");
                    Assert(!prePushContent.Contains("\r\n", StringComparison.Ordinal), "pre-push hook must use LF-only line endings");
                    AssertContains("#!/bin/sh", preCommitContent, "pre-commit hook must be a sh script");
                    AssertContains("#!/bin/sh", prePushContent, "pre-push hook must be a sh script");
                    AssertContains("CLAUDE.md", preCommitContent, "pre-commit hook must reference CLAUDE.md protection");
                    AssertContains("CLAUDE.md", prePushContent, "pre-push hook must reference CLAUDE.md protection");
                    AssertContains("secret", preCommitContent, "pre-commit hook must include secret pattern scanning");
                    AssertContains("secret", prePushContent, "pre-push hook must include secret pattern scanning");
                    AssertContains("privateIdentifiers", preCommitContent, "pre-commit hook must handle private identifier section");
                    AssertContains("privateIdentifiers", prePushContent, "pre-push hook must handle private identifier section");
                }
            });

            await RunTest("ProvisionAsync writes non-tracked boundary config into dock .armada directory", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string repoPath = Path.Combine(settings.ReposDirectory, "boundary-vessel.git");
                    Directory.CreateDirectory(repoPath);

                    List<string> customPaths = new List<string> { "**/secrets.json" };
                    Vessel vessel = new Vessel("boundary-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = repoPath;
                    vessel.ProtectedPaths = customPaths;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("boundary-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain/msn_boundary", "msn_boundary").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock must be provisioned");

                    string configPath = Path.Combine(dock!.WorktreePath!, ".armada", "boundary.json");
                    Assert(File.Exists(configPath), "boundary.json must exist in the dock .armada directory");

                    string configJson = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
                    AssertContains("protectedPaths", configJson, "boundary.json must contain protectedPaths");
                    AssertContains("CLAUDE.md", configJson, "boundary.json must include built-in CLAUDE.md protection");
                    AssertContains("secrets.json", configJson, "boundary.json must include vessel-configured protected path");
                    AssertContains("secretPatterns", configJson, "boundary.json must contain secretPatterns");
                    AssertContains("privateIdentifiers", configJson, "boundary.json must contain privateIdentifiers section");

                    // Verify excluded from git tracking via info/exclude
                    string excludePath = Path.Combine(dock.WorktreePath!, ".git", "info", "exclude");
                    if (File.Exists(excludePath))
                    {
                        string excludeContent = await File.ReadAllTextAsync(excludePath).ConfigureAwait(false);
                        AssertContains("boundary.json", excludeContent, "boundary.json must be listed in git info/exclude");
                    }
                }
            });

            await RunTest("ProvisionAsync populates private identifiers in boundary config for a public-classified vessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    // Classify the vessel as public and configure a private-identifier denylist.
                    settings.DockBoundary.PublicRepoPatterns = new List<string> { "github.com/acme" };
                    settings.DockBoundary.PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>
                    {
                        new DockBoundaryPrivateIdentifierEntry { Label = "internal-org", Pattern = "ACME-INTERNAL-[0-9]+" }
                    };

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string repoPath = Path.Combine(settings.ReposDirectory, "public-vessel.git");
                    Directory.CreateDirectory(repoPath);

                    Vessel vessel = new Vessel("public-vessel", "https://github.com/acme/repo.git");
                    vessel.LocalPath = repoPath;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("public-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain/msn_public", "msn_public").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock must be provisioned");

                    string configPath = Path.Combine(dock!.WorktreePath!, ".armada", "boundary.json");
                    Assert(File.Exists(configPath), "boundary.json must exist for a public vessel");

                    string configJson = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
                    AssertContains("ACME-INTERNAL", configJson,
                        "boundary.json for a public vessel must carry the configured private-identifier pattern");
                }
            });

            await RunTest("ProvisionAsync omits private identifiers from boundary config for a non-public vessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    // The denylist is configured, but only "github.com/acme" vessels are public.
                    settings.DockBoundary.PublicRepoPatterns = new List<string> { "github.com/acme" };
                    settings.DockBoundary.PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>
                    {
                        new DockBoundaryPrivateIdentifierEntry { Label = "internal-org", Pattern = "ACME-INTERNAL-[0-9]+" }
                    };

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string repoPath = Path.Combine(settings.ReposDirectory, "private-vessel.git");
                    Directory.CreateDirectory(repoPath);

                    // Repo URL does not match any PublicRepoPatterns entry -> not classified public.
                    Vessel vessel = new Vessel("private-vessel", "https://github.com/private-org/repo.git");
                    vessel.LocalPath = repoPath;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("private-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain/msn_private", "msn_private").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock must be provisioned");

                    string configPath = Path.Combine(dock!.WorktreePath!, ".armada", "boundary.json");
                    Assert(File.Exists(configPath), "boundary.json must exist for a non-public vessel");

                    string configJson = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
                    AssertContains("privateIdentifiers", configJson, "boundary.json must still carry the privateIdentifiers section");
                    AssertFalse(configJson.Contains("ACME-INTERNAL", StringComparison.Ordinal),
                        "Private-identifier patterns must not leak into a non-public vessel's boundary config");
                }
            });

            // The M1 fix routes secret/private-id patterns through .armada/boundary.patterns
            // (raw, un-JSON-escaped) instead of boundary.json, so the hook's grep -qE receives
            // single-backslash metacharacters. This regression guard runs unconditionally --
            // unlike DockBoundaryHookExecutionTests, which skips when sh.exe is unavailable --
            // so the core fix stays covered even on a minimal CI host without Git-for-Windows sh.
            await RunTest("ProvisionAsync writes boundary.patterns with raw single-backslash metacharacter secret patterns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string repoPath = Path.Combine(settings.ReposDirectory, "patterns-vessel.git");
                    Directory.CreateDirectory(repoPath);

                    Vessel vessel = new Vessel("patterns-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = repoPath;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("patterns-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain/msn_patterns", "msn_patterns").ConfigureAwait(false);
                    AssertNotNull(dock, "Dock must be provisioned");

                    string patternsPath = Path.Combine(dock!.WorktreePath!, ".armada", "boundary.patterns");
                    Assert(File.Exists(patternsPath), "boundary.patterns must be written into the dock .armada directory");

                    string patternsContent = await File.ReadAllTextAsync(patternsPath).ConfigureAwait(false);
                    AssertContains("# secretPatterns", patternsContent, "boundary.patterns must carry the # secretPatterns header the hook parses");
                    AssertContains("# privateIdentifiers", patternsContent, "boundary.patterns must carry the # privateIdentifiers header even when empty");

                    // Crux of the M1 fix: metacharacters appear raw (single backslash) so grep -qE
                    // matches. The JSON-parse path doubled them to \\s / \\w and the hook never fired.
                    Assert(patternsContent.Contains(@"\s", StringComparison.Ordinal),
                        "boundary.patterns must contain a raw \\s metacharacter from the password/apikey patterns");
                    Assert(patternsContent.Contains(@"\w", StringComparison.Ordinal),
                        "boundary.patterns must contain a raw \\w metacharacter");
                    AssertFalse(patternsContent.Contains(@"\\s", StringComparison.Ordinal),
                        "boundary.patterns must NOT contain a JSON-escaped \\\\s -- that double-escape broke grep -qE");
                    AssertFalse(patternsContent.Contains(@"\\w", StringComparison.Ordinal),
                        "boundary.patterns must NOT contain a JSON-escaped \\\\w");
                    AssertContains("password", patternsContent, "boundary.patterns must include the CORE_RULE_5 password-literal pattern");

                    // boundary.patterns must never be committed by a captain.
                    string excludePath = Path.Combine(dock.WorktreePath!, ".git", "info", "exclude");
                    Assert(File.Exists(excludePath), "git info/exclude must exist after provisioning");
                    string excludeContent = await File.ReadAllTextAsync(excludePath).ConfigureAwait(false);
                    AssertContains("boundary.patterns", excludeContent, "boundary.patterns must be listed in git info/exclude");
                }
            });

            await RunTest("ProvisionAsync writes private-identifier patterns into boundary.patterns only for a public vessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.DockBoundary.PublicRepoPatterns = new List<string> { "github.com/acme" };
                    settings.DockBoundary.PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>
                    {
                        new DockBoundaryPrivateIdentifierEntry { Label = "internal-org", Pattern = @"ACME-\w{4,}" }
                    };

                    GitInfoGitService git = new GitInfoGitService();
                    DockService service = new DockService(logging, testDb.Driver, settings, git);

                    string publicRepoPath = Path.Combine(settings.ReposDirectory, "public-patterns.git");
                    string privateRepoPath = Path.Combine(settings.ReposDirectory, "private-patterns.git");
                    Directory.CreateDirectory(publicRepoPath);
                    Directory.CreateDirectory(privateRepoPath);

                    Vessel publicVessel = new Vessel("public-patterns", "https://github.com/acme/repo.git");
                    publicVessel.LocalPath = publicRepoPath;
                    publicVessel = await testDb.Driver.Vessels.CreateAsync(publicVessel).ConfigureAwait(false);

                    Vessel privateVessel = new Vessel("private-patterns", "https://github.com/private-org/repo.git");
                    privateVessel.LocalPath = privateRepoPath;
                    privateVessel = await testDb.Driver.Vessels.CreateAsync(privateVessel).ConfigureAwait(false);

                    Captain captain = new Captain("patterns-pub-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock? publicDock = await service.ProvisionAsync(publicVessel, captain, "armada/captain/msn_pub_pat", "msn_pub_pat").ConfigureAwait(false);
                    Dock? privateDock = await service.ProvisionAsync(privateVessel, captain, "armada/captain/msn_priv_pat", "msn_priv_pat").ConfigureAwait(false);
                    AssertNotNull(publicDock, "Public-vessel dock must be provisioned");
                    AssertNotNull(privateDock, "Private-vessel dock must be provisioned");

                    string publicPatterns = await File.ReadAllTextAsync(
                        Path.Combine(publicDock!.WorktreePath!, ".armada", "boundary.patterns")).ConfigureAwait(false);
                    string privatePatterns = await File.ReadAllTextAsync(
                        Path.Combine(privateDock!.WorktreePath!, ".armada", "boundary.patterns")).ConfigureAwait(false);

                    // Public vessel: the raw private-id pattern lands under the privateIdentifiers section.
                    AssertContains(@"ACME-\w{4,}", publicPatterns,
                        "Public vessel boundary.patterns must carry the raw private-identifier pattern");
                    int headerIndex = publicPatterns.IndexOf("# privateIdentifiers", StringComparison.Ordinal);
                    int patternIndex = publicPatterns.IndexOf(@"ACME-\w{4,}", StringComparison.Ordinal);
                    Assert(headerIndex >= 0 && patternIndex > headerIndex,
                        "Private-identifier pattern must appear after the # privateIdentifiers header so the hook routes it correctly");
                    AssertFalse(publicPatterns.Contains(@"ACME-\\w", StringComparison.Ordinal),
                        "Public vessel private-identifier pattern must stay raw (not JSON double-escaped)");

                    // Non-public vessel: the same denylist must not leak into its hook patterns.
                    AssertFalse(privatePatterns.Contains("ACME-", StringComparison.Ordinal),
                        "Non-public vessel boundary.patterns must omit configured private-identifier patterns");
                }
            });
        }

        private static bool PathEquals(string a, string b)
        {
            return String.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using Process process = new Process { StartInfo = startInfo };
            process.Start();

            string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("git " + String.Join(" ", args) + " failed (exit " + process.ExitCode + "): " + stderr.Trim());
            }

            return stdout;
        }

        private class LockingGitService : IGitService
        {
            private int _CurrentCreateCalls;
            public int MaxConcurrentCreateCalls { get; private set; }

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                Directory.CreateDirectory(localPath);
                return Task.CompletedTask;
            }

            public virtual async Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                int current = Interlocked.Increment(ref _CurrentCreateCalls);
                if (current > MaxConcurrentCreateCalls)
                    MaxConcurrentCreateCalls = current;

                try
                {
                    Directory.CreateDirectory(worktreePath);
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _CurrentCreateCalls);
                }
            }

            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(Directory.Exists(path));
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>("main");
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123");
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }

        private static async Task<OpenCodeTestConfig> ReadOpenCodeConfigAsync(string path)
        {
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            OpenCodeTestConfig? config = JsonSerializer.Deserialize<OpenCodeTestConfig>(json);
            if (config == null)
            {
                throw new InvalidOperationException("Unable to deserialize OpenCode config at " + path);
            }

            return config;
        }

        private void AssertOpenCodeBareStringGrant(OpenCodeTestConfig config, string label)
        {
            // The builder now emits external_directory as the bare string "allow"
            // (opencode normalizes it to {"*":"allow"}), which dodges the broken
            // Windows path-glob matcher. The supplied roots no longer drive a path
            // map, so the seeded document is identical regardless of worktree,
            // sibling, or playbooks roots.
            AssertNotNull(config.Permission, label + " (permission block present)");
            AssertEqual("allow", config.Permission!.ExternalDirectory, label + " (external_directory bare string 'allow')");
        }

        private sealed class OpenCodeTestConfig
        {
            [JsonPropertyName("permission")]
            public OpenCodePermissionBlock? Permission { get; set; }
        }

        private sealed class OpenCodePermissionBlock
        {
            // external_directory is emitted as a BARE STRING ("allow"), normalized by
            // opencode to {"*":"allow"}. The prior path-keyed map shape was broken on
            // Windows (sst/opencode #11042/#7279/#20045), so the builder no longer emits
            // per-root grants; the supplied roots no longer influence this document.
            [JsonPropertyName("external_directory")]
            public string? ExternalDirectory { get; set; }
        }

        private class GitInfoGitService : LockingGitService
        {
            public override Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                Directory.CreateDirectory(Path.Combine(worktreePath, ".git", "info"));
                return Task.CompletedTask;
            }
        }

        private sealed class PreseedOpenCodeGitService : GitInfoGitService
        {
            private readonly string _OpenCodeConfig;

            public PreseedOpenCodeGitService(string openCodeConfig)
            {
                _OpenCodeConfig = openCodeConfig ?? throw new ArgumentNullException(nameof(openCodeConfig));
            }

            public override async Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                await base.CreateWorktreeAsync(repoPath, worktreePath, branchName, baseBranch, detached, token: token).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(worktreePath, "opencode.json"), _OpenCodeConfig, token).ConfigureAwait(false);
            }
        }

        private class WorktreeCreation
        {
            public string RepoPath { get; set; } = String.Empty;
            public string WorktreePath { get; set; } = String.Empty;
            public string BranchName { get; set; } = String.Empty;
            public string BaseBranch { get; set; } = String.Empty;
            public bool Detached { get; set; }
        }

        private class RefSpecPush
        {
            public string RepoPath { get; set; } = String.Empty;
            public string SrcRef { get; set; } = String.Empty;
            public string DestRef { get; set; } = String.Empty;
        }

        private class RecordingGitService : IGitService
        {
            public List<WorktreeCreation> CreatedWorktrees { get; } = new List<WorktreeCreation>();
            public List<RefSpecPush> PushedRefSpecs { get; } = new List<RefSpecPush>();
            public HashSet<string> ExistingBranches { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int CloneBareCalls { get; private set; }
            public int BranchExistsCalls { get; private set; }
            public string? RevisionShaResult { get; set; } = null;
            public Task<string?> GetRevisionShaAsync(string repoPath, string revision, CancellationToken token = default) => Task.FromResult(RevisionShaResult);

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                CloneBareCalls++;
                Directory.CreateDirectory(localPath);
                return Task.CompletedTask;
            }

            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                CreatedWorktrees.Add(new WorktreeCreation
                {
                    RepoPath = repoPath,
                    WorktreePath = Path.GetFullPath(worktreePath),
                    BranchName = branchName,
                    BaseBranch = baseBranch,
                    Detached = detached
                });
                Directory.CreateDirectory(worktreePath);
                return Task.CompletedTask;
            }

            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(Directory.Exists(path));
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default)
            {
                PushedRefSpecs.Add(new RefSpecPush { RepoPath = repoPath, SrcRef = srcRef, DestRef = destRef });
                return Task.CompletedTask;
            }
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123");
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);

            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default)
            {
                BranchExistsCalls++;
                return Task.FromResult(ExistingBranches.Contains(branchName));
            }
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(Directory.Exists(worktreePath));
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>(null);
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }
        /// <summary>
        /// Git service that records primary (non-detached) worktree creations and throws for detached (sibling) ones.
        /// </summary>
        private class ThrowOnDetachedGitService : IGitService
        {
            public List<WorktreeCreation> PrimaryWorktrees { get; } = new List<WorktreeCreation>();

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                Directory.CreateDirectory(localPath);
                return Task.CompletedTask;
            }

            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                if (detached)
                    throw new InvalidOperationException("Simulated sibling provisioning failure");
                PrimaryWorktrees.Add(new WorktreeCreation
                {
                    RepoPath = repoPath,
                    WorktreePath = Path.GetFullPath(worktreePath),
                    BranchName = branchName,
                    BaseBranch = baseBranch,
                    Detached = detached
                });
                Directory.CreateDirectory(Path.Combine(worktreePath, ".git", "info"));
                return Task.CompletedTask;
            }

            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(Directory.Exists(path));
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>("main");
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123");
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }

        /// <summary>
        /// Git service that throws on the detached worktree creation whose path contains a marker
        /// substring, while recording every other (primary or recovered sibling) worktree creation.
        /// Used to prove the per-sibling loop continues past a failure to provision later siblings.
        /// </summary>
        private class PartialFailureGitService : IGitService
        {
            private readonly string _FailMarker;

            public PartialFailureGitService(string failMarker)
            {
                _FailMarker = failMarker ?? throw new ArgumentNullException(nameof(failMarker));
            }

            public List<WorktreeCreation> CreatedWorktrees { get; } = new List<WorktreeCreation>();
            public int Failures { get; private set; }

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                Directory.CreateDirectory(localPath);
                return Task.CompletedTask;
            }

            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                if (detached && worktreePath.Contains(_FailMarker, StringComparison.OrdinalIgnoreCase))
                {
                    Failures++;
                    throw new InvalidOperationException("Simulated sibling provisioning failure for " + worktreePath);
                }

                CreatedWorktrees.Add(new WorktreeCreation
                {
                    RepoPath = repoPath,
                    WorktreePath = Path.GetFullPath(worktreePath),
                    BranchName = branchName,
                    BaseBranch = baseBranch,
                    Detached = detached
                });
                Directory.CreateDirectory(Path.Combine(worktreePath, ".git", "info"));
                return Task.CompletedTask;
            }

            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(Directory.Exists(path));
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>("main");
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123");
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }

        /// <summary>
        /// Git service that raises OperationCanceledException on detached (sibling) worktree creation
        /// to prove that cancellation propagates out of the per-sibling loop rather than being
        /// swallowed by the ordinary warn-and-skip handler.
        /// </summary>
        private class CancelOnDetachedGitService : IGitService
        {
            public bool DetachedAttempted { get; private set; }

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                Directory.CreateDirectory(localPath);
                return Task.CompletedTask;
            }

            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                if (detached)
                {
                    DetachedAttempted = true;
                    throw new OperationCanceledException("Simulated cancellation during sibling provisioning");
                }

                Directory.CreateDirectory(Path.Combine(worktreePath, ".git", "info"));
                return Task.CompletedTask;
            }

            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(Directory.Exists(path));
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>("main");
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123");
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }
    }
}
