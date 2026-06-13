namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.Linq;
    using System.Text.Json;
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
                    AssertContains("command = \"armada\"", codexMcp, "Codex MCP config should launch Armada over stdio");
                    AssertContains("args = [\"mcp\", \"stdio\"]", codexMcp, "Codex MCP config should use the stdio MCP command");
                    AssertContains("startup_timeout_sec = 120", codexMcp, "Codex MCP config should allow Armada MCP to start");
                    AssertContains("mcp_servers.armada", codexMcp, "Codex MCP config should name the Armada server");
                    string geminiMcp = await File.ReadAllTextAsync(geminiMcpPath).ConfigureAwait(false);
                    AssertContains("localhost:" + settings.McpPort, geminiMcp, "Gemini MCP config should point at Armada MCP");
                    AssertContains("\"armada\"", geminiMcp, "Gemini MCP config should name the Armada server");

                    string metadataPath = Path.Combine(settings.LogDirectory, "docks", dock!.Id + ".start");
                    AssertTrue(File.Exists(metadataPath), "Dock provisioning should persist the start commit metadata");
                    AssertEqual("abc123", (await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false)).Trim(), "Metadata should store the provisioned HEAD commit");

                    await service.ReclaimAsync(dock.Id).ConfigureAwait(false);
                    AssertFalse(File.Exists(metadataPath), "Dock reclaim should remove the start commit metadata");
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

                    await service.ReclaimAsync(dock.Id).ConfigureAwait(false);
                    AssertFalse(Directory.Exists(expectedSibA), "Reclaim should tear down provisioned sibling worktrees");
                    AssertFalse(Directory.Exists(expectedSibB), "Reclaim should tear down nested sibling worktrees");
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

                    string artifactSourceRoot = Path.Combine(Path.GetTempPath(), "armada_test_jpro_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        // Arrange -- create source artifact tree that simulates the JPRO extraction output
                        string vinCatalogDir = Path.Combine(artifactSourceRoot, "output", "jpro-export", "vin-catalog");
                        string meddutyDir = Path.Combine(artifactSourceRoot, "output", "jpro-export", "medduty-kwp");
                        Directory.CreateDirectory(vinCatalogDir);
                        Directory.CreateDirectory(meddutyDir);
                        await File.WriteAllTextAsync(Path.Combine(vinCatalogDir, "catalog.json"), "{\"v\":1}").ConfigureAwait(false);
                        await File.WriteAllTextAsync(Path.Combine(meddutyDir, "data.bin"), "binary").ConfigureAwait(false);

                        // Register the sibling (JproDeobfuscator) vessel with a WorkingDirectory pointing at our temp tree
                        Vessel siblingVessel = new Vessel("JproDeobfuscator", "https://github.com/test/jpro.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "JproDeobfuscator.git");
                        siblingVessel.WorkingDirectory = artifactSourceRoot;
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Id,
                                RepoUrl = "https://github.com/test/jpro.git",
                                RelativePath = "../JproDeobfuscator",
                                BranchStrategy = SiblingBranchStrategyEnum.MatchBranchElseDefault,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "jpro-export") }
                            }
                        };

                        Vessel vessel = new Vessel("otrbuddy", "https://github.com/test/otrbuddy.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "otrbuddy.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("captain-jpro");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        // Act
                        Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-jpro/msn_art", "msn_art").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock should be provisioned");

                        // Assert -- artifacts copied into the sibling worktree at the expected relative path
                        string siblingWorktree = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../JproDeobfuscator"));
                        string expectedCatalog = Path.Combine(siblingWorktree, "output", "jpro-export", "vin-catalog", "catalog.json");
                        string expectedMedduty = Path.Combine(siblingWorktree, "output", "jpro-export", "medduty-kwp", "data.bin");
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
                    string hostWorkDir = Path.Combine(Path.GetTempPath(), "armada_test_jpro_absent_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Directory.CreateDirectory(hostWorkDir);

                        Vessel siblingVessel = new Vessel("JproDeobfuscatorAbsent", "https://github.com/test/jpro.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "JproDeobfuscatorAbsent.git");
                        siblingVessel.WorkingDirectory = hostWorkDir;
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Id,
                                RepoUrl = "https://github.com/test/jpro.git",
                                RelativePath = "../JproDeobfuscatorAbsent",
                                BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "jpro-export") }
                            }
                        };

                        Vessel vessel = new Vessel("otrbuddy-absent", "https://github.com/test/otrbuddy.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "otrbuddy-absent.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("captain-absent");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        // Act -- should not throw; absent source is a clean skip
                        Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-absent/msn_abs", "msn_abs").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock should be provisioned even when artifact source is absent");

                        string siblingWorktree = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../JproDeobfuscatorAbsent"));
                        string absentDest = Path.Combine(siblingWorktree, "output", "jpro-export");
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

                    string artifactSourceRoot = Path.Combine(Path.GetTempPath(), "armada_test_jpro_name_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        string nestedFaultDir = Path.Combine(artifactSourceRoot, "output", "jpro-export", "fault-descriptions", "nested");
                        Directory.CreateDirectory(nestedFaultDir);
                        string sourceFile = Path.Combine(nestedFaultDir, "fault.json");
                        await File.WriteAllTextAsync(sourceFile, "{\"fault\":123}").ConfigureAwait(false);

                        Vessel siblingVessel = new Vessel("JproDeobfuscatorByName", "https://github.com/test/jpro-name.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "JproDeobfuscatorByName.git");
                        siblingVessel.WorkingDirectory = artifactSourceRoot;
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Name,
                                RepoUrl = "https://github.com/test/jpro-name.git",
                                RelativePath = "../JproDeobfuscatorByName",
                                BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "jpro-export") }
                            }
                        };

                        Vessel vessel = new Vessel("otrbuddy-name-ref", "https://github.com/test/otrbuddy.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "otrbuddy-name-ref.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("captain-name-ref");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-name-ref/msn_art", "msn_art").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock should be provisioned");

                        string siblingWorktree = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../JproDeobfuscatorByName"));
                        string copiedFile = Path.Combine(siblingWorktree, "output", "jpro-export", "fault-descriptions", "nested", "fault.json");
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
                        Vessel siblingVessel = new Vessel("JproDeobfuscatorNoWorkDir", "https://github.com/test/jpro-noworkdir.git");
                        siblingVessel.LocalPath = Path.Combine(settings.ReposDirectory, "JproDeobfuscatorNoWorkDir.git");
                        siblingVessel = await testDb.Driver.Vessels.CreateAsync(siblingVessel).ConfigureAwait(false);

                        List<SiblingRepo> siblings = new List<SiblingRepo>
                        {
                            new SiblingRepo
                            {
                                VesselRef = siblingVessel.Id,
                                RepoUrl = "https://github.com/test/jpro-noworkdir.git",
                                RelativePath = "../JproDeobfuscatorNoWorkDir",
                                BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                                DefaultBranch = "main",
                                ExtractionArtifactPaths = new List<string> { Path.Combine("output", "jpro-export") }
                            }
                        };

                        Vessel vessel = new Vessel("otrbuddy-noworkdir", "https://github.com/test/otrbuddy.git");
                        vessel.LocalPath = Path.Combine(settings.ReposDirectory, "otrbuddy-noworkdir.git");
                        vessel.SiblingRepos = JsonSerializer.Serialize(siblings);
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("captain-noworkdir");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        RecordingGitService git = new RecordingGitService();
                        DockService service = new DockService(logging, testDb.Driver, settings, git);

                        Dock? dock = await service.ProvisionAsync(vessel, captain, "armada/captain-noworkdir/msn_art", "msn_art").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock should be provisioned even when sibling WorkingDirectory is missing");

                        string siblingWorktree = Path.GetFullPath(Path.Combine(dock!.WorktreePath!, "../JproDeobfuscatorNoWorkDir"));
                        string artifactDest = Path.Combine(siblingWorktree, "output", "jpro-export");
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

            public async Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", CancellationToken token = default)
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
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }

        private class WorktreeCreation
        {
            public string RepoPath { get; set; } = String.Empty;
            public string WorktreePath { get; set; } = String.Empty;
            public string BranchName { get; set; } = String.Empty;
            public string BaseBranch { get; set; } = String.Empty;
        }

        private class RecordingGitService : IGitService
        {
            public List<WorktreeCreation> CreatedWorktrees { get; } = new List<WorktreeCreation>();
            public HashSet<string> ExistingBranches { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int CloneBareCalls { get; private set; }
            public int BranchExistsCalls { get; private set; }

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                CloneBareCalls++;
                Directory.CreateDirectory(localPath);
                return Task.CompletedTask;
            }

            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", CancellationToken token = default)
            {
                CreatedWorktrees.Add(new WorktreeCreation
                {
                    RepoPath = repoPath,
                    WorktreePath = Path.GetFullPath(worktreePath),
                    BranchName = branchName,
                    BaseBranch = baseBranch
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
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
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
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }
    }
}
