#nullable enable
namespace Armada.Test.Database
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;

    // A committed completion must reach its review and captain-release owners.
    internal sealed class MissionCompletionTimestampServiceDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly bool _NoCleanup;
        internal MissionCompletionTimestampServiceDatabaseTests(DatabaseDriver driver, bool noCleanup)
        { _Driver = driver; _NoCleanup = noCleanup; }

        internal async Task VerifyReviewTailAsync(CancellationToken token)
        {
            // The real owner schedules existing Pending work after completion. This test requires
            // an isolated runner database and refuses that unrelated side effect before setup.
            DatabaseAssert.Equal(0, (await _Driver.Missions.EnumerateByStatusAsync(MissionStatusEnum.Pending, token)
                .ConfigureAwait(false)).Count, "Service completion fixture requires no unrelated Pending work");
            for (int attempt = 0; attempt < 4; attempt++)
                await CompleteOneAsync(attempt, token).ConfigureAwait(false);
        }

        private async Task CompleteOneAsync(int attempt, CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string root = Path.Combine(Path.GetTempPath(), "armada_completion_timestamp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            Mission? mission = null;
            Captain? captain = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("completion-time", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "completion-time", token: token).ConfigureAwait(false);
                captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "completion-time", token,
                    configure: value => value.State = CaptainStateEnum.Working).ConfigureAwait(false);
                mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, null!, captain.Id,
                    "completion-time", token, startedUtc: DateTime.UtcNow.AddMinutes(-5), configure: value =>
                    {
                        value.Status = MissionStatusEnum.InProgress;
                        value.Persona = "Worker";
                        value.RequiresReview = true;
                        value.CommitHash = new string('a', 40);
                        value.DiffSnapshot = "diff --git a/Behavior.cs b/Behavior.cs\n--- a/Behavior.cs\n+++ b/Behavior.cs\n@@ -1 +1 @@\n-old\n+new\n";
                    }).ConfigureAwait(false);
                mission = DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false), "Captured mission");
                ArmadaEvent audit = new ArmadaEvent("mission.automatic_work_permission_changed", "Enable completion timestamp fixture")
                {
                    TenantId = tenant.Id, UserId = user.Id, MissionId = mission.Id,
                    EntityType = "mission", EntityId = mission.Id,
                    Payload = JsonSerializer.Serialize(new { operation = "Enable", actorUserId = user.Id, reason = "Selected-provider service regression" })
                };
                DatabaseAssert.True(await _Driver.Missions.TryWriteAutomaticWorkPermissionAsync(mission,
                    new MissionAutomaticWorkPermission
                    {
                        Verification = new MissionAutomaticWorkGrant
                        {
                            MissionId = mission.Id, TenantId = tenant.Id, UserId = user.Id,
                            StartedUtc = mission.StartedUtc!.Value, CommitHash = mission.CommitHash!,
                            GrantedByUserId = user.Id, GrantedUtc = DateTime.UtcNow,
                            ExpiresUtc = DateTime.UtcNow.AddMinutes(10)
                        }
                    }, audit, token).ConfigureAwait(false), "Enable real completion CAS owner with a bound verification grant");
                captain.CurrentMissionId = mission.Id;
                await _Driver.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    DataDirectory = root, LogDirectory = Path.Combine(root, "logs"),
                    DocksDirectory = Path.Combine(root, "docks"), ReposDirectory = Path.Combine(root, "repos")
                };
                // Concrete owners; no Git command, worktree, gate, mock, clock override or timestamp callback.
                GitService git = new GitService(logging);
                DockService docks = new DockService(logging, _Driver, settings, git);
                CaptainService captains = new CaptainService(logging, _Driver, settings, git, docks);
                MissionService service = new MissionService(logging, _Driver, settings, docks, captains, git: git);
                int reviews = 0, outcomes = 0;
                bool landing = true;
                service.OnGetMissionOutput = _ => "[ARMADA:RESULT] COMPLETE\nImplemented the requested behavior.\n" + new string('x', 1200);
                service.OnReviewRequested = value => { if (value.Id == mission.Id) reviews++; };
                service.OnMissionOutcome = (value, shouldLand) =>
                { if (value.Id == mission.Id) { outcomes++; landing = shouldLand; } return Task.CompletedTask; };
                await service.HandleCompletionAsync(captain, mission.Id, token).ConfigureAwait(false);

                Mission saved = DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false), "Completed row");
                Captain released = DatabaseAssert.NotNull(await _Driver.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false), "Completion captain");
                var events = await _Driver.Events.EnumerateByMissionAsync(mission.Id, 100, token).ConfigureAwait(false);
                Console.WriteLine("COMPLETION_TIMESTAMP_OBSERVATION " + JsonSerializer.Serialize(new
                {
                    attempt, missionId = mission.Id, saved.Status, saved.CompletedUtc, saved.ReviewRequestedUtc,
                    completedTickRemainder = saved.CompletedUtc?.Ticks % 10,
                    reviewTickRemainder = saved.ReviewRequestedUtc?.Ticks % 10,
                    reviews, outcomes, landing, captainState = released.State, released.CurrentMissionId,
                    outcomeEvents = events.Count(value => value.EventType == "mission.work_produced")
                }));
                DatabaseAssert.Equal(MissionStatusEnum.Review, saved.Status, "The real owner commits Review");
                DatabaseAssert.True(saved.CompletedUtc.HasValue && saved.ReviewRequestedUtc.HasValue, "The real owner generates terminal timestamps");
                DatabaseAssert.Equal(1, reviews, "A committed Review reaches its callback");
                DatabaseAssert.Equal(1, outcomes, "A committed Review reaches the outcome callback");
                DatabaseAssert.True(!landing, "Review does not request landing");
                DatabaseAssert.Equal(1, events.Count(value => value.EventType == "mission.work_produced"), "Normal Review tail emits the existing work-produced event");
                DatabaseAssert.Equal(CaptainStateEnum.Idle, released.State, "The real owner releases its captain");
                DatabaseAssert.True(released.CurrentMissionId == null, "The released captain no longer owns the mission");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    if (mission != null)
                        foreach (ArmadaEvent value in await _Driver.Events.EnumerateByMissionAsync(mission.Id, 100, token).ConfigureAwait(false))
                            await _Driver.Events.DeleteAsync(value.Id, token).ConfigureAwait(false);
                    if (captain != null)
                        foreach (Signal value in await _Driver.Signals.EnumerateRecentAsync(1000, token).ConfigureAwait(false))
                            if (value.FromCaptainId == captain.Id) await _Driver.Signals.DeleteAsync(value.Id, token).ConfigureAwait(false);
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
                logging.Dispose();
                if (!_NoCleanup) Directory.Delete(root, recursive: true);
            }
        }
    }
}
