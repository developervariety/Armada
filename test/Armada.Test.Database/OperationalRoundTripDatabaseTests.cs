#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Provider-backed round trips for method sets that carry plain records: every property is written with a
    /// non-default value, read back through the same driver and through a freshly opened driver, and compared
    /// value by value, with every timestamp required to read back as the same UTC instant.
    /// </summary>
    internal sealed class OperationalRoundTripDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal OperationalRoundTripDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifySkillsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? skillId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("skill-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "skill-user", token: token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                Skill skill = new Skill
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Name = "round-trip-skill-" + suffix,
                    Description = "Skill description ユニコード",
                    Category = "category-" + suffix,
                    Content = "Skill content 内容\nsecond line",
                    IsBuiltIn = true,
                    Active = false,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                Skill created = await _Driver.Skills.CreateAsync(skill, token).ConfigureAwait(false);
                skillId = created.Id;
                SkillQuery scope = new SkillQuery { TenantId = tenant.Id, UserId = user.Id };

                DatabaseAssert.AllProperties(created, await _Driver.Skills.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Skill");
                DatabaseAssert.True(await _Driver.Skills.ReadAsync(created.Id, new SkillQuery { TenantId = "ten_other_" + suffix }, token).ConfigureAwait(false) == null,
                    "Another tenant cannot read the skill");

                created.Description = "Updated skill description";
                created.Category = null;
                created.Content = "Updated content";
                created.IsBuiltIn = false;
                created.Active = true;
                Skill updated = await _Driver.Skills.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Skills.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Reopened Skill");
                }

                SkillQuery window = new SkillQuery
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    FromUtc = updated.CreatedUtc.AddMinutes(-1),
                    ToUtc = updated.CreatedUtc.AddMinutes(1),
                    PageNumber = 1,
                    PageSize = 10
                };
                EnumerationResult<Skill> page = await _Driver.Skills.EnumerateAsync(window, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, page.TotalRecords, "Skill creation-time window counts the skill");
                DatabaseAssert.AllProperties(updated, page.Objects[0], "Enumerated Skill");
                window.FromUtc = updated.CreatedUtc.AddMinutes(1);
                window.ToUtc = updated.CreatedUtc.AddMinutes(2);
                DatabaseAssert.Equal(0, (await _Driver.Skills.EnumerateAllAsync(window, token).ConfigureAwait(false)).Count, "Skill creation-time window excludes a later range");

                await _Driver.Skills.DeleteAsync(created.Id, scope, token).ConfigureAwait(false);
                skillId = null;
                DatabaseAssert.True(await _Driver.Skills.ReadAsync(created.Id, scope, token).ConfigureAwait(false) == null, "Deleted skill is gone");
            }
            finally
            {
                if (skillId != null && !_NoCleanup) await _Driver.Skills.DeleteAsync(skillId, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyProjectProfilesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? profileId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("profile-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "profile-user", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "profile-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "profile-vessel", token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                ProjectProfile profile = new ProjectProfile
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Name = "round-trip-profile-" + suffix,
                    Description = "Profile description ユニコード",
                    Scope = ProjectProfileScopeEnum.Vessel,
                    FleetId = fleet.Id,
                    VesselId = vessel.Id,
                    IsDefault = true,
                    Active = false,
                    DefaultPipelineId = "ppl_" + suffix,
                    WorkflowProfileId = "wfp_" + suffix,
                    PersonaOverrides = new List<PersonaOverride>
                    {
                        new PersonaOverride { PersonaName = "Worker", PromptTemplateName = "persona.worker", AdditionalInstructions = "Keep changes small.", Enabled = false }
                    },
                    Skills = new List<string> { "skill-a-" + suffix, "skill-b-" + suffix },
                    AuthorizationPolicy = "{\"allow\":[\"read\"]}",
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                ProjectProfile created = await _Driver.ProjectProfiles.CreateAsync(profile, token).ConfigureAwait(false);
                profileId = created.Id;
                ProjectProfileQuery scope = new ProjectProfileQuery { TenantId = tenant.Id, UserId = user.Id };

                DatabaseAssert.AllProperties(created, await _Driver.ProjectProfiles.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "ProjectProfile");
                DatabaseAssert.True(await _Driver.ProjectProfiles.ReadAsync(created.Id, new ProjectProfileQuery { TenantId = "ten_other_" + suffix }, token).ConfigureAwait(false) == null,
                    "Another tenant cannot read the project profile");

                created.Description = null;
                created.Scope = ProjectProfileScopeEnum.Fleet;
                created.VesselId = null;
                created.IsDefault = false;
                created.Active = true;
                created.PersonaOverrides = new List<PersonaOverride>();
                created.Skills = new List<string> { "skill-c-" + suffix };
                created.AuthorizationPolicy = null;
                ProjectProfile updated = await _Driver.ProjectProfiles.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.ProjectProfiles.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Reopened ProjectProfile");
                }

                ProjectProfileQuery window = new ProjectProfileQuery
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    FleetId = fleet.Id,
                    FromUtc = updated.CreatedUtc.AddMinutes(-1),
                    ToUtc = updated.CreatedUtc.AddMinutes(1),
                    PageNumber = 1,
                    PageSize = 10
                };
                EnumerationResult<ProjectProfile> page = await _Driver.ProjectProfiles.EnumerateAsync(window, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, page.TotalRecords, "Project profile creation-time window counts the profile");
                DatabaseAssert.AllProperties(updated, page.Objects[0], "Enumerated ProjectProfile");
                window.FromUtc = updated.CreatedUtc.AddMinutes(1);
                window.ToUtc = updated.CreatedUtc.AddMinutes(2);
                DatabaseAssert.Equal(0, (await _Driver.ProjectProfiles.EnumerateAllAsync(window, token).ConfigureAwait(false)).Count, "Project profile creation-time window excludes a later range");

                await _Driver.ProjectProfiles.DeleteAsync(created.Id, scope, token).ConfigureAwait(false);
                profileId = null;
                DatabaseAssert.True(await _Driver.ProjectProfiles.ReadAsync(created.Id, scope, token).ConfigureAwait(false) == null, "Deleted project profile is gone");
            }
            finally
            {
                if (profileId != null && !_NoCleanup) await _Driver.ProjectProfiles.DeleteAsync(profileId, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }
        internal async Task VerifyPlaybookEmptyTextAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? playbookId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("playbook-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "playbook-user", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "playbook-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "playbook-vessel", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "playbook-captain", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "playbook-voyage", token).ConfigureAwait(false);
                Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "playbook-mission", token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);

                Playbook playbook = new Playbook
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    FileName = "round-trip-" + suffix + ".md",
                    Description = "Playbook description ユニコード",
                    Content = "# Playbook\nBody 内容",
                    Active = false
                };
                Playbook created = await _Driver.Playbooks.CreateAsync(playbook, token).ConfigureAwait(false);
                playbookId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Playbooks.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "Playbook");

                // Every provider reads an empty optional text column as null, so a caller never has to tell
                // an empty description from a missing one.
                created.Description = "";
                await _Driver.Playbooks.UpdateAsync(created, token).ConfigureAwait(false);
                Playbook emptied = DatabaseAssert.NotNull(await _Driver.Playbooks.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "Playbook with empty description");
                DatabaseAssert.True(emptied.Description == null, "Empty playbook description reads as null");

                MissionPlaybookSnapshot snapshot = new MissionPlaybookSnapshot
                {
                    PlaybookId = created.Id,
                    FileName = created.FileName,
                    Description = "",
                    Content = "Snapshot content",
                    DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent,
                    ResolvedPath = "",
                    WorktreeRelativePath = "",
                    SourceLastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                };
                await _Driver.Playbooks.SetMissionSnapshotsAsync(mission.Id, new List<MissionPlaybookSnapshot> { snapshot }, token).ConfigureAwait(false);
                List<MissionPlaybookSnapshot> snapshots = await _Driver.Playbooks.GetMissionSnapshotsAsync(mission.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, snapshots.Count, "Mission playbook snapshot count");
                DatabaseAssert.True(snapshots[0].Description == null, "Empty snapshot description reads as null");
                DatabaseAssert.True(snapshots[0].ResolvedPath == null, "Empty snapshot resolved path reads as null");
                DatabaseAssert.True(snapshots[0].WorktreeRelativePath == null, "Empty snapshot worktree path reads as null");
                DatabaseAssert.Equal(created.Id, snapshots[0].PlaybookId, "Snapshot playbook id");
                DatabaseAssert.UtcInstant(snapshot.SourceLastUpdateUtc, snapshots[0].SourceLastUpdateUtc, "Snapshot SourceLastUpdateUtc");
                await _Driver.Playbooks.SetMissionSnapshotsAsync(mission.Id, new List<MissionPlaybookSnapshot>(), token).ConfigureAwait(false);
            }
            finally
            {
                if (playbookId != null && !_NoCleanup) await _Driver.Playbooks.DeleteAsync(playbookId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }
        internal async Task VerifyLandingJobsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? mergeEntryId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("landing-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "landing-user", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "landing-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "landing-vessel", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "landing-captain", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "landing-voyage", token).ConfigureAwait(false);
                Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "landing-mission", token).ConfigureAwait(false);
                MergeEntry entry = await fixture.CreateMergeEntryAsync(tenant.Id, user.Id, mission.Id, vessel.Id, token).ConfigureAwait(false);
                mergeEntryId = entry.Id;

                LandingJob job = new LandingJob
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    MergeEntryId = entry.Id,
                    MissionId = mission.Id,
                    VesselId = vessel.Id,
                    BranchName = "armada/landing-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    TargetBranch = "release/next",
                    State = LandingJobStateEnum.Testing,
                    RetryCount = 2,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-4),
                    StartedUtc = DateTime.UtcNow.AddMinutes(-3),
                    LastError = "Previous attempt failed ユニコード"
                };
                LandingJob created = await _Driver.LandingJobs.CreateAsync(job, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(created, await _Driver.LandingJobs.ReadAsync(created.Id, token).ConfigureAwait(false), "LandingJob");
                DatabaseAssert.AllProperties(created, await _Driver.LandingJobs.ReadByMergeEntryAsync(entry.Id, token).ConfigureAwait(false), "LandingJob by merge entry");
                DatabaseAssert.ContainsIds(await _Driver.LandingJobs.EnumerateByStateAsync(LandingJobStateEnum.Testing, token).ConfigureAwait(false), item => item.Id, created.Id);

                created.State = LandingJobStateEnum.Landed;
                created.RetryCount = 3;
                created.CompletedUtc = DateTime.UtcNow;
                created.LastError = null;
                LandingJob updated = await _Driver.LandingJobs.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.LandingJobs.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened LandingJob");
                }

                List<LandingJob> testing = await _Driver.LandingJobs.EnumerateByStateAsync(LandingJobStateEnum.Testing, token).ConfigureAwait(false);
                DatabaseAssert.True(testing.TrueForAll(item => item.Id != created.Id), "A landed job leaves the testing state list");

                await _Driver.LandingJobs.DeleteByMergeEntryAsync(entry.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.LandingJobs.ReadAsync(created.Id, token).ConfigureAwait(false) == null, "Deleted landing job is gone");
            }
            finally
            {
                if (mergeEntryId != null && !_NoCleanup) await _Driver.LandingJobs.DeleteByMergeEntryAsync(mergeEntryId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyJudgeFollowUpsAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string vesselId = "vsl_follow_" + suffix;
            JudgeFollowUp? stored = null;
            try
            {
                JudgeFollowUp followUp = new JudgeFollowUp
                {
                    TenantId = "ten_follow_" + suffix,
                    UserId = "usr_follow_" + suffix,
                    JudgeMissionId = "msn_judge_" + suffix,
                    ReviewedMissionId = "msn_reviewed_" + suffix,
                    VoyageId = "vyg_follow_" + suffix,
                    VesselId = vesselId,
                    JudgeVerdict = "PASS_WITH_NOTES",
                    SuggestedFollowUps = "- Split the helper ユニコード",
                    AuditVerdict = "Pending",
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-2)
                };
                stored = await _Driver.JudgeFollowUps.UpsertAsync(followUp, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(followUp, stored, "JudgeFollowUp");
                DatabaseAssert.AllProperties(followUp, await _Driver.JudgeFollowUps.ReadAsync(stored.Id, token).ConfigureAwait(false), "JudgeFollowUp by id");
                DatabaseAssert.AllProperties(followUp, await _Driver.JudgeFollowUps.ReadByJudgeMissionAsync(followUp.JudgeMissionId, token).ConfigureAwait(false), "JudgeFollowUp by Judge mission");
                DatabaseAssert.Equal(1, (await _Driver.JudgeFollowUps.EnumeratePendingAsync(vesselId, token).ConfigureAwait(false)).Count, "Pending follow-up for the vessel");
                DatabaseAssert.Equal(1, (await _Driver.JudgeFollowUps.EnumerateUnassociatedAsync(vesselId, token).ConfigureAwait(false)).Count, "Unassociated follow-up for the vessel");
                DatabaseAssert.Equal(1, (await _Driver.JudgeFollowUps.EnumerateUnassociatedByReviewedMissionAsync(followUp.ReviewedMissionId, token).ConfigureAwait(false)).Count,
                    "Unassociated follow-up by reviewed mission");
                DatabaseAssert.Equal(1, (await _Driver.JudgeFollowUps.EnumerateUnassociatedByJudgeMissionAsync(followUp.JudgeMissionId, token).ConfigureAwait(false)).Count,
                    "Unassociated follow-up by Judge mission");

                string mergeEntryId = "mrg_follow_" + suffix;
                DatabaseAssert.True(await _Driver.JudgeFollowUps.TryAssociateAsync(stored.Id, mergeEntryId, token).ConfigureAwait(false), "First association wins");
                DatabaseAssert.True(!await _Driver.JudgeFollowUps.TryAssociateAsync(stored.Id, "mrg_other_" + suffix, token).ConfigureAwait(false), "A second association is refused");
                JudgeFollowUp associated = DatabaseAssert.NotNull(await _Driver.JudgeFollowUps.ReadByMergeEntryAsync(mergeEntryId, token).ConfigureAwait(false), "Follow-up by merge entry");
                DatabaseAssert.Equal(stored.Id, associated.Id, "Associated follow-up id");
                DatabaseAssert.Equal(0, (await _Driver.JudgeFollowUps.EnumerateUnassociatedAsync(vesselId, token).ConfigureAwait(false)).Count, "An associated follow-up is no longer unassociated");

                DateTime completedUtc = DateTime.UtcNow.AddSeconds(-5);
                JudgeFollowUp audited = await _Driver.JudgeFollowUps.CompleteAuditAsync(stored.Id, "Actionable", "Audit notes 内容", "Open an objective", completedUtc, token).ConfigureAwait(false);
                DatabaseAssert.Equal("Actionable", audited.AuditVerdict, "Audit verdict");
                DatabaseAssert.Equal("Audit notes 内容", audited.AuditNotes, "Audit notes");
                DatabaseAssert.Equal("Open an objective", audited.AuditRecommendedAction, "Audit recommended action");
                DatabaseAssert.UtcInstant(completedUtc, audited.AuditCompletedUtc, "JudgeFollowUp.AuditCompletedUtc");
                DatabaseAssert.Equal(mergeEntryId, audited.MergeEntryId, "Completing an audit keeps the association");
                DatabaseAssert.Equal(0, (await _Driver.JudgeFollowUps.EnumeratePendingAsync(vesselId, token).ConfigureAwait(false)).Count, "An audited follow-up is no longer pending");

                audited.SuggestedFollowUps = "- Updated suggestion";
                audited.AuditRecommendedAction = null;
                JudgeFollowUp updated = await _Driver.JudgeFollowUps.UpdateAsync(audited, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.JudgeFollowUps.ReadAsync(stored.Id, token).ConfigureAwait(false), "Reopened JudgeFollowUp");
                }
            }
            finally
            {
                // The follow-up method set has no delete; remove the fixture row directly.
                if (stored != null && !_NoCleanup)
                {
                    using (System.Data.Common.DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        using (System.Data.Common.DbCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "DELETE FROM judge_follow_ups WHERE id = @id;";
                            System.Data.Common.DbParameter parameter = command.CreateParameter();
                            parameter.ParameterName = "@id";
                            parameter.Value = stored.Id;
                            command.Parameters.Add(parameter);
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
    }
}
