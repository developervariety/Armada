#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Selected-provider permission transactions and the completion-only snapshot predicate.</summary>
    internal sealed class MissionAutomaticWorkPermissionDatabaseTests
    {
        private const string _EventType = "mission.automatic_work_permission_changed";
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal MissionAutomaticWorkPermissionDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyPermissionTransactionsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            List<string> auditIds = new List<string>();
            try
            {
                if (_Settings.Type == DatabaseTypeEnum.SqlServer)
                {
                    using (SqlCommand command = new SqlCommand())
                    {
                        foreach (string? value in new string?[] { null, "permission Unicode Ω" })
                        {
                            command.Parameters.Clear();
                            StoredValueBinder.For(DatabaseTypeEnum.SqlServer).For(command, "missions")
                                .Text("@permission", "automatic_work_permission_json", value);
                            SqlParameter parameter = (SqlParameter)command.Parameters["@permission"];
                            DatabaseAssert.Equal(SqlDbType.NVarChar, parameter.SqlDbType,
                                "The actual SqlClient permission binder uses Unicode for both null and nonnull raw JSON");
                        }
                    }
                }

                Mission created = await CreateMissionAsync(fixture, "permission-transaction", token).ConfigureAwait(false);
                DatabaseAssert.True(created.AutomaticWorkPermission == null && !created.AutomaticWorkPermissionInvalid,
                    "A fresh mission starts in legacy mode on the selected provider");
                Mission enabled = await WriteAsync(created, new MissionAutomaticWorkPermission(), "Enable", auditIds, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, enabled.AutomaticWorkPermissionWriteRevision, "Enable revision");
                DatabaseAssert.True(enabled.AutomaticWorkPermission!.Verification == null && enabled.AutomaticWorkPermission.DependentAdmission == null,
                    "Enable grants neither action");

                MissionAutomaticWorkGrant verification = Grant(enabled);
                Mission verified = await WriteAsync(enabled, new MissionAutomaticWorkPermission { Verification = verification }, "Grant verification Ω", auditIds, token).ConfigureAwait(false);
                MissionAutomaticWorkGrant dependent = Grant(verified);
                Mission both = await WriteAsync(verified, new MissionAutomaticWorkPermission { Verification = verification, DependentAdmission = dependent },
                    "Grant dependent", auditIds, token).ConfigureAwait(false);
                Mission stale = await ReadAsync(_Driver, created.Id, token).ConfigureAwait(false);
                Mission revoked = await WriteAsync(both, new MissionAutomaticWorkPermission { DependentAdmission = dependent }, "Revoke verification", auditIds, token).ConfigureAwait(false);
                DatabaseAssert.True(revoked.AutomaticWorkPermission!.Verification == null, "Selected grant is revoked");
                DatabaseAssert.Equal(JsonSerializer.Serialize(dependent), JsonSerializer.Serialize(revoked.AutomaticWorkPermission.DependentAdmission),
                    "Other grant is unchanged");
                DatabaseAssert.Equal(4L, revoked.AutomaticWorkPermissionWriteRevision, "Four committed permission operations");
                await AssertAuditsAsync(revoked, auditIds.Count, token).ConfigureAwait(false);

                ArmadaEvent staleAudit = Audit(stale, "Stale grant must not restore verification");
                DatabaseAssert.True(!await _Driver.Missions.TryWriteAutomaticWorkPermissionAsync(stale,
                    new MissionAutomaticWorkPermission { Verification = verification, DependentAdmission = dependent }, staleAudit, token).ConfigureAwait(false),
                    "A stale dedicated writer cannot restore a revoked grant");
                DatabaseAssert.True(await _Driver.Events.ReadAsync(staleAudit.Id, token).ConfigureAwait(false) == null,
                    "A rejected permission CAS inserts no audit");
                stale.Title = "A stale ordinary writer must not win";
                bool staleRefused = false;
                try { await _Driver.Missions.UpdateAsync(stale, token).ConfigureAwait(false); }
                catch (InvalidOperationException ex)
                { staleRefused = ex.Message.StartsWith("automatic_work_permission_stale_mission_write", StringComparison.Ordinal); }
                DatabaseAssert.True(staleRefused, "Ordinary stale-write protection stays in force");
                await AssertSameAsync(revoked, token).ConfigureAwait(false);

                // The next permission UPDATE can succeed, but its duplicate audit INSERT must fail.
                // The real provider transaction must roll BOTH changes back; no provider-specific trigger is used.
                ArmadaEvent duplicateAudit = Audit(revoked, "Rollback an unauditable revoke");
                duplicateAudit.Id = auditIds[0];
                bool insertFailed = false;
                try
                {
                    await _Driver.Missions.TryWriteAutomaticWorkPermissionAsync(revoked,
                        new MissionAutomaticWorkPermission(), duplicateAudit, token).ConfigureAwait(false);
                }
                catch (DbException) { insertFailed = true; }
                DatabaseAssert.True(insertFailed, "Duplicate audit primary key must reach the real provider and fail");
                await AssertSameAsync(revoked, token).ConfigureAwait(false);
                await AssertAuditsAsync(revoked, 4, token).ConfigureAwait(false);

                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Mission restored = await ReadAsync(reopened, revoked.Id, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(revoked.AutomaticWorkPermissionRawJson, restored.AutomaticWorkPermissionRawJson, "Exact raw grant JSON after reopen");
                    DatabaseAssert.Equal(revoked.AutomaticWorkPermissionWriteRevision, restored.AutomaticWorkPermissionWriteRevision, "Grant revision after reopen");
                    Mission summary = DatabaseAssert.NotNull(await reopened.Missions.ReadSummaryAsync(revoked.Id, token).ConfigureAwait(false), "Permission summary");
                    DatabaseAssert.Equal(revoked.AutomaticWorkPermissionRawJson, summary.AutomaticWorkPermissionRawJson, "Summary reads keep the permission snapshot");
                    Mission cleared = await WriteAsync(restored, new MissionAutomaticWorkPermission(), "Revoke dependent", auditIds, token).ConfigureAwait(false);
                    DatabaseAssert.True(cleared.AutomaticWorkPermission!.Verification == null && cleared.AutomaticWorkPermission.DependentAdmission == null,
                        "Both grants can be revoked without opting out of the barrier");
                    await AssertAuditsAsync(cleared, 5, token).ConfigureAwait(false);
                }
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (string id in auditIds) await _Driver.Events.DeleteAsync(id, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyCompletionSnapshotAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            List<string> auditIds = new List<string>();
            try
            {
                Mission created = await CreateMissionAsync(fixture, "permission-completion", token).ConfigureAwait(false);
                Mission enabled = await WriteAsync(created, new MissionAutomaticWorkPermission(), "Enable", auditIds, token).ConfigureAwait(false);
                Mission oldExpected = await ReadAsync(_Driver, created.Id, token).ConfigureAwait(false);
                Mission oldDesired = await ReadAsync(_Driver, created.Id, token).ConfigureAwait(false);
                oldDesired.Status = MissionStatusEnum.Review;
                Mission granted = await WriteAsync(enabled, new MissionAutomaticWorkPermission { Verification = Grant(enabled) }, "Grant during completion", auditIds, token).ConfigureAwait(false);
                DatabaseAssert.True(!await _Driver.Missions.TryUpdateCompletionAsync(oldDesired, oldExpected, token).ConfigureAwait(false),
                    "The completion primitive refuses stale permission raw/revision");
                await AssertSameAsync(granted, token).ConfigureAwait(false);

                Mission currentExpected = await ReadAsync(_Driver, created.Id, token).ConfigureAwait(false);
                Mission desired = await ReadAsync(_Driver, created.Id, token).ConfigureAwait(false);
                desired.Status = MissionStatusEnum.Review;
                DateTime completedWrite = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234561);
                DateTime reviewWrite = new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc).AddTicks(7654329);
                desired.CompletedUtc = completedWrite;
                desired.ReviewRequestedUtc = reviewWrite;
                string expectedBefore = JsonSerializer.Serialize(currentExpected);
                DatabaseAssert.True(await _Driver.Missions.TryUpdateCompletionAsync(desired, currentExpected, token).ConfigureAwait(false),
                    "The fresh permission snapshot can preserve the verdict through the real completion CAS");
                Mission review = await ReadAsync(_Driver, created.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(MissionStatusEnum.Review, review.Status, "Stored verdict");
                bool nativeMicroseconds = _Settings.Type == DatabaseTypeEnum.Postgresql || _Settings.Type == DatabaseTypeEnum.Mysql;
                DateTime expectedCompleted = nativeMicroseconds ? completedWrite.AddTicks(-1) : completedWrite;
                DateTime expectedReview = nativeMicroseconds ? reviewWrite.AddTicks(-9) : reviewWrite;
                DatabaseAssert.Equal(expectedCompleted, desired.CompletedUtc!.Value, "Completion sends the exact representable timestamp");
                DatabaseAssert.Equal(expectedReview, desired.ReviewRequestedUtc!.Value, "Review sends the exact representable timestamp");
                DatabaseAssert.Equal(desired.CompletedUtc, review.CompletedUtc, "Stored completion matches the working write exactly");
                DatabaseAssert.Equal(desired.ReviewRequestedUtc, review.ReviewRequestedUtc, "Stored review matches the working write exactly");
                DatabaseAssert.Equal(expectedBefore, JsonSerializer.Serialize(currentExpected), "Timestamp normalization never changes the expected snapshot");
                DatabaseAssert.Equal(currentExpected.StartedUtc, review.StartedUtc, "The exact launch timestamp is preserved");
                DatabaseAssert.Equal(currentExpected.CreatedUtc, review.CreatedUtc, "The creation timestamp is preserved");
                DatabaseAssert.Equal(granted.AutomaticWorkPermissionRawJson, review.AutomaticWorkPermissionRawJson, "Completion never overwrites permissions");
                DatabaseAssert.Equal(granted.AutomaticWorkPermissionWriteRevision, review.AutomaticWorkPermissionWriteRevision, "Completion never increments the permission revision");
                DatabaseAssert.Equal(currentExpected.AdmissionWriteRevision + 1, review.AdmissionWriteRevision, "Only the exact owned admission increment occurs");

                foreach (string change in new[] { "source", "start", "cancel", "ordinary" })
                {
                    Mission before = await CreateMissionAsync(fixture, "completion-refuse-" + change, token).ConfigureAwait(false);
                    Mission expected = await ReadAsync(_Driver, before.Id, token).ConfigureAwait(false);
                    Mission intended = await ReadAsync(_Driver, before.Id, token).ConfigureAwait(false);
                    intended.Status = MissionStatusEnum.Review;
                    Mission replacement = await ReadAsync(_Driver, before.Id, token).ConfigureAwait(false);
                    if (change == "source") replacement.CommitHash = new string('b', 40);
                    else if (change == "start") replacement.StartedUtc = replacement.StartedUtc!.Value.AddSeconds(1);
                    else if (change == "cancel") { replacement.Status = MissionStatusEnum.Cancelled; replacement.FailureReason = "Fixture cancellation must survive"; }
                    else replacement.Title = "A concurrent ordinary change must survive";
                    await _Driver.Missions.UpdateAsync(replacement, token).ConfigureAwait(false);
                    Mission protectedRow = await ReadAsync(_Driver, before.Id, token).ConfigureAwait(false);
                    DatabaseAssert.True(!await _Driver.Missions.TryUpdateCompletionAsync(intended, expected, token).ConfigureAwait(false),
                        "Completion refuses changed " + change + " without overwriting it");
                    await AssertSameAsync(protectedRow, token).ConfigureAwait(false);
                }

                Mission capture = await CreateMissionAsync(fixture, "completion-capture", token).ConfigureAwait(false);
                Mission captureExpected = await ReadAsync(_Driver, capture.Id, token).ConfigureAwait(false);
                Mission captureDesired = await ReadAsync(_Driver, capture.Id, token).ConfigureAwait(false);
                captureDesired.CommitHash = new string('c', 40);
                captureDesired.DiffSnapshot = "Actual captured source delta";
                DatabaseAssert.True(await _Driver.Missions.TryUpdateCompletionAsync(captureDesired, captureExpected, token).ConfigureAwait(false),
                    "Captured HEAD advances against the OLD expected commit rather than the mutated desired one");
                Mission captured = await ReadAsync(_Driver, capture.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(captureDesired.CommitHash, captured.CommitHash, "Captured source is stored");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Mission restored = await ReadAsync(reopened, created.Id, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(MissionStatusEnum.Review, restored.Status, "Completion verdict survives reopen");
                    DatabaseAssert.Equal(review.AutomaticWorkPermissionRawJson, restored.AutomaticWorkPermissionRawJson, "Completion keeps exact permission JSON across reopen");
                }
            }
            finally
            {
                if (!_NoCleanup)
                    foreach (string id in auditIds) await _Driver.Events.DeleteAsync(id, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task<Mission> CreateMissionAsync(DatabaseFixture fixture, string name, CancellationToken token)
        {
            TenantMetadata tenant = await fixture.CreateTenantAsync(name, token: token).ConfigureAwait(false);
            UserMaster user = await fixture.CreateUserAsync(tenant.Id, name, token: token).ConfigureAwait(false);
            Mission created = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, null!, null!, name, token,
                startedUtc: DateTime.UtcNow.AddMinutes(-5), configure: mission =>
                {
                    mission.Persona = "Worker"; mission.Status = MissionStatusEnum.WorkProduced;
                    mission.CommitHash = new string('a', 40); mission.RequiresReview = true;
                    mission.RecoveryAttempts = 2; mission.LandingRetryCount = 1;
                }).ConfigureAwait(false);
            return await ReadAsync(_Driver, created.Id, token).ConfigureAwait(false);
        }

        private async Task<Mission> WriteAsync(Mission expected, MissionAutomaticWorkPermission next, string label,
            List<string> auditIds, CancellationToken token)
        {
            ArmadaEvent audit = Audit(expected, label);
            DatabaseAssert.True(await _Driver.Missions.TryWriteAutomaticWorkPermissionAsync(expected, next, audit, token).ConfigureAwait(false), label + " commits permission and audit");
            auditIds.Add(audit.Id);
            Mission fresh = await ReadAsync(_Driver, expected.Id, token).ConfigureAwait(false);
            DatabaseAssert.Equal(expected.AutomaticWorkPermissionWriteRevision + 1, fresh.AutomaticWorkPermissionWriteRevision, label + " revision");
            return fresh;
        }

        private async Task AssertSameAsync(Mission expected, CancellationToken token)
        {
            Mission current = await ReadAsync(_Driver, expected.Id, token).ConfigureAwait(false);
            DatabaseAssert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(current), "A rejected write preserves ordinary fields and permission projection");
            DatabaseAssert.Equal(expected.AutomaticWorkPermissionRawJson, current.AutomaticWorkPermissionRawJson, "Rejected write exact raw JSON");
            DatabaseAssert.Equal(expected.AutomaticWorkPermissionWriteRevision, current.AutomaticWorkPermissionWriteRevision, "Rejected write permission revision");
            DatabaseAssert.Equal(expected.AdmissionWriteRevision, current.AdmissionWriteRevision, "Rejected write admission revision");
        }

        private async Task AssertAuditsAsync(Mission mission, int count, CancellationToken token)
        {
            List<ArmadaEvent> audits = (await _Driver.Events.EnumerateByMissionAsync(mission.Id, 100, token).ConfigureAwait(false))
                .Where(item => item.EventType == _EventType).ToList();
            DatabaseAssert.Equal(count, audits.Count, "Atomic audit row count");
            foreach (ArmadaEvent audit in audits)
            {
                DatabaseAssert.Equal(mission.Id, audit.MissionId, "Audit mission scope");
                DatabaseAssert.Equal(mission.Id, audit.EntityId, "Audit entity scope");
                DatabaseAssert.Equal(mission.TenantId, audit.TenantId, "Audit tenant scope");
                DatabaseAssert.Equal(mission.UserId, audit.UserId, "Audit user scope");
                DatabaseAssert.True(!String.IsNullOrWhiteSpace(audit.Payload), "Fixture issuer/reason payload persists");
            }
        }

        private static ArmadaEvent Audit(Mission mission, string operation) => new ArmadaEvent(_EventType, "Provider permission transaction")
        {
            TenantId = mission.TenantId, UserId = mission.UserId, MissionId = mission.Id,
            EntityType = "mission", EntityId = mission.Id, VesselId = mission.VesselId, VoyageId = mission.VoyageId,
            Payload = JsonSerializer.Serialize(new { operation, actorUserId = mission.UserId, reason = "Provider fixture reason Ω" })
        };

        private static MissionAutomaticWorkGrant Grant(Mission mission) => new MissionAutomaticWorkGrant
        {
            MissionId = mission.Id, TenantId = mission.TenantId, UserId = mission.UserId,
            StartedUtc = mission.StartedUtc!.Value, CommitHash = mission.CommitHash!,
            GrantedByUserId = mission.UserId!, GrantedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(10)
        };

        private static async Task<Mission> ReadAsync(DatabaseDriver driver, string id, CancellationToken token) =>
            DatabaseAssert.NotNull(await driver.Missions.ReadAsync(id, token).ConfigureAwait(false), "Provider fixture mission");
    }
}
