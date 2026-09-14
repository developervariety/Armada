namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Provider-backed proof that the terminal-voyage mission repair reads WorkProduced summaries, merge
    /// entries and voyages correctly, persists the terminal status and reason, and is idempotent.
    /// </summary>
    internal sealed class TerminalVoyageReconciliationDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal TerminalVoyageReconciliationDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string root = Path.Combine(Path.GetTempPath(), "armada_tvm_db_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            List<string> eventIds = new List<string>();
            try
            {
                string source = Path.Combine(root, "source");
                string bare = Path.Combine(root, "vessel.git");
                Directory.CreateDirectory(source);
                await GitAsync(source, "init", "-b", "main").ConfigureAwait(false);
                await GitAsync(source, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await GitAsync(source, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                await CommitAsync(source, "base.txt").ConfigureAwait(false);
                await GitAsync(source, "checkout", "-b", "work/landed").ConfigureAwait(false);
                await CommitAsync(source, "landed.txt").ConfigureAwait(false);
                string landedSha = await GitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false);
                await GitAsync(source, "checkout", "main").ConfigureAwait(false);
                await GitAsync(source, "merge", "--no-ff", "-m", "land", "work/landed").ConfigureAwait(false);
                await GitAsync(source, "checkout", "-b", "work/unlanded", "main").ConfigureAwait(false);
                await CommitAsync(source, "unlanded.txt").ConfigureAwait(false);
                string unlandedSha = await GitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false);
                await GitAsync(root, "clone", "--bare", source, bare).ConfigureAwait(false);

                TenantMetadata tenant = await fixture.CreateTenantAsync("terminal-voyage", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "terminal-voyage", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "terminal-voyage", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "terminal-voyage", token,
                    item => item.LocalPath = bare).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "terminal-voyage", token).ConfigureAwait(false);
                DateTime ended = DateTime.UtcNow.AddHours(-2);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "terminal-voyage", token, item =>
                {
                    item.Status = VoyageStatusEnum.Failed;
                    item.CompletedUtc = ended;
                }).ConfigureAwait(false);

                Mission landed = await CreateProducedAsync(fixture, tenant, user, voyage, vessel, captain, landedSha, token).ConfigureAwait(false);
                Mission unlanded = await CreateProducedAsync(fixture, tenant, user, voyage, vessel, captain, unlandedSha, token).ConfigureAwait(false);
                Mission inFlight = await CreateProducedAsync(fixture, tenant, user, voyage, vessel, captain, unlandedSha, token).ConfigureAwait(false);
                await fixture.CreateMergeEntryAsync(tenant.Id, user.Id, inFlight.Id, vessel.Id, token).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                TerminalVoyageMissionReconciler reconciler = new TerminalVoyageMissionReconciler(logging, _Driver, new GitService(logging));

                TerminalVoyageMissionReconciliationResult dry = await reconciler.ReconcileAsync(Request(vessel, true), token).ConfigureAwait(false);
                DatabaseAssert.Equal(3, dry.Examined, "Dry run examines every WorkProduced mission under the ended voyage");
                DatabaseAssert.Equal(1, dry.Completed, "Dry run completes landed work");
                DatabaseAssert.Equal(1, dry.Failed, "Dry run fails unlanded work");
                DatabaseAssert.Equal(1, dry.Kept, "Dry run keeps the mission with a queued landing");
                DatabaseAssert.Equal(MissionStatusEnum.WorkProduced, (await _Driver.Missions.ReadAsync(landed.Id, token).ConfigureAwait(false))!.Status, "Dry run writes nothing");

                TerminalVoyageMissionReconciliationResult applied = await reconciler.ReconcileAsync(Request(vessel, false), token).ConfigureAwait(false);
                DatabaseAssert.Equal(dry.Completed, applied.Completed, "Apply matches the dry run");
                DatabaseAssert.Equal(dry.Failed, applied.Failed, "Apply matches the dry run");

                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Mission landedAfter = DatabaseAssert.NotNull(await reopened.Missions.ReadAsync(landed.Id, token).ConfigureAwait(false), "Landed mission");
                    DatabaseAssert.Equal(MissionStatusEnum.Complete, landedAfter.Status, "Landed work is Complete after reopen");
                    DatabaseAssert.Equal(landedSha, landedAfter.CommitHash, "Commit evidence survives");
                    Mission unlandedAfter = DatabaseAssert.NotNull(await reopened.Missions.ReadAsync(unlanded.Id, token).ConfigureAwait(false), "Unlanded mission");
                    DatabaseAssert.Equal(MissionStatusEnum.Failed, unlandedAfter.Status, "Unlanded work is Failed after reopen");
                    DatabaseAssert.True((unlandedAfter.FailureReason ?? String.Empty).Contains(TerminalVoyageMissionRule.ReasonWorkUnlanded, StringComparison.Ordinal),
                        "The reason survives reopen");
                    DatabaseAssert.True(unlandedAfter.CompletedUtc.HasValue, "Completion time is recorded");
                    DatabaseAssert.Equal(MissionStatusEnum.WorkProduced,
                        DatabaseAssert.NotNull(await reopened.Missions.ReadAsync(inFlight.Id, token).ConfigureAwait(false), "In-flight mission").Status,
                        "A queued landing keeps the mission WorkProduced");

                    EnumerationResult<ArmadaEvent> events = await reopened.Events.EnumerateAsync(
                        new EnumerationQuery { PageNumber = 1, PageSize = 1000, EventType = TerminalVoyageMissionReconciler.EventType }, token).ConfigureAwait(false);
                    List<ArmadaEvent> ours = events.Objects
                        .Where(item => item.MissionId == landed.Id || item.MissionId == unlanded.Id || item.MissionId == inFlight.Id)
                        .ToList();
                    eventIds.AddRange(ours.Select(item => item.Id));
                    DatabaseAssert.Equal(2, ours.Count, "One event per changed mission");
                }

                TerminalVoyageMissionReconciliationResult again = await reconciler.ReconcileAsync(Request(vessel, false), token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, again.Examined, "A repeated pass finds only the kept mission");
                DatabaseAssert.Equal(0, again.Completed + again.Failed + again.Cancelled, "A repeated pass changes nothing");
            }
            finally
            {
                foreach (string id in eventIds)
                {
                    await _Driver.Events.DeleteAsync(id, token).ConfigureAwait(false);
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("could not remove test directory " + root + ": " + ex.Message);
                }
            }
        }

        private static TerminalVoyageMissionReconciliationRequest Request(Vessel vessel, bool dryRun)
        {
            return new TerminalVoyageMissionReconciliationRequest
            {
                DryRun = dryRun,
                IncludeHistorical = true,
                VesselId = vessel.Id
            };
        }

        private static Task<Mission> CreateProducedAsync(
            DatabaseFixture fixture,
            TenantMetadata tenant,
            UserMaster user,
            Voyage voyage,
            Vessel vessel,
            Captain captain,
            string commit,
            CancellationToken token)
        {
            return fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "terminal-voyage", token, configure: item =>
            {
                item.Status = MissionStatusEnum.WorkProduced;
                item.Persona = "Worker";
                item.CommitHash = commit;
            });
        }

        private static async Task CommitAsync(string dir, string file)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, file), file + "\n").ConfigureAwait(false);
            await GitAsync(dir, "add", file).ConfigureAwait(false);
            await GitAsync(dir, "commit", "-m", file).ConfigureAwait(false);
        }

        private static async Task<string> GitAsync(string dir, params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using (Process process = Process.Start(psi)!)
            {
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr);
                return stdout.Trim();
            }
        }
    }
}
