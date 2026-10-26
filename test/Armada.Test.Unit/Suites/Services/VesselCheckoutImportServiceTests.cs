namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Behavior tests for safe discovery and registration of existing checkouts.</summary>
    public sealed class VesselCheckoutImportServiceTests : TestSuite
    {
        private sealed class TempDirectory : IDisposable
        {
            public string Path { get; }

            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_checkout_import_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public void Dispose()
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, true);
            }
        }

        private sealed class ImportAttempt
        {
            public Vessel? Vessel { get; set; }
            public bool Conflict { get; set; }
        }

        public override string Name => "VesselCheckoutImportService";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Rejects forged scoped and revoked administrators before reading roots", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (TempDirectory temp = new TempDirectory())
                {
                    AuthContext caller = await CreateAdminAsync(testDb).ConfigureAwait(false);
                    ArmadaSettings settings = CreateSettings(temp.Path + "-missing-root");
                    VesselCheckoutImportService service = new VesselCheckoutImportService(testDb.Driver, settings);

                    UserMaster ordinaryUser = await testDb.Driver.Users.CreateAsync(new UserMaster(caller.TenantId!,
                        "ordinary@example.invalid", "test-password")).ConfigureAwait(false);
                    AuthContext forged = new AuthContext
                    {
                        IsAuthenticated = true,
                        IsAdmin = true,
                        TenantId = caller.TenantId,
                        UserId = ordinaryUser.Id
                    };
                    await AssertThrowsAsync<UnauthorizedAccessException>(async () => await service.DiscoverAsync(forged).ConfigureAwait(false));

                    AuthContext mission = CopyAdmin(caller);
                    mission.MissionId = "mission-scope";
                    await AssertThrowsAsync<UnauthorizedAccessException>(async () => await service.DiscoverAsync(mission).ConfigureAwait(false));

                    AuthContext ask = CopyAdmin(caller);
                    ask.AskThreadId = "ask-thread-scope";
                    ask.AskTurnId = "ask-turn-scope";
                    await AssertThrowsAsync<UnauthorizedAccessException>(async () => await service.DiscoverAsync(ask).ConfigureAwait(false));

                    UserMaster user = (await testDb.Driver.Users.ReadByIdAsync(caller.UserId!).ConfigureAwait(false))!;
                    user.Active = false;
                    await testDb.Driver.Users.UpdateAsync(user).ConfigureAwait(false);
                    await AssertThrowsAsync<UnauthorizedAccessException>(async () => await service.DiscoverAsync(caller).ConfigureAwait(false));

                    user.Active = true;
                    await testDb.Driver.Users.UpdateAsync(user).ConfigureAwait(false);
                    TenantMetadata tenant = (await testDb.Driver.Tenants.ReadAsync(caller.TenantId!).ConfigureAwait(false))!;
                    tenant.Active = false;
                    await testDb.Driver.Tenants.UpdateAsync(tenant).ConfigureAwait(false);
                    await AssertThrowsAsync<UnauthorizedAccessException>(async () => await service.DiscoverAsync(caller).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);

            await RunTest("Empty and invalid roots report configuration and refuse import", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (TempDirectory temp = new TempDirectory())
                {
                    AuthContext caller = await CreateAdminAsync(testDb).ConfigureAwait(false);
                    ArmadaSettings settings = new ArmadaSettings();
                    VesselCheckoutImportService service = new VesselCheckoutImportService(testDb.Driver, settings);
                    VesselCheckoutDiscovery empty = await service.DiscoverAsync(caller).ConfigureAwait(false);
                    AssertFalse(empty.Configured, "an empty root list disables discovery");
                    AssertEqual(0, empty.Candidates.Count);
                    await AssertThrowsAsync<InvalidOperationException>(async () => await service.ImportAsync(caller,
                        new VesselCheckoutImportRequest { CheckoutPath = temp.Path, Name = "empty-root" }).ConfigureAwait(false));

                    settings.VesselImport.AllowedRoots.Add(Path.Combine(temp.Path, "missing"));
                    VesselCheckoutDiscovery invalid = await service.DiscoverAsync(caller).ConfigureAwait(false);
                    AssertTrue(invalid.Configured, "a configured but invalid root remains visible as configured");
                    AssertTrue(invalid.Errors.Count > 0, "an invalid root reports a bounded error");
                    AssertEqual(0, invalid.Candidates.Count);
                    await AssertThrowsAsync<ArgumentException>(async () => await service.ImportAsync(caller,
                        new VesselCheckoutImportRequest { CheckoutPath = temp.Path, Name = "invalid-root" }).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);

            await RunTest("Discovers sanitized metadata and imports without changing the checkout", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (TempDirectory temp = new TempDirectory())
                {
                    string checkout = CreateCheckout(temp.Path, "sample-checkout");
                    string remoteSecret = "remote-secret-" + Guid.NewGuid().ToString("N");
                    RunGit(checkout, "remote", "add", "origin", "https://user:" + remoteSecret + "@example.invalid/org/sample.git?token=" + remoteSecret);
                    RunGit(checkout, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/feature/nested");
                    AuthContext caller = await CreateAdminAsync(testDb).ConfigureAwait(false);
                    VesselCheckoutImportService service = new VesselCheckoutImportService(testDb.Driver, CreateSettings(temp.Path));

                    string beforeStatus = RunGit(checkout, "status", "--porcelain");
                    string beforeRefs = RunGit(checkout, "for-each-ref", "--format=%(refname) %(objectname)");
                    string beforeHead = RunGit(checkout, "symbolic-ref", "refs/remotes/origin/HEAD");
                    string filePath = Path.Combine(checkout, "tracked.txt");
                    string beforeFile = File.ReadAllText(filePath);

                    VesselCheckoutDiscovery discovery = await service.DiscoverAsync(caller).ConfigureAwait(false);
                    AssertTrue(discovery.Configured);
                    AssertEqual(1, discovery.Candidates.Count);
                    VesselCheckoutCandidate candidate = discovery.Candidates[0];
                    AssertEqual(Path.GetFullPath(checkout), candidate.CheckoutPath);
                    AssertEqual("sample-checkout", candidate.SuggestedName);
                    AssertEqual("feature/nested", candidate.DefaultBranch, "origin/HEAD keeps the full branch suffix");
                    AssertTrue(candidate.OriginUrl != null, "a non-credential origin can be returned in sanitized form");
                    AssertFalse(candidate.OriginUrl!.Contains(remoteSecret, StringComparison.Ordinal), "the credential is removed from discovery output");
                    AssertFalse(candidate.OriginUrl.Contains("user", StringComparison.Ordinal), "the origin user-info is removed");

                    Vessel created = await service.ImportAsync(caller, new VesselCheckoutImportRequest
                    {
                        CheckoutPath = candidate.CheckoutPath,
                        Name = "imported-checkout",
                        FleetId = String.Empty
                    }).ConfigureAwait(false);
                    AssertEqual(Path.GetFullPath(checkout), created.WorkingDirectory);
                    AssertNull(created.LocalPath, "the imported path must not be treated as an Armada-owned bare repository");
                    AssertEqual(Path.GetFullPath(checkout), created.RepoUrl, "RepoUrl stores the local source path, not credential-bearing origin metadata");
                    AssertEqual("feature/nested", created.DefaultBranch);
                    AssertNull(created.FleetId, "an empty optional fleet ID is stored as null");
                    Vessel? stored = await testDb.Driver.Vessels.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNotNull(stored);
                    AssertNull(stored!.LocalPath);
                    AssertEqual(beforeStatus, RunGit(checkout, "status", "--porcelain"));
                    AssertEqual(beforeRefs, RunGit(checkout, "for-each-ref", "--format=%(refname) %(objectname)"));
                    AssertEqual(beforeHead, RunGit(checkout, "symbolic-ref", "refs/remotes/origin/HEAD"));
                    AssertEqual(beforeFile, File.ReadAllText(filePath));
                    VesselCheckoutDiscovery afterImport = await service.DiscoverAsync(caller).ConfigureAwait(false);
                    AssertEqual(created.Id, afterImport.Candidates[0].ExistingVesselId, "discovery marks the canonical imported checkout");
                    await AssertThrowsAsync<InvalidOperationException>(async () => await service.ImportAsync(caller,
                        new VesselCheckoutImportRequest { CheckoutPath = checkout, Name = "duplicate-checkout" }).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);

            await RunTest("Matches stored symlink aliases when discovering and importing a checkout", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (TempDirectory temp = new TempDirectory())
                {
                    string checkout = CreateCheckout(temp.Path, "physical-checkout");
                    string alias = Path.Combine(temp.Path, "registered-alias");
                    Directory.CreateSymbolicLink(alias, checkout);
                    AuthContext caller = await CreateAdminAsync(testDb).ConfigureAwait(false);
                    Vessel existing = new Vessel("registered-through-alias", alias)
                    {
                        TenantId = caller.TenantId,
                        UserId = caller.UserId,
                        RepoUrl = alias,
                        LocalPath = null,
                        WorkingDirectory = alias
                    };
                    existing = await testDb.Driver.Vessels.CreateAsync(existing).ConfigureAwait(false);
                    VesselCheckoutImportService service = new VesselCheckoutImportService(testDb.Driver, CreateSettings(temp.Path));

                    VesselCheckoutDiscovery discovery = await service.DiscoverAsync(caller).ConfigureAwait(false);
                    AssertEqual(1, discovery.Candidates.Count, "the symlink candidate is excluded while the physical checkout is listed");
                    AssertEqual(Path.GetFullPath(checkout), discovery.Candidates[0].CheckoutPath);
                    AssertEqual(existing.Id, discovery.Candidates[0].ExistingVesselId,
                        "the existing working-directory alias maps to the physical checkout");
                    await AssertThrowsAsync<InvalidOperationException>(async () => await service.ImportAsync(caller,
                        new VesselCheckoutImportRequest { CheckoutPath = checkout, Name = "duplicate-physical-checkout" }).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);

            await RunTest("Rejects nested outside bare and symlink checkout paths", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (TempDirectory temp = new TempDirectory())
                {
                    string checkout = CreateCheckout(temp.Path, "valid-checkout");
                    string nested = Path.Combine(checkout, "nested");
                    Directory.CreateDirectory(nested);
                    using (TempDirectory outsideRoot = new TempDirectory())
                    {
                        string outside = CreateCheckout(outsideRoot.Path, "outside-checkout");
                        string bare = Path.Combine(temp.Path, "bare.git");
                        RunGit(temp.Path, "init", "--bare", bare);
                        AuthContext caller = await CreateAdminAsync(testDb).ConfigureAwait(false);
                        VesselCheckoutImportService service = new VesselCheckoutImportService(testDb.Driver, CreateSettings(temp.Path));

                        await AssertThrowsAsync<ArgumentException>(async () => await service.ImportAsync(caller,
                            new VesselCheckoutImportRequest { CheckoutPath = nested, Name = "nested-checkout" }).ConfigureAwait(false));
                        await AssertThrowsAsync<ArgumentException>(async () => await service.ImportAsync(caller,
                            new VesselCheckoutImportRequest { CheckoutPath = outside, Name = "outside-checkout" }).ConfigureAwait(false));
                        string traversal = Path.Combine(temp.Path, "..", Path.GetFileName(outsideRoot.Path), "outside-checkout");
                        await AssertThrowsAsync<ArgumentException>(async () => await service.ImportAsync(caller,
                            new VesselCheckoutImportRequest { CheckoutPath = traversal, Name = "traversal-checkout" }).ConfigureAwait(false));
                        await AssertThrowsAsync<ArgumentException>(async () => await service.ImportAsync(caller,
                            new VesselCheckoutImportRequest { CheckoutPath = bare, Name = "bare-checkout" }).ConfigureAwait(false));

                        if (!OperatingSystem.IsWindows())
                        {
                            string link = Path.Combine(temp.Path, "linked-checkout");
                            Directory.CreateSymbolicLink(link, checkout);
                            await AssertThrowsAsync<ArgumentException>(async () => await service.ImportAsync(caller,
                                new VesselCheckoutImportRequest { CheckoutPath = link, Name = "linked-checkout" }).ConfigureAwait(false));

                            using (TempDirectory linkHost = new TempDirectory())
                            {
                                string rootLink = Path.Combine(linkHost.Path, "root-link");
                                Directory.CreateSymbolicLink(rootLink, temp.Path);
                                ArmadaSettings linkedRootSettings = CreateSettings(rootLink);
                                VesselCheckoutImportService linkedRootService = new VesselCheckoutImportService(testDb.Driver, linkedRootSettings);
                                VesselCheckoutDiscovery linkedRootDiscovery = await linkedRootService.DiscoverAsync(caller).ConfigureAwait(false);
                                AssertTrue(linkedRootDiscovery.Errors.Count > 0, "symlink roots are reported and not traversed");
                                AssertEqual(0, linkedRootDiscovery.Candidates.Count);
                            }
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Concurrent imports through service instances create one row", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (TempDirectory temp = new TempDirectory())
                {
                    string checkout = CreateCheckout(temp.Path, "concurrent-checkout");
                    AuthContext caller = await CreateAdminAsync(testDb).ConfigureAwait(false);
                    ArmadaSettings settings = CreateSettings(temp.Path);
                    VesselCheckoutImportService firstService = new VesselCheckoutImportService(testDb.Driver, settings);
                    VesselCheckoutImportService secondService = new VesselCheckoutImportService(testDb.Driver, settings);
                    VesselCheckoutImportRequest firstRequest = new VesselCheckoutImportRequest { CheckoutPath = checkout, Name = "concurrent-import" };
                    VesselCheckoutImportRequest secondRequest = new VesselCheckoutImportRequest { CheckoutPath = checkout, Name = "concurrent-import" };

                    Task<ImportAttempt> first = CaptureImportAsync(firstService, caller, firstRequest);
                    Task<ImportAttempt> second = CaptureImportAsync(secondService, caller, secondRequest);
                    ImportAttempt[] attempts = await Task.WhenAll(first, second).ConfigureAwait(false);
                    int createdCount = 0;
                    int conflictCount = 0;
                    foreach (ImportAttempt attempt in attempts)
                    {
                        if (attempt.Vessel != null) createdCount++;
                        if (attempt.Conflict) conflictCount++;
                    }
                    AssertEqual(1, createdCount, "one concurrent request creates the row");
                    AssertEqual(1, conflictCount, "the other request reports the duplicate");
                    List<Vessel> vessels = await testDb.Driver.Vessels.EnumerateAsync().ConfigureAwait(false);
                    AssertEqual(1, vessels.Count(vessel => String.Equals(vessel.WorkingDirectory, Path.GetFullPath(checkout), StringComparison.Ordinal)));
                }
            }).ConfigureAwait(false);

            await RunTest("Hot reload copies configured checkout roots into the live settings object", () =>
            {
                ArmadaSettings live = new ArmadaSettings();
                VesselImportSettings retained = live.VesselImport;
                ArmadaSettings source = new ArmadaSettings();
                source.VesselImport.AllowedRoots.Add(Path.Combine(Path.GetTempPath(), "checkout-root"));
                live.ApplyHotReloadableFrom(source);
                AssertTrue(ReferenceEquals(retained, live.VesselImport), "hot reload preserves the nested object reference");
                AssertEqual(1, live.VesselImport.AllowedRoots.Count);
                AssertEqual(source.VesselImport.AllowedRoots[0], live.VesselImport.AllowedRoots[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        private static async Task<AuthContext> CreateAdminAsync(TestDatabase testDb)
        {
            TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("Checkout import tenant")).ConfigureAwait(false);
            UserMaster user = new UserMaster(tenant.Id, "checkout-admin@example.invalid", "test-password") { IsAdmin = true };
            user = await testDb.Driver.Users.CreateAsync(user).ConfigureAwait(false);
            return new AuthContext
            {
                IsAuthenticated = true,
                TenantId = tenant.Id,
                UserId = user.Id,
                IsAdmin = true
            };
        }

        private static AuthContext CopyAdmin(AuthContext caller)
        {
            return new AuthContext
            {
                IsAuthenticated = caller.IsAuthenticated,
                TenantId = caller.TenantId,
                UserId = caller.UserId,
                IsAdmin = caller.IsAdmin,
                IsTenantAdmin = caller.IsTenantAdmin
            };
        }

        private static ArmadaSettings CreateSettings(string allowedRoot)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.VesselImport.AllowedRoots.Add(allowedRoot);
            return settings;
        }

        private static string CreateCheckout(string root, string name)
        {
            string checkout = Path.Combine(root, name);
            Directory.CreateDirectory(checkout);
            RunGit(checkout, "init", "-b", "main");
            RunGit(checkout, "config", "user.name", "Checkout Import Tests");
            RunGit(checkout, "config", "user.email", "checkout-import@example.invalid");
            File.WriteAllText(Path.Combine(checkout, "tracked.txt"), "checkout content");
            RunGit(checkout, "add", "tracked.txt");
            RunGit(checkout, "commit", "-m", "initial");
            return checkout;
        }

        private static string RunGit(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            using (Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git did not start."))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(15000))
                {
                    process.Kill(true);
                    throw new InvalidOperationException("Git fixture command timed out.");
                }
                if (process.ExitCode != 0) throw new InvalidOperationException("Git fixture command failed: " + error);
                return output.Trim();
            }
        }

        private static async Task<ImportAttempt> CaptureImportAsync(
            VesselCheckoutImportService service,
            AuthContext caller,
            VesselCheckoutImportRequest request)
        {
            try
            {
                return new ImportAttempt { Vessel = await service.ImportAsync(caller, request).ConfigureAwait(false) };
            }
            catch (InvalidOperationException)
            {
                return new ImportAttempt { Conflict = true };
            }
        }
    }
}
