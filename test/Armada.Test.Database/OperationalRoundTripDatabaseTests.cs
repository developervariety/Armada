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

                MissionPlaybookSnapshot full = new MissionPlaybookSnapshot
                {
                    PlaybookId = created.Id,
                    FileName = created.FileName,
                    Description = "Snapshot description ユニコード",
                    Content = "Snapshot content 内容",
                    DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree,
                    ResolvedPath = "playbooks/" + created.FileName,
                    WorktreeRelativePath = ".armada/playbooks/" + created.FileName,
                    SourceLastUpdateUtc = DateTime.UtcNow.AddMinutes(-2)
                };
                await _Driver.Playbooks.SetMissionSnapshotsAsync(mission.Id, new List<MissionPlaybookSnapshot> { full }, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    List<MissionPlaybookSnapshot> reread = await reopened.Playbooks.GetMissionSnapshotsAsync(mission.Id, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(1, reread.Count, "Reopened mission playbook snapshot count");
                    DatabaseAssert.AllProperties(full, reread[0], "Reopened MissionPlaybookSnapshot");
                }
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

                // Every provider reads an empty optional text column as null, as it does for every other record.
                updated.SuggestedFollowUps = "";
                updated.AuditNotes = "";
                updated.AuditRecommendedAction = "";
                updated.VoyageId = "";
                await _Driver.JudgeFollowUps.UpdateAsync(updated, token).ConfigureAwait(false);
                JudgeFollowUp emptied = DatabaseAssert.NotNull(await _Driver.JudgeFollowUps.ReadAsync(stored.Id, token).ConfigureAwait(false), "Follow-up with empty text");
                DatabaseAssert.True(emptied.SuggestedFollowUps == null, "Empty suggested follow-ups read as null");
                DatabaseAssert.True(emptied.AuditNotes == null, "Empty audit notes read as null");
                DatabaseAssert.True(emptied.AuditRecommendedAction == null, "Empty recommended action reads as null");
                DatabaseAssert.True(emptied.VoyageId == null, "Empty voyage id reads as null");
            }
            finally
            {
                // The follow-up method set has no delete; remove the fixture row directly.
                if (stored != null && !_NoCleanup)
                    await ExecuteForIdAsync("DELETE FROM judge_follow_ups WHERE id = @id;", stored.Id, token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyTenantsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? tenantId = null;
            try
            {
                TenantMetadata tenant = new TenantMetadata("round-trip-tenant-" + Guid.NewGuid().ToString("N").Substring(0, 12) + " ユニコード")
                {
                    Active = false,
                    IsProtected = true,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-4)
                };
                TenantMetadata created = await _Driver.Tenants.CreateAsync(tenant, token).ConfigureAwait(false);
                tenantId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Tenants.ReadAsync(created.Id, token).ConfigureAwait(false), "Tenant");
                DatabaseAssert.AllProperties(created, await _Driver.Tenants.ReadByNameAsync(created.Name, token).ConfigureAwait(false), "Tenant by name");

                created.Name = created.Name + " updated";
                created.Active = true;
                created.IsProtected = false;
                TenantMetadata updated = await _Driver.Tenants.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Tenants.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Tenant");
                }
            }
            finally
            {
                if (tenantId != null && !_NoCleanup) await _Driver.Tenants.DeleteAsync(tenantId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyUsersAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? userId = null;
            string? tenantId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("user-round-trip", token: token).ConfigureAwait(false);
                tenantId = tenant.Id;
                UserMaster user = new UserMaster(tenant.Id, "round-trip-" + Guid.NewGuid().ToString("N").Substring(0, 12) + "@example.com", "secret ユニコード")
                {
                    FirstName = "Given ユニコード",
                    LastName = "Family",
                    IsAdmin = true,
                    IsTenantAdmin = true,
                    IsProtected = true,
                    Active = false,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-4)
                };
                UserMaster created = await _Driver.Users.CreateAsync(user, token).ConfigureAwait(false);
                userId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Users.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "User");
                DatabaseAssert.AllProperties(created, await _Driver.Users.ReadByEmailAsync(tenant.Id, created.Email, token).ConfigureAwait(false), "User by email");

                created.FirstName = null;
                created.LastName = null;
                created.IsAdmin = false;
                created.IsTenantAdmin = false;
                created.IsProtected = false;
                created.Active = true;
                UserMaster updated = await _Driver.Users.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Users.ReadByIdAsync(created.Id, token).ConfigureAwait(false), "Reopened User");
                }
            }
            finally
            {
                if (userId != null && tenantId != null && !_NoCleanup) await _Driver.Users.DeleteAsync(tenantId, userId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyCredentialsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? credentialId = null;
            string? tenantId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("credential-round-trip", token: token).ConfigureAwait(false);
                tenantId = tenant.Id;
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "credential-round-trip", token: token).ConfigureAwait(false);
                Credential credential = new Credential(tenant.Id, user.Id)
                {
                    Name = "Round-trip credential ユニコード",
                    Active = false,
                    IsProtected = true,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-4)
                };
                Credential created = await _Driver.Credentials.CreateAsync(credential, token).ConfigureAwait(false);
                credentialId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Credentials.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "Credential");
                DatabaseAssert.AllProperties(created, await _Driver.Credentials.ReadByBearerTokenAsync(created.BearerToken, token).ConfigureAwait(false), "Credential by bearer token");

                created.Name = null;
                created.Active = true;
                created.IsProtected = false;
                Credential updated = await _Driver.Credentials.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Credentials.ReadByIdAsync(created.Id, token).ConfigureAwait(false), "Reopened Credential");
                }
            }
            finally
            {
                if (credentialId != null && tenantId != null && !_NoCleanup) await _Driver.Credentials.DeleteAsync(tenantId, credentialId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyFleetsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? fleetId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("fleet-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "fleet-round-trip", token: token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                Fleet fleet = new Fleet("round-trip-fleet-" + suffix)
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Description = "Fleet description ユニコード",
                    DefaultPipelineId = "ppl_" + suffix,
                    DefaultPlaybooks = "[{\"playbookId\":\"pbk_" + suffix + "\",\"deliveryMode\":\"InstructionWithReference\"}]",
                    Active = false,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-4)
                };
                Fleet created = await _Driver.Fleets.CreateAsync(fleet, token).ConfigureAwait(false);
                fleetId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Fleets.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "Fleet");
                DatabaseAssert.AllProperties(created, await _Driver.Fleets.ReadByNameAsync(created.Name, token).ConfigureAwait(false), "Fleet by name");

                created.Description = null;
                created.DefaultPipelineId = null;
                created.DefaultPlaybooks = null;
                created.Active = true;
                Fleet updated = await _Driver.Fleets.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Fleets.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Fleet");
                }
            }
            finally
            {
                if (fleetId != null && !_NoCleanup) await _Driver.Fleets.DeleteAsync(fleetId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifySignalsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? signalId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("signal-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "signal-round-trip", token: token).ConfigureAwait(false);
                Captain from = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "signal-from", token).ConfigureAwait(false);
                Captain to = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "signal-to", token).ConfigureAwait(false);
                Signal signal = new Signal(SignalTypeEnum.Completion, "{\"text\":\"Signal payload ユニコード\"}")
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    FromCaptainId = from.Id,
                    ToCaptainId = to.Id,
                    Read = false,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                Signal created = await _Driver.Signals.CreateAsync(signal, token).ConfigureAwait(false);
                signalId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Signals.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "Signal");

                await _Driver.Signals.MarkReadAsync(created.Id, token).ConfigureAwait(false);
                created.Read = true;
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(created, await reopened.Signals.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Signal");
                }
            }
            finally
            {
                if (signalId != null && !_NoCleanup) await _Driver.Signals.DeleteAsync(signalId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyEventsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? eventId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("event-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "event-round-trip", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "event-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "event-vessel", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "event-captain", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "event-voyage", token).ConfigureAwait(false);
                Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "event-mission", token).ConfigureAwait(false);
                ArmadaEvent evt = new ArmadaEvent("mission.round_trip", "Event message ユニコード")
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    EntityType = "mission",
                    EntityId = mission.Id,
                    CaptainId = captain.Id,
                    MissionId = mission.Id,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Payload = "{\"kind\":\"round-trip\"}",
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                ArmadaEvent created = await _Driver.Events.CreateAsync(evt, token).ConfigureAwait(false);
                eventId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Events.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "Event");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(created, await reopened.Events.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Event");
                }
            }
            finally
            {
                if (eventId != null && !_NoCleanup) await _Driver.Events.DeleteAsync(eventId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyCaptainsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? captainId = null;
            string? endpointId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("captain-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "captain-round-trip", token: token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                ModelEndpoint endpoint = await _Driver.ModelEndpoints.CreateAsync(new ModelEndpoint
                {
                    Id = "mep_captain_" + suffix,
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Name = "Captain endpoint " + suffix,
                    Kind = ModelEndpointKindEnum.Inference,
                    Scope = ScopeEnum.UserSpecific,
                    Provider = ModelProviderEnum.OpenAICompatible,
                    BaseUrl = "http://localhost:9999/v1",
                    Model = "captain-model"
                }, token).ConfigureAwait(false);
                endpointId = endpoint.Id;

                // Sub-second digits and a date far from the host's daylight-saving rules make a host-offset read visible.
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                Captain captain = new Captain("round-trip-captain-" + suffix + "-ユニコード", AgentRuntimeEnum.ApiEndpoint)
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Model = "model ユニコード",
                    ModelEndpointId = endpoint.Id,
                    ApiKey = "api-key-" + suffix,
                    ApiBaseUrl = "https://provider.example.test/v1",
                    SystemInstructions = "System instructions ユニコード",
                    AllowedPersonas = "[\"Worker\",\"Judge\"]",
                    PreferredPersona = "Judge",
                    RuntimeOptionsJson = "{\"reasoning\":\"high\"}",
                    Tier = CaptainTierEnum.Economy,
                    PreferenceRank = -7,
                    State = CaptainStateEnum.Stalled,
                    CurrentMissionId = "msn_captain_" + suffix,
                    CurrentDockId = "dck_captain_" + suffix,
                    ProcessId = 424242,
                    ProcessStartedUtc = baseUtc.AddMinutes(-3),
                    RecoveryAttempts = 3,
                    LastHeartbeatUtc = baseUtc.AddMinutes(-2),
                    QuarantineUntilUtc = baseUtc.AddHours(6),
                    QuarantineReason = "Quarantine reason ユニコード",
                    DefaultPlaybooks = "[{\"playbookId\":\"pbk_" + suffix + "\",\"deliveryMode\":\"InstructionWithReference\"}]",
                    CreatedUtc = baseUtc.AddMinutes(-5)
                };
                Captain created = await _Driver.Captains.CreateAsync(captain, token).ConfigureAwait(false);
                captainId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Captains.ReadAsync(created.Id, token).ConfigureAwait(false), "Captain");
                DatabaseAssert.AllProperties(created, await _Driver.Captains.ReadAsync(tenant.Id, user.Id, created.Id, token).ConfigureAwait(false), "Captain by tenant and user");
                DatabaseAssert.AllProperties(created, await _Driver.Captains.ReadByNameAsync(created.Name, token).ConfigureAwait(false), "Captain by name");

                DateTime aliveFloor = DateTime.UtcNow.AddSeconds(-1);
                await _Driver.Captains.UpdateProcessAliveAsync(created.Id, token).ConfigureAwait(false);
                Captain alive = DatabaseAssert.NotNull(await _Driver.Captains.ReadAsync(created.Id, token).ConfigureAwait(false), "Captain after liveness");
                DatabaseAssert.True(alive.LastProcessAliveUtc.HasValue && alive.LastProcessAliveUtc.Value.Kind == DateTimeKind.Utc
                    && alive.LastProcessAliveUtc.Value >= aliveFloor && alive.LastProcessAliveUtc.Value <= DateTime.UtcNow.AddSeconds(1),
                    "Captain liveness reads back as the UTC instant it was written, got " + alive.LastProcessAliveUtc?.ToString("O"));

                created.Model = null;
                created.ModelEndpointId = null;
                created.ApiKey = null;
                created.ApiBaseUrl = null;
                created.SystemInstructions = null;
                created.AllowedPersonas = null;
                created.PreferredPersona = null;
                created.RuntimeOptionsJson = null;
                created.Tier = null;
                created.PreferenceRank = 0;
                created.State = CaptainStateEnum.Idle;
                created.CurrentMissionId = null;
                created.CurrentDockId = null;
                created.ProcessId = null;
                created.RecoveryAttempts = 0;
                created.LastHeartbeatUtc = null;
                created.QuarantineUntilUtc = null;
                created.QuarantineReason = String.Empty;
                created.DefaultPlaybooks = null;
                Captain updated = await _Driver.Captains.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Captain reread = DatabaseAssert.NotNull(await reopened.Captains.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Captain");
                    // An empty stored quarantine reason reads as no reason on every provider.
                    DatabaseAssert.True(reread.QuarantineReason == null, "Empty quarantine reason reads as null, got '" + reread.QuarantineReason + "'");
                    // Liveness is written only by its own refresh, so an ordinary update keeps the refreshed value.
                    DatabaseAssert.UtcInstant(alive.LastProcessAliveUtc, reread.LastProcessAliveUtc, "Reopened Captain.LastProcessAliveUtc");
                    DatabaseAssert.AllProperties(updated, reread, "Reopened Captain", nameof(Captain.QuarantineReason), nameof(Captain.LastProcessAliveUtc));
                }
            }
            finally
            {
                if (captainId != null && !_NoCleanup) await _Driver.Captains.DeleteAsync(captainId, token).ConfigureAwait(false);
                if (endpointId != null && !_NoCleanup) await _Driver.ModelEndpoints.DeleteAsync(endpointId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyWorkflowProfilesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? profileId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("workflow-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "workflow-round-trip", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "workflow-round-trip", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "workflow-round-trip", token).ConfigureAwait(false);
                WorkflowProfile profile = new WorkflowProfile
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Name = "round-trip-workflow-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Description = "Workflow description ユニコード",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    FleetId = fleet.Id,
                    VesselId = vessel.Id,
                    IsDefault = true,
                    Active = false,
                    LanguageHints = new List<string> { "csharp", "ユニコード" },
                    LintCommand = "lint",
                    BuildCommand = "build",
                    UnitTestCommand = "unit",
                    ContainerlessUnitTestCommand = "unit --no-containers",
                    IntegrationTestCommand = "integration",
                    E2ETestCommand = "e2e",
                    MigrationCommand = "migrate",
                    SecurityScanCommand = "scan",
                    PerformanceCommand = "perf",
                    PackageCommand = "package",
                    DeploymentVerificationCommand = "verify",
                    RollbackVerificationCommand = "verify-rollback",
                    PublishArtifactCommand = "publish",
                    ReleaseVersioningCommand = "version",
                    ChangelogGenerationCommand = "changelog",
                    EnvironmentVariables = new Dictionary<string, string> { { "FIXTURE_MODE", "round-trip" } },
                    RequiredSecrets = new List<string> { "env:API_TOKEN" },
                    ExpectedArtifacts = new List<string> { "artifacts/app.zip" },
                    Environments = new List<WorkflowEnvironmentProfile>
                    {
                        new WorkflowEnvironmentProfile
                        {
                            EnvironmentName = "staging",
                            DeployCommand = "deploy",
                            RollbackCommand = "rollback",
                            SmokeTestCommand = "smoke",
                            HealthCheckCommand = "health",
                            DeploymentVerificationCommand = "verify-deploy",
                            RollbackVerificationCommand = "verify-rollback"
                        }
                    },
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                WorkflowProfile created = await _Driver.WorkflowProfiles.CreateAsync(profile, token).ConfigureAwait(false);
                profileId = created.Id;
                WorkflowProfileQuery scope = new WorkflowProfileQuery { TenantId = tenant.Id, UserId = user.Id };
                DatabaseAssert.AllProperties(created, await _Driver.WorkflowProfiles.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "WorkflowProfile");

                created.Description = null;
                created.Scope = WorkflowProfileScopeEnum.Global;
                created.FleetId = null;
                created.VesselId = null;
                created.IsDefault = false;
                created.Active = true;
                created.LanguageHints = new List<string>();
                created.LintCommand = null;
                created.ChangelogGenerationCommand = null;
                created.EnvironmentVariables = new Dictionary<string, string>();
                created.RequiredSecrets = new List<string>();
                created.ExpectedArtifacts = new List<string>();
                created.Environments = new List<WorkflowEnvironmentProfile>();
                WorkflowProfile updated = await _Driver.WorkflowProfiles.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.WorkflowProfiles.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Reopened WorkflowProfile");
                }
            }
            finally
            {
                if (profileId != null && !_NoCleanup) await _Driver.WorkflowProfiles.DeleteAsync(profileId, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyCheckRunsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? runId = null;
            try
            {
                DeliveryGraph graph = await CreateDeliveryGraphAsync(fixture, "check-round-trip", token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                CheckRun run = new CheckRun
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    WorkflowProfileId = graph.Profile.Id,
                    VesselId = graph.Vessel.Id,
                    MissionId = graph.Mission.Id,
                    VoyageId = graph.Voyage.Id,
                    DeploymentId = "dpl_check_" + suffix,
                    Label = "Check label ユニコード",
                    Type = CheckRunTypeEnum.UnitTest,
                    Source = CheckRunSourceEnum.External,
                    Status = CheckRunStatusEnum.Failed,
                    ProviderName = "provider",
                    ExternalId = "external-" + suffix,
                    ExternalUrl = "https://ci.example.test/" + suffix,
                    EnvironmentName = "staging",
                    Command = "dotnet test ユニコード",
                    WorkingDirectory = "work/dir",
                    BranchName = "feature/" + suffix,
                    CommitHash = "0123456789abcdef0123456789abcdef01234567",
                    RegressionPurpose = RegressionPurposeEnum.Consumer,
                    RegressionObjectiveId = "obj_" + suffix,
                    RegressionLandedCommit = "fedcba9876543210fedcba9876543210fedcba98",
                    ExitCode = 3,
                    Output = "Output ユニコード",
                    Summary = "Summary",
                    TestSummary = new CheckRunTestSummary { Format = "trx", Total = 10, Passed = 7, Failed = 2, Skipped = 1, DurationMs = 5000000000L },
                    CoverageSummary = new CheckRunCoverageSummary { Format = "cobertura", SourcePath = "coverage.xml" },
                    Artifacts = new List<CheckRunArtifact> { new CheckRunArtifact { Path = "artifacts/out.txt", SizeBytes = 42, LastWriteUtc = baseUtc } },
                    DurationMs = 5000000000L,
                    SlotRequestedUtc = baseUtc.AddMinutes(-3),
                    StartedUtc = baseUtc.AddMinutes(-2),
                    CompletedUtc = baseUtc.AddMinutes(-1),
                    CreatedUtc = baseUtc.AddMinutes(-4)
                };
                CheckRun created = await _Driver.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
                runId = created.Id;
                CheckRunQuery scope = new CheckRunQuery { TenantId = graph.Tenant.Id, UserId = graph.User.Id };
                DatabaseAssert.AllProperties(created, await _Driver.CheckRuns.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "CheckRun");

                created.WorkflowProfileId = null;
                created.MissionId = null;
                created.VoyageId = null;
                created.DeploymentId = null;
                created.Label = null;
                created.Status = CheckRunStatusEnum.Passed;
                created.ProviderName = null;
                created.ExternalId = null;
                created.ExternalUrl = null;
                created.EnvironmentName = null;
                created.WorkingDirectory = null;
                created.BranchName = null;
                created.CommitHash = null;
                created.RegressionPurpose = RegressionPurposeEnum.None;
                created.RegressionObjectiveId = null;
                created.RegressionLandedCommit = null;
                created.ExitCode = null;
                created.Output = null;
                created.Summary = null;
                created.TestSummary = null;
                created.CoverageSummary = null;
                created.Artifacts = new List<CheckRunArtifact>();
                created.DurationMs = null;
                created.SlotRequestedUtc = null;
                created.StartedUtc = null;
                created.CompletedUtc = null;
                CheckRun updated = await _Driver.CheckRuns.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.CheckRuns.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Reopened CheckRun");
                }
            }
            finally
            {
                if (runId != null && !_NoCleanup) await _Driver.CheckRuns.DeleteAsync(runId, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyDeploymentEnvironmentsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? environmentId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("environment-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "environment-round-trip", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "environment-round-trip", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "environment-round-trip", token).ConfigureAwait(false);
                DeploymentEnvironment environment = new DeploymentEnvironment
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    VesselId = vessel.Id,
                    Name = "round-trip-environment-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Description = "Environment description ユニコード",
                    Kind = EnvironmentKindEnum.Production,
                    ConfigurationSource = "config/production.json",
                    BaseUrl = "https://production.example.test",
                    HealthEndpoint = "/health",
                    AccessNotes = "Access notes",
                    DeploymentRules = "Deployment rules",
                    VerificationDefinitions = new List<DeploymentVerificationDefinition>
                    {
                        new DeploymentVerificationDefinition
                        {
                            Name = "home",
                            Method = "POST",
                            Path = "/",
                            RequestBody = "{}",
                            Headers = new Dictionary<string, string> { { "X-Fixture", "1" } },
                            ExpectedStatusCode = 201,
                            MustContainText = "ok",
                            Active = false
                        }
                    },
                    RolloutMonitoringWindowMinutes = 45,
                    RolloutMonitoringIntervalSeconds = 90,
                    AlertOnRegression = false,
                    RequiresApproval = true,
                    IsDefault = true,
                    Active = false,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                DeploymentEnvironment created = await _Driver.Environments.CreateAsync(environment, token).ConfigureAwait(false);
                environmentId = created.Id;
                DeploymentEnvironmentQuery scope = new DeploymentEnvironmentQuery { TenantId = tenant.Id, UserId = user.Id };
                DatabaseAssert.AllProperties(created, await _Driver.Environments.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "DeploymentEnvironment");

                created.Description = null;
                created.Kind = EnvironmentKindEnum.Staging;
                created.ConfigurationSource = null;
                created.BaseUrl = null;
                created.HealthEndpoint = null;
                created.AccessNotes = null;
                created.DeploymentRules = null;
                created.VerificationDefinitions = new List<DeploymentVerificationDefinition>();
                created.RolloutMonitoringWindowMinutes = 0;
                created.RolloutMonitoringIntervalSeconds = 300;
                created.AlertOnRegression = true;
                created.RequiresApproval = false;
                created.IsDefault = false;
                created.Active = true;
                DeploymentEnvironment updated = await _Driver.Environments.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Environments.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Reopened DeploymentEnvironment");
                }
            }
            finally
            {
                if (environmentId != null && !_NoCleanup) await _Driver.Environments.DeleteAsync(environmentId, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyReleasesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? releaseId = null;
            try
            {
                DeliveryGraph graph = await CreateDeliveryGraphAsync(fixture, "release-round-trip", token).ConfigureAwait(false);
                Release release = new Release
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    VesselId = graph.Vessel.Id,
                    WorkflowProfileId = graph.Profile.Id,
                    Title = "Round-trip release ユニコード",
                    Version = "2.3.4",
                    TagName = "v2.3.4",
                    Summary = "Release summary",
                    Notes = "Release notes ユニコード",
                    Status = ReleaseStatusEnum.Shipped,
                    VoyageIds = new List<string> { graph.Voyage.Id },
                    MissionIds = new List<string> { graph.Mission.Id },
                    CheckRunIds = new List<string> { graph.CheckRun.Id },
                    Artifacts = new List<ReleaseArtifact>
                    {
                        new ReleaseArtifact { SourceType = "CheckRun", SourceId = graph.CheckRun.Id, Path = "artifacts/app.zip", SizeBytes = 5000000000L, LastWriteUtc = DateTime.UtcNow.AddMinutes(-3) }
                    },
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
                    PublishedUtc = DateTime.UtcNow.AddMinutes(-1)
                };
                Release created = await _Driver.Releases.CreateAsync(release, token).ConfigureAwait(false);
                releaseId = created.Id;
                ReleaseQuery scope = new ReleaseQuery { TenantId = graph.Tenant.Id, UserId = graph.User.Id };
                DatabaseAssert.AllProperties(created, await _Driver.Releases.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Release");

                created.WorkflowProfileId = null;
                created.Version = null;
                created.TagName = null;
                created.Summary = null;
                created.Notes = null;
                created.Status = ReleaseStatusEnum.Draft;
                created.VoyageIds = new List<string>();
                created.MissionIds = new List<string>();
                created.CheckRunIds = new List<string>();
                created.Artifacts = new List<ReleaseArtifact>();
                created.PublishedUtc = null;
                Release updated = await _Driver.Releases.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Releases.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Reopened Release");
                }
            }
            finally
            {
                if (releaseId != null && !_NoCleanup) await _Driver.Releases.DeleteAsync(releaseId, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyDeploymentsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? deploymentId = null;
            try
            {
                DeliveryGraph graph = await CreateDeliveryGraphAsync(fixture, "deployment-round-trip", token).ConfigureAwait(false);
                DeploymentEnvironment environment = await fixture.CreateDeploymentEnvironmentAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, "deployment-round-trip", token: token).ConfigureAwait(false);
                Release release = await fixture.CreateReleaseAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, token: token).ConfigureAwait(false);
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                Deployment deployment = new Deployment
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    VesselId = graph.Vessel.Id,
                    WorkflowProfileId = graph.Profile.Id,
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    ReleaseId = release.Id,
                    MissionId = graph.Mission.Id,
                    VoyageId = graph.Voyage.Id,
                    Title = "Round-trip deployment ユニコード",
                    SourceRef = "refs/heads/main",
                    Summary = "Deployment summary",
                    Notes = "Deployment notes ユニコード",
                    Status = DeploymentStatusEnum.RolledBack,
                    VerificationStatus = DeploymentVerificationStatusEnum.Failed,
                    ApprovalRequired = true,
                    ApprovedByUserId = graph.User.Id,
                    ApprovedUtc = baseUtc.AddMinutes(-9),
                    ApprovalComment = "Approved ユニコード",
                    DeployCheckRunId = graph.CheckRun.Id,
                    SmokeTestCheckRunId = graph.CheckRun.Id,
                    HealthCheckRunId = graph.CheckRun.Id,
                    DeploymentVerificationCheckRunId = graph.CheckRun.Id,
                    RollbackCheckRunId = graph.CheckRun.Id,
                    RollbackVerificationCheckRunId = graph.CheckRun.Id,
                    CheckRunIds = new List<string> { graph.CheckRun.Id },
                    RequestHistorySummary = new RequestHistorySummaryResult { TotalCount = 4, SuccessCount = 3, FailureCount = 1, SuccessRate = 75, AverageDurationMs = 12.5, FromUtc = baseUtc.AddMinutes(-8), ToUtc = baseUtc, BucketMinutes = 15 },
                    CreatedUtc = baseUtc.AddMinutes(-10),
                    StartedUtc = baseUtc.AddMinutes(-8),
                    CompletedUtc = baseUtc.AddMinutes(-7),
                    VerifiedUtc = baseUtc.AddMinutes(-6),
                    RolledBackUtc = baseUtc.AddMinutes(-5),
                    MonitoringWindowEndsUtc = baseUtc.AddMinutes(30),
                    LastMonitoredUtc = baseUtc.AddMinutes(-4),
                    LastRegressionAlertUtc = baseUtc.AddMinutes(-3),
                    LatestMonitoringSummary = "Monitoring summary ユニコード",
                    MonitoringFailureCount = 4
                };
                Deployment created = await _Driver.Deployments.CreateAsync(deployment, token).ConfigureAwait(false);
                deploymentId = created.Id;
                DeploymentQuery scope = new DeploymentQuery { TenantId = graph.Tenant.Id, UserId = graph.User.Id };
                DatabaseAssert.AllProperties(created, await _Driver.Deployments.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Deployment");

                created.WorkflowProfileId = null;
                created.ReleaseId = null;
                created.MissionId = null;
                created.VoyageId = null;
                created.SourceRef = null;
                created.Summary = null;
                created.Notes = null;
                created.Status = DeploymentStatusEnum.PendingApproval;
                created.VerificationStatus = DeploymentVerificationStatusEnum.NotRun;
                created.ApprovalRequired = false;
                created.ApprovedByUserId = null;
                created.ApprovedUtc = null;
                created.ApprovalComment = null;
                created.DeployCheckRunId = null;
                created.SmokeTestCheckRunId = null;
                created.HealthCheckRunId = null;
                created.DeploymentVerificationCheckRunId = null;
                created.RollbackCheckRunId = null;
                created.RollbackVerificationCheckRunId = null;
                created.CheckRunIds = new List<string>();
                created.RequestHistorySummary = null;
                created.StartedUtc = null;
                created.CompletedUtc = null;
                created.VerifiedUtc = null;
                created.RolledBackUtc = null;
                created.MonitoringWindowEndsUtc = null;
                created.LastMonitoredUtc = null;
                created.LastRegressionAlertUtc = null;
                created.LatestMonitoringSummary = null;
                created.MonitoringFailureCount = 0;
                Deployment updated = await _Driver.Deployments.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Deployments.ReadAsync(created.Id, scope, token).ConfigureAwait(false), "Reopened Deployment");
                }
            }
            finally
            {
                if (deploymentId != null && !_NoCleanup) await _Driver.Deployments.DeleteAsync(deploymentId, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyMemoriesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? memoryId = null;
            string? tenantId = null;
            try
            {
                DeliveryGraph graph = await CreateDeliveryGraphAsync(fixture, "memory-round-trip", token).ConfigureAwait(false);
                tenantId = graph.Tenant.Id;
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                Memory memory = new Memory
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    Scope = MemoryScopeEnum.UserSpecific,
                    Type = MemoryTypeEnum.Procedural,
                    Topic = "topic ユニコード",
                    Key = "round-trip/" + suffix,
                    Summary = "Summary ユニコード",
                    Content = "Content ユニコード",
                    Salience = 0.625,
                    SourceKind = MemorySourceKindEnum.Voyage,
                    SourceVoyageId = graph.Voyage.Id,
                    SourceMissionId = graph.Mission.Id,
                    SourceVesselId = graph.Vessel.Id,
                    SourceDetail = "Source detail",
                    VesselId = graph.Vessel.Id,
                    Tags = new List<string> { "alpha", "ユニコード" },
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                Memory created = await _Driver.Memories.CreateAsync(memory, token).ConfigureAwait(false);
                memoryId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Memories.ReadAsync(graph.Tenant.Id, created.Id, token).ConfigureAwait(false), "Memory");
                DatabaseAssert.AllProperties(created, await _Driver.Memories.ReadByKeyAsync(graph.Tenant.Id, created.Key!, token).ConfigureAwait(false), "Memory by key");

                Memory changed = DatabaseAssert.NotNull(await _Driver.Memories.ReadAsync(created.Id, token).ConfigureAwait(false), "Memory to update");
                int expectedVersion = changed.Version;
                changed.Scope = MemoryScopeEnum.TenantWide;
                changed.Type = MemoryTypeEnum.Semantic;
                changed.Topic = null;
                changed.Summary = null;
                changed.Content = "Updated content";
                changed.Salience = 0.25;
                changed.SourceKind = MemorySourceKindEnum.Manual;
                changed.SourceVoyageId = null;
                changed.SourceMissionId = null;
                changed.SourceVesselId = null;
                changed.SourceDetail = null;
                changed.VesselId = null;
                changed.Tags = new List<string>();
                changed.Version = expectedVersion + 1;
                changed.LastUpdateUtc = DateTime.UtcNow;
                DatabaseAssert.True(await _Driver.Memories.UpdateAsync(changed, expectedVersion, token).ConfigureAwait(false), "Memory update applies");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(changed, await reopened.Memories.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Memory");
                }
            }
            finally
            {
                if (memoryId != null && tenantId != null && !_NoCleanup) await _Driver.Memories.DeleteAsync(tenantId, memoryId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyModelEndpointsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? endpointId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("endpoint-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "endpoint-round-trip", token: token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Id = "mep_round_trip_" + suffix,
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Name = "Endpoint ユニコード " + suffix,
                    Kind = ModelEndpointKindEnum.Embedding,
                    Scope = ScopeEnum.UserSpecific,
                    Provider = ModelProviderEnum.OpenAICompatible,
                    BaseUrl = "http://localhost:9999/v1",
                    Model = "model ユニコード",
                    Dimensionality = 1536,
                    TimeoutMs = 45000,
                    Enabled = false,
                    ApiKey = "api-key-" + suffix,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                ModelEndpoint created = await _Driver.ModelEndpoints.CreateAsync(endpoint, token).ConfigureAwait(false);
                endpointId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.ModelEndpoints.ReadAsync(tenant.Id, user.Id, created.Id, token).ConfigureAwait(false), "ModelEndpoint");

                ModelEndpoint health = DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(created.Id, token).ConfigureAwait(false), "Endpoint for health");
                DateTime expectedLastUpdateUtc = health.LastUpdateUtc;
                DateTime checkedUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                health.HealthStatus = EndpointHealthStatusEnum.Unhealthy;
                health.LastHealthCheckUtc = checkedUtc;
                health.LastHealthError = "Health error ユニコード";
                health.LastLatencyMs = 4321;
                health.HealthHistory = new List<ModelEndpointHealthRecord>
                {
                    new ModelEndpointHealthRecord { TimestampUtc = checkedUtc.AddMinutes(-1), Success = true },
                    new ModelEndpointHealthRecord { TimestampUtc = checkedUtc, Success = false }
                };
                health.LastUpdateUtc = DateTime.UtcNow;
                DatabaseAssert.True(await _Driver.ModelEndpoints.UpdateHealthAsync(health, expectedLastUpdateUtc, token).ConfigureAwait(false), "Endpoint health update applies");
                DatabaseAssert.AllProperties(health, await _Driver.ModelEndpoints.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "ModelEndpoint after health");

                health.TenantId = tenant.Id;
                health.Model = null;
                health.Dimensionality = 0;
                health.TimeoutMs = 120000;
                health.Enabled = true;
                health.ApiKey = null;
                ModelEndpoint updated = await _Driver.ModelEndpoints.UpdateAsync(health, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.ModelEndpoints.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened ModelEndpoint");
                }
            }
            finally
            {
                if (endpointId != null && !_NoCleanup) await _Driver.ModelEndpoints.DeleteAsync(endpointId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyPromptTemplatesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? templateId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("template-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "template-round-trip", token: token).ConfigureAwait(false);
                PromptTemplate template = new PromptTemplate
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    OwnershipScope = OwnershipScopeEnum.UserSpecific,
                    Name = "round-trip.template." + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Description = "Template description ユニコード",
                    Category = "persona",
                    Content = "Template content ユニコード {{value}}",
                    IsBuiltIn = true,
                    Active = false,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                PromptTemplate created = await _Driver.PromptTemplates.CreateAsync(template, token).ConfigureAwait(false);
                templateId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.PromptTemplates.ReadAsync(created.Id, token).ConfigureAwait(false), "PromptTemplate");
                DatabaseAssert.AllProperties(created, await _Driver.PromptTemplates.ReadByNameAsync(tenant.Id, created.Name, token).ConfigureAwait(false), "PromptTemplate by name");

                created.OwnershipScope = OwnershipScopeEnum.TenantWide;
                created.Description = null;
                created.Category = "mission";
                created.Content = "Updated content";
                created.IsBuiltIn = false;
                created.Active = true;
                PromptTemplate updated = await _Driver.PromptTemplates.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.PromptTemplates.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened PromptTemplate");
                }
            }
            finally
            {
                if (templateId != null && !_NoCleanup) await _Driver.PromptTemplates.DeleteAsync(templateId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyTokenUsageRecordsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? sourceId = null;
            try
            {
                DeliveryGraph graph = await CreateDeliveryGraphAsync(fixture, "usage-round-trip", token).ConfigureAwait(false);
                sourceId = "msn_usage_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                TokenUsageRecord full = new TokenUsageRecord
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    Model = "model ユニコード",
                    Runtime = "Codex",
                    Source = "mission",
                    SourceId = sourceId,
                    VesselId = graph.Vessel.Id,
                    CaptainId = graph.Captain.Id,
                    InputTokens = 5000000001L,
                    OutputTokens = 5000000002L,
                    CachedTokens = 5000000003L,
                    TotalTokens = 15000000006L,
                    UsageRule = TokenUsageRuleEnum.SeparateInputBuckets,
                    UncachedInputTokens = 5000000004L,
                    CacheReadInputTokens = 5000000005L,
                    CacheWriteInputTokens = 5000000006L,
                    Estimated = true,
                    CreatedUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560)
                };
                TokenUsageRecord sparse = new TokenUsageRecord
                {
                    TenantId = graph.Tenant.Id,
                    Model = "sparse-model",
                    Source = "mission",
                    SourceId = sourceId,
                    InputTokens = 1,
                    OutputTokens = 2,
                    TotalTokens = 3,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
                };
                TokenUsageRecord createdFull = await _Driver.TokenUsage.CreateAsync(full, token).ConfigureAwait(false);
                TokenUsageRecord createdSparse = await _Driver.TokenUsage.CreateAsync(sparse, token).ConfigureAwait(false);
                TokenUsageQuery scope = new TokenUsageQuery { TenantId = graph.Tenant.Id };
                DatabaseAssert.AllProperties(createdFull, await _Driver.TokenUsage.ReadAsync(createdFull.Id, scope, token).ConfigureAwait(false), "TokenUsageRecord");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(createdFull, await reopened.TokenUsage.ReadAsync(createdFull.Id, scope, token).ConfigureAwait(false), "Reopened TokenUsageRecord");
                    DatabaseAssert.AllProperties(createdSparse, await reopened.TokenUsage.ReadAsync(createdSparse.Id, scope, token).ConfigureAwait(false), "Reopened sparse TokenUsageRecord");
                }
            }
            finally
            {
                if (sourceId != null && !_NoCleanup) await _Driver.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { SourceId = sourceId }, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyRequestHistoryAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            List<string> ids = new List<string>();
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("history-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "history-round-trip", token: token).ConfigureAwait(false);
                Credential credential = await fixture.CreateCredentialAsync(tenant.Id, user.Id, "history-round-trip", token: token).ConfigureAwait(false);
                RequestHistoryEntry entry = new RequestHistoryEntry
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    CredentialId = credential.Id,
                    PrincipalDisplay = "Principal ユニコード",
                    AuthMethod = "Bearer",
                    Method = "POST",
                    Route = "/api/v1/round-trip",
                    RouteTemplate = "/api/v1/{name}",
                    QueryString = "?a=1",
                    StatusCode = 503,
                    DurationMs = 12.5,
                    RequestSizeBytes = 123456,
                    ResponseSizeBytes = 654321,
                    RequestContentType = "application/json",
                    ResponseContentType = "text/plain",
                    IsSuccess = false,
                    ClientIp = "192.0.2.1",
                    CorrelationId = "corr-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    CreatedUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560)
                };
                RequestHistoryDetail detail = new RequestHistoryDetail
                {
                    RequestHistoryId = entry.Id,
                    PathParamsJson = "{\"name\":\"round-trip\"}",
                    QueryParamsJson = "{\"a\":\"1\"}",
                    RequestHeadersJson = "{\"X-Fixture\":\"1\"}",
                    ResponseHeadersJson = "{\"X-Reply\":\"1\"}",
                    RequestBodyText = "Request body ユニコード",
                    ResponseBodyText = "Response body",
                    RequestBodyTruncated = true,
                    ResponseBodyTruncated = true
                };
                RequestHistoryRecord created = await _Driver.RequestHistory.CreateAsync(entry, detail, token).ConfigureAwait(false);
                ids.Add(created.Entry.Id);
                RequestHistoryEntry bare = new RequestHistoryEntry { Method = "GET", Route = "/api/v1/bare", CreatedUtc = DateTime.UtcNow.AddMinutes(-1) };
                RequestHistoryRecord createdBare = await _Driver.RequestHistory.CreateAsync(bare, null, token).ConfigureAwait(false);
                ids.Add(createdBare.Entry.Id);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    RequestHistoryRecord read = DatabaseAssert.NotNull(await reopened.RequestHistory.ReadAsync(created.Entry.Id, null, token).ConfigureAwait(false), "Reopened RequestHistoryRecord");
                    DatabaseAssert.AllProperties(created.Entry, read.Entry, "Reopened RequestHistoryEntry");
                    DatabaseAssert.AllProperties(created.Detail!, DatabaseAssert.NotNull(read.Detail, "Reopened RequestHistoryDetail"), "Reopened RequestHistoryDetail");
                    RequestHistoryRecord readBare = DatabaseAssert.NotNull(await reopened.RequestHistory.ReadAsync(createdBare.Entry.Id, null, token).ConfigureAwait(false), "Reopened bare RequestHistoryRecord");
                    DatabaseAssert.AllProperties(createdBare.Entry, readBare.Entry, "Reopened bare RequestHistoryEntry");
                }
            }
            finally
            {
                if (!_NoCleanup) foreach (string id in ids) await _Driver.RequestHistory.DeleteAsync(id, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyPersonasAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? personaId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("persona-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "persona-round-trip", token: token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "persona-round-trip", token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                Persona persona = new Persona
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    OwnershipScope = OwnershipScopeEnum.UserSpecific,
                    Name = "RoundTripPersona" + suffix,
                    Description = "Persona description ユニコード",
                    DefaultCaptainId = captain.Id,
                    PromptTemplateName = "persona.worker",
                    IsBuiltIn = true,
                    DefaultPlaybooks = "[{\"playbookId\":\"pbk_" + suffix + "\",\"deliveryMode\":\"InstructionWithReference\"}]",
                    MinimumTier = CaptainTierEnum.Premium,
                    Active = false,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                Persona created = await _Driver.Personas.CreateAsync(persona, token).ConfigureAwait(false);
                personaId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Personas.ReadAsync(created.Id, token).ConfigureAwait(false), "Persona");
                DatabaseAssert.AllProperties(created, await _Driver.Personas.ReadByNameAsync(tenant.Id, created.Name, token).ConfigureAwait(false), "Persona by name");

                created.OwnershipScope = OwnershipScopeEnum.TenantWide;
                created.Description = null;
                created.DefaultCaptainId = null;
                created.PromptTemplateName = "persona.judge";
                created.IsBuiltIn = false;
                created.DefaultPlaybooks = null;
                created.MinimumTier = null;
                created.Active = true;
                Persona updated = await _Driver.Personas.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Personas.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Persona");
                }
            }
            finally
            {
                if (personaId != null && !_NoCleanup) await _Driver.Personas.DeleteAsync(personaId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyPipelinesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? pipelineId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("pipeline-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "pipeline-round-trip", token: token).ConfigureAwait(false);
                Pipeline pipeline = new Pipeline
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    OwnershipScope = OwnershipScopeEnum.UserSpecific,
                    Name = "RoundTripPipeline" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Description = "Pipeline description ユニコード",
                    IsBuiltIn = true,
                    Active = false,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage
                        {
                            Order = 1,
                            PersonaName = "Worker",
                            IsOptional = true,
                            Description = "Stage description ユニコード",
                            PreferredModel = "model ユニコード",
                            RequiresReview = true,
                            ReviewDenyAction = ReviewDenyActionEnum.FailPipeline
                        },
                        new PipelineStage
                        {
                            Order = 2,
                            PersonaName = "Judge"
                        }
                    }
                };
                Pipeline created = await _Driver.Pipelines.CreateAsync(pipeline, token).ConfigureAwait(false);
                pipelineId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Pipelines.ReadAsync(created.Id, token).ConfigureAwait(false), "Pipeline");
                DatabaseAssert.AllProperties(created, await _Driver.Pipelines.ReadByNameAsync(tenant.Id, created.Name, token).ConfigureAwait(false), "Pipeline by name");

                created.OwnershipScope = OwnershipScopeEnum.TenantWide;
                created.Description = null;
                created.IsBuiltIn = false;
                created.Active = true;
                created.Stages[0].IsOptional = false;
                created.Stages[0].Description = null;
                created.Stages[0].PreferredModel = null;
                created.Stages[0].RequiresReview = false;
                created.Stages[0].ReviewDenyAction = ReviewDenyActionEnum.RetryStage;
                Pipeline updated = await _Driver.Pipelines.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Pipelines.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Pipeline");
                }
            }
            finally
            {
                if (pipelineId != null && !_NoCleanup) await _Driver.Pipelines.DeleteAsync(pipelineId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyDocksAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? dockId = null;
            try
            {
                DeliveryGraph graph = await CreateDeliveryGraphAsync(fixture, "dock-round-trip", token).ConfigureAwait(false);
                Dock dock = new Dock
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    VesselId = graph.Vessel.Id,
                    CaptainId = graph.Captain.Id,
                    WorktreePath = "docks/round-trip ユニコード",
                    BranchName = "armada/round-trip",
                    Active = false,
                    CreatedUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560)
                };
                Dock created = await _Driver.Docks.CreateAsync(dock, token).ConfigureAwait(false);
                dockId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Docks.ReadAsync(created.Id, token).ConfigureAwait(false), "Dock");
                DatabaseAssert.AllProperties(created, await _Driver.Docks.ReadAsync(graph.Tenant.Id, graph.User.Id, created.Id, token).ConfigureAwait(false), "Dock by tenant and user");

                created.CaptainId = null;
                created.WorktreePath = null;
                created.BranchName = null;
                created.Active = true;
                Dock updated = await _Driver.Docks.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Docks.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Dock");
                }
            }
            finally
            {
                if (dockId != null && !_NoCleanup) await _Driver.Docks.DeleteAsync(dockId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyVoyagesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? voyageId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("voyage-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "voyage-round-trip", token: token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                Voyage voyage = new Voyage("Round-trip voyage ユニコード", "Voyage description ユニコード")
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Status = VoyageStatusEnum.Failed,
                    CreatedUtc = baseUtc.AddMinutes(-5),
                    CompletedUtc = baseUtc,
                    AutoPush = true,
                    AutoCreatePullRequests = false,
                    AutoMergePullRequests = true,
                    LandingMode = LandingModeEnum.MergeQueue,
                    SourcePlanningSessionId = "pls_" + suffix,
                    SourcePlanningMessageId = "plm_" + suffix,
                    CaptainOverridesJson = "{\"Worker\":\"cpt_" + suffix + "\"}"
                };
                Voyage created = await _Driver.Voyages.CreateAsync(voyage, token).ConfigureAwait(false);
                voyageId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Voyages.ReadAsync(created.Id, token).ConfigureAwait(false), "Voyage");
                DatabaseAssert.AllProperties(created, await _Driver.Voyages.ReadAsync(tenant.Id, user.Id, created.Id, token).ConfigureAwait(false), "Voyage by tenant and user");

                created.Description = null;
                created.Status = VoyageStatusEnum.Open;
                created.CompletedUtc = null;
                created.AutoPush = null;
                created.AutoCreatePullRequests = null;
                created.AutoMergePullRequests = null;
                created.LandingMode = null;
                created.SourcePlanningSessionId = String.Empty;
                created.SourcePlanningMessageId = String.Empty;
                created.CaptainOverridesJson = null;
                Voyage updated = await _Driver.Voyages.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Voyage reread = DatabaseAssert.NotNull(await reopened.Voyages.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Voyage");
                    // An empty stored planning source reads as no source on every provider.
                    DatabaseAssert.True(reread.SourcePlanningSessionId == null && reread.SourcePlanningMessageId == null,
                        "Empty planning source ids read as null, got '" + reread.SourcePlanningSessionId + "' and '" + reread.SourcePlanningMessageId + "'");
                    DatabaseAssert.AllProperties(updated, reread, "Reopened Voyage", nameof(Voyage.SourcePlanningSessionId), nameof(Voyage.SourcePlanningMessageId));
                }
            }
            finally
            {
                if (voyageId != null && !_NoCleanup) await _Driver.Voyages.DeleteAsync(voyageId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyMissionsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? missionId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("mission-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "mission-round-trip", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "mission-round-trip", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "mission-round-trip", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "mission-round-trip", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "mission-round-trip", token).ConfigureAwait(false);
                Mission parent = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "mission-round-trip-parent", token).ConfigureAwait(false);
                Dock dock = await fixture.CreateDockAsync(tenant.Id, user.Id, vessel.Id, captain.Id, token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);

                // Sub-second digits and a date far from the host's daylight-saving rules make a host-offset read visible.
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                Mission mission = new Mission("Round-trip mission ユニコード", "Mission description ユニコード")
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    VoyageId = voyage.Id,
                    VesselId = vessel.Id,
                    CaptainId = captain.Id,
                    Status = MissionStatusEnum.Testing,
                    AssignmentState = MissionAssignmentStateEnum.WaitingForProviderUsage,
                    Priority = 7,
                    ParentMissionId = parent.Id,
                    BranchName = "armada/round-trip-" + suffix,
                    DockId = dock.Id,
                    ProcessId = 424242,
                    PrUrl = "https://example.test/pull/1",
                    CommitHash = "0123456789abcdef0123456789abcdef01234567",
                    DiffSnapshot = "diff --git a/file b/file\n+ユニコード",
                    AgentOutput = "Agent output ユニコード",
                    Persona = "Judge",
                    RequestedCaptainId = captain.Id,
                    Tier = CaptainTierEnum.Premium,
                    DependsOnMissionId = parent.Id,
                    StageOrder = 3,
                    PreferredModel = "high",
                    CapabilityHint = "review",
                    Mode = MissionModeEnum.Audit,
                    FailureReason = "Failure reason ユニコード",
                    ReconciledUtc = baseUtc.AddMinutes(1),
                    ReconciledReason = "Reconciled reason",
                    HeldForOperatorReview = true,
                    HeldForOperatorReviewReason = "Held reason ユニコード",
                    RequiresReview = true,
                    ReviewDenyAction = ReviewDenyActionEnum.FailPipeline,
                    ReviewComment = "Review comment ユニコード",
                    ReviewedByUserId = user.Id,
                    ReviewRequestedUtc = baseUtc.AddMinutes(2),
                    ReviewedUtc = baseUtc.AddMinutes(3),
                    PrestagedFiles = new List<PrestagedFile> { PrestagedFile.FromContent("input/ユニコード.txt", "prestaged input") },
                    CreatedUtc = baseUtc.AddMinutes(-5),
                    RecoveryAttempts = 2,
                    LandingRetryCount = 3,
                    StartFromRef = "refs/heads/accepted-" + suffix,
                    RetrySkipCaptainIds = "cpt_a_" + suffix + ",cpt_b_" + suffix,
                    LastRecoveryActionUtc = baseUtc.AddMinutes(4)
                };
                // The start time is set after the identifier, because a new identifier clears it.
                mission.ProcessStartedUtc = baseUtc.AddMinutes(-4);
                mission.StartedUtc = baseUtc.AddMinutes(-3);
                mission.CompletedUtc = baseUtc;
                Mission created = await _Driver.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                missionId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Missions.ReadAsync(created.Id, token).ConfigureAwait(false), "Mission");
                DatabaseAssert.AllProperties(created, await _Driver.Missions.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "Mission by tenant");
                DatabaseAssert.AllProperties(created, await _Driver.Missions.ReadAsync(tenant.Id, user.Id, created.Id, token).ConfigureAwait(false), "Mission by tenant and user");
                List<Mission> byVoyage = await _Driver.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(created, byVoyage.Find(item => item.Id == created.Id), "Mission by voyage");
                EnumerationResult<Mission> page = await _Driver.Missions.EnumerateAsync(tenant.Id, user.Id, new EnumerationQuery { PageSize = 100 }, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(created, page.Objects.Find(item => item.Id == created.Id), "Enumerated Mission");

                created.Description = null;
                created.Status = MissionStatusEnum.Pending;
                created.AssignmentState = MissionAssignmentStateEnum.Pending;
                created.ParentMissionId = null;
                created.BranchName = null;
                created.DockId = null;
                created.ProcessId = null;
                created.PrUrl = null;
                created.CommitHash = null;
                created.DiffSnapshot = null;
                created.AgentOutput = null;
                created.Persona = null;
                created.RequestedCaptainId = null;
                created.Tier = null;
                created.DependsOnMissionId = null;
                created.StageOrder = null;
                created.PreferredModel = null;
                created.CapabilityHint = null;
                created.Mode = MissionModeEnum.Implementation;
                created.FailureReason = null;
                created.ReconciledUtc = null;
                created.ReconciledReason = null;
                created.HeldForOperatorReview = false;
                created.HeldForOperatorReviewReason = null;
                created.RequiresReview = false;
                created.ReviewDenyAction = ReviewDenyActionEnum.RetryStage;
                created.ReviewComment = null;
                created.ReviewedByUserId = null;
                created.ReviewRequestedUtc = null;
                created.ReviewedUtc = null;
                created.PrestagedFiles = null;
                created.StartedUtc = null;
                created.CompletedUtc = null;
                created.TotalRuntimeMs = null;
                created.RecoveryAttempts = 0;
                created.LandingRetryCount = 0;
                created.StartFromRef = null;
                created.RetrySkipCaptainIds = null;
                created.LastRecoveryActionUtc = null;
                Mission updated = await _Driver.Missions.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Missions.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Mission");
                }
            }
            finally
            {
                if (missionId != null && !_NoCleanup) await _Driver.Missions.DeleteAsync(missionId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyVesselsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? vesselId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("vessel-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "vessel-round-trip", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "vessel-round-trip", token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                Vessel vessel = new Vessel("round-trip-vessel-" + suffix + "-ユニコード", "https://example.test/repo-" + suffix + ".git")
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    FleetId = fleet.Id,
                    LocalPath = "/repos/" + suffix + ".git",
                    WorkingDirectory = "/work/" + suffix,
                    GitHubTokenOverride = "token-" + suffix,
                    DefaultBranch = "trunk-" + suffix,
                    ProjectContext = "Project context ユニコード",
                    StyleGuide = "Style guide ユニコード",
                    EnableModelContext = false,
                    ModelContext = "Model context ユニコード",
                    LandingMode = LandingModeEnum.MergeQueue,
                    BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote,
                    ArchitectMaxMissionsPerVoyage = 9,
                    AllowConcurrentMissions = true,
                    RequirePassingChecksToLand = true,
                    ProtectedBranchPatterns = new List<string> { "main", "release/*" },
                    ReleaseBranchPrefix = "rel-" + suffix + "/",
                    HotfixBranchPrefix = "fix-" + suffix + "/",
                    RequirePullRequestForProtectedBranches = true,
                    RequireMergeQueueForReleaseBranches = true,
                    DefaultPipelineId = "ppl_" + suffix,
                    ProtectedPaths = new List<string> { "**/CLAUDE.md", "secrets/**" },
                    AutoLandPredicate = "{\"maxFiles\":3}",
                    DefaultPlaybooks = "[{\"playbookId\":\"pbk_" + suffix + "\",\"deliveryMode\":\"InstructionWithReference\"}]",
                    SiblingRepos = "[{\"name\":\"sibling\"}]",
                    AutoLandCalibrationLandedCount = 4,
                    SecretScanEnabled = true,
                    ProtectedPathPatterns = new List<string> { "*.pem" },
                    PrivateIdentifierDenylist = new List<string> { "private-name" },
                    Active = false,
                    CreatedUtc = baseUtc.AddMinutes(-5)
                };
                Vessel created = await _Driver.Vessels.CreateAsync(vessel, token).ConfigureAwait(false);
                vesselId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Vessels.ReadAsync(created.Id, token).ConfigureAwait(false), "Vessel");
                DatabaseAssert.AllProperties(created, await _Driver.Vessels.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "Vessel by tenant");
                DatabaseAssert.AllProperties(created, await _Driver.Vessels.ReadAsync(tenant.Id, user.Id, created.Id, token).ConfigureAwait(false), "Vessel by tenant and user");
                DatabaseAssert.AllProperties(created, await _Driver.Vessels.ReadByNameAsync(tenant.Id, created.Name, token).ConfigureAwait(false), "Vessel by name");
                List<Vessel> byFleet = await _Driver.Vessels.EnumerateByFleetAsync(fleet.Id, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(created, byFleet.Find(item => item.Id == created.Id), "Vessel by fleet");

                created.LocalPath = null;
                created.WorkingDirectory = null;
                created.GitHubTokenOverride = null;
                created.ProjectContext = null;
                created.StyleGuide = null;
                created.EnableModelContext = true;
                created.ModelContext = null;
                created.LandingMode = null;
                created.BranchCleanupPolicy = null;
                created.ArchitectMaxMissionsPerVoyage = null;
                created.AllowConcurrentMissions = false;
                created.RequirePassingChecksToLand = false;
                created.ProtectedBranchPatterns = new List<string>();
                created.RequirePullRequestForProtectedBranches = false;
                created.RequireMergeQueueForReleaseBranches = false;
                created.DefaultPipelineId = null;
                created.ProtectedPaths = null;
                created.AutoLandPredicate = null;
                created.DefaultPlaybooks = null;
                created.SiblingRepos = null;
                created.AutoLandCalibrationLandedCount = 0;
                created.SecretScanEnabled = false;
                created.ProtectedPathPatterns = new List<string>();
                created.PrivateIdentifierDenylist = new List<string>();
                created.Active = true;
                Vessel updated = await _Driver.Vessels.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Vessels.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Vessel");
                }
            }
            finally
            {
                if (vesselId != null && !_NoCleanup) await _Driver.Vessels.DeleteAsync(vesselId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyMergeEntriesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? entryId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("merge-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "merge-round-trip", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "merge-round-trip", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "merge-round-trip", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "merge-round-trip", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "merge-round-trip", token).ConfigureAwait(false);
                Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "merge-round-trip", token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);

                // Sub-second digits and a date far from the host's daylight-saving rules make a host-offset read visible.
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                MergeEntry entry = new MergeEntry("armada/merge-" + suffix + "-ユニコード", "release-" + suffix)
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    MissionId = mission.Id,
                    VesselId = vessel.Id,
                    Status = MergeStatusEnum.Failed,
                    Priority = 9,
                    BatchId = "batch-" + suffix,
                    TestCommand = "dotnet test ユニコード",
                    TestOutput = "Test output ユニコード",
                    TestExitCode = 3,
                    CreatedUtc = baseUtc.AddMinutes(-5),
                    TestStartedUtc = baseUtc.AddMinutes(-2),
                    CompletedUtc = baseUtc,
                    AuditLane = "Deep",
                    AuditConventionPassed = false,
                    AuditConventionNotes = "Convention notes ユニコード",
                    AuditCriticalTrigger = "critical-path",
                    AuditDeepPicked = true,
                    AuditDeepCompletedUtc = baseUtc.AddMinutes(1),
                    AuditDeepVerdict = "Concern",
                    AuditDeepNotes = "Deep notes ユニコード",
                    AuditDeepRecommendedAction = "Revert",
                    PrUrl = "https://example.test/pull/" + suffix,
                    PrBaseBranch = "base-" + suffix,
                    MergeFailureClass = MergeFailureClassEnum.TestFailureAfterMerge,
                    ConflictedFiles = "[\"src/a.cs\",\"src/b.cs\"]",
                    MergeFailureSummary = "Failure summary ユニコード",
                    DiffLineCount = 1234
                };
                MergeEntry created = await _Driver.MergeEntries.CreateAsync(entry, token).ConfigureAwait(false);
                entryId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.MergeEntries.ReadAsync(created.Id, token).ConfigureAwait(false), "MergeEntry");
                DatabaseAssert.AllProperties(created, await _Driver.MergeEntries.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "MergeEntry by tenant");
                DatabaseAssert.AllProperties(created, await _Driver.MergeEntries.ReadAsync(tenant.Id, user.Id, created.Id, token).ConfigureAwait(false), "MergeEntry by tenant and user");
                List<MergeEntry> byStatus = await _Driver.MergeEntries.EnumerateByStatusAsync(tenant.Id, MergeStatusEnum.Failed, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(created, byStatus.Find(item => item.Id == created.Id), "MergeEntry by status");

                created.MissionId = null;
                created.Status = MergeStatusEnum.Queued;
                created.Priority = 0;
                created.BatchId = null;
                created.TestCommand = null;
                created.TestOutput = null;
                created.TestExitCode = null;
                created.TestStartedUtc = null;
                created.CompletedUtc = null;
                created.AuditLane = null;
                created.AuditConventionPassed = null;
                created.AuditConventionNotes = null;
                created.AuditCriticalTrigger = null;
                created.AuditDeepPicked = null;
                created.AuditDeepCompletedUtc = null;
                created.AuditDeepVerdict = null;
                created.AuditDeepNotes = null;
                created.AuditDeepRecommendedAction = null;
                created.PrUrl = null;
                created.PrBaseBranch = null;
                created.MergeFailureClass = null;
                created.ConflictedFiles = null;
                created.MergeFailureSummary = null;
                created.DiffLineCount = 0;
                MergeEntry updated = await _Driver.MergeEntries.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.MergeEntries.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened MergeEntry");
                }
            }
            finally
            {
                if (entryId != null && !_NoCleanup) await _Driver.MergeEntries.DeleteAsync(entryId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyObjectivesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? objectiveId = null;
            string? pipelineId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("objective-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "objective-round-trip", token: token).ConfigureAwait(false);
                Objective parent = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "objective-round-trip-parent", token: token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                Pipeline pipeline = await _Driver.Pipelines.CreateAsync(new Pipeline
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Name = "ObjectiveRoundTripPipeline" + suffix,
                    Stages = new List<PipelineStage> { new PipelineStage { Order = 1, PersonaName = "Worker" } }
                }, token).ConfigureAwait(false);
                pipelineId = pipeline.Id;

                // Sub-second digits and a date far from the host's daylight-saving rules make a host-offset read visible.
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                Objective objective = new Objective
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Title = "Round-trip objective ユニコード",
                    Description = "Objective description ユニコード",
                    Status = ObjectiveStatusEnum.Blocked,
                    AutoDispatchEnabled = true,
                    Kind = ObjectiveKindEnum.Research,
                    Category = "Category " + suffix,
                    Priority = ObjectivePriorityEnum.P0,
                    Rank = 42,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                    Effort = ObjectiveEffortEnum.XL,
                    Owner = "owner-" + suffix,
                    TargetVersion = "9.8.7",
                    DueUtc = baseUtc.AddDays(3),
                    ParentObjectiveId = parent.Id,
                    BlockedByObjectiveIds = new List<string> { parent.Id },
                    RefinementSummary = "Refinement summary ユニコード",
                    Preparation = new ObjectivePreparation
                    {
                        Source = new ObjectivePreparationAnchor { Ref = "refs/heads/source-" + suffix, ResolvedCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
                    },
                    SuggestedPipelineId = pipeline.Id,
                    StartFromRef = "refs/heads/start-" + suffix,
                    SuggestedPlaybooks = new List<SelectedPlaybook> { new SelectedPlaybook { PlaybookId = "pbk_" + suffix } },
                    RefinementSessionIds = new List<string> { "ors_" + suffix },
                    Tags = new List<string> { "tag-ユニコード" },
                    AcceptanceCriteria = new List<string> { "criterion" },
                    NonGoals = new List<string> { "non-goal" },
                    RolloutConstraints = new List<string> { "constraint" },
                    EvidenceLinks = new List<string> { "https://example.test/evidence" },
                    FleetIds = new List<string> { "flt_" + suffix },
                    VesselIds = new List<string> { "vsl_" + suffix },
                    PlanningSessionIds = new List<string> { "pls_" + suffix },
                    VoyageIds = new List<string> { "vyg_" + suffix },
                    MissionIds = new List<string> { "msn_" + suffix },
                    CheckRunIds = new List<string> { "chk_" + suffix },
                    ReleaseIds = new List<string> { "rel_" + suffix },
                    DeploymentIds = new List<string> { "dpl_" + suffix },
                    IncidentIds = new List<string> { "inc_" + suffix },
                    SourceProvider = "provider",
                    SourceType = "issue",
                    SourceId = "source-" + suffix,
                    SourceUrl = "https://example.test/source/" + suffix,
                    SourceUpdatedUtc = baseUtc.AddMinutes(-1),
                    CreatedUtc = baseUtc.AddMinutes(-5),
                    LastUpdateUtc = baseUtc.AddMinutes(-4),
                    CompletedUtc = baseUtc
                };
                Objective created = await _Driver.Objectives.CreateAsync(objective, token).ConfigureAwait(false);
                objectiveId = created.Id;
                DatabaseAssert.AllProperties(created, await _Driver.Objectives.ReadAsync(created.Id, token).ConfigureAwait(false), "Objective");
                DatabaseAssert.AllProperties(created, await _Driver.Objectives.ReadAsync(tenant.Id, created.Id, token).ConfigureAwait(false), "Objective by tenant");
                DatabaseAssert.AllProperties(created, await _Driver.Objectives.ReadAsync(tenant.Id, user.Id, created.Id, token).ConfigureAwait(false), "Objective by tenant and user");
                List<Objective> listed = await _Driver.Objectives.EnumerateAsync(tenant.Id, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(created, listed.Find(item => item.Id == created.Id), "Enumerated Objective");

                created.Description = null;
                created.Status = ObjectiveStatusEnum.Draft;
                created.AutoDispatchEnabled = false;
                created.Kind = ObjectiveKindEnum.Feature;
                created.Category = null;
                created.Priority = ObjectivePriorityEnum.P2;
                created.Rank = 0;
                created.BacklogState = ObjectiveBacklogStateEnum.Inbox;
                created.Effort = ObjectiveEffortEnum.M;
                created.Owner = null;
                created.TargetVersion = null;
                created.DueUtc = null;
                created.ParentObjectiveId = null;
                created.BlockedByObjectiveIds = new List<string>();
                created.RefinementSummary = null;
                created.Preparation = new ObjectivePreparation();
                created.SuggestedPipelineId = null;
                created.StartFromRef = null;
                created.SuggestedPlaybooks = new List<SelectedPlaybook>();
                created.RefinementSessionIds = new List<string>();
                created.Tags = new List<string>();
                created.SourceProvider = null;
                created.SourceType = null;
                created.SourceId = null;
                created.SourceUrl = null;
                created.SourceUpdatedUtc = null;
                created.CompletedUtc = null;
                Objective updated = await _Driver.Objectives.UpdateAsync(created, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.Objectives.ReadAsync(created.Id, token).ConfigureAwait(false), "Reopened Objective");
                }
            }
            finally
            {
                if (objectiveId != null && !_NoCleanup) await _Driver.Objectives.DeleteAsync(objectiveId, token).ConfigureAwait(false);
                if (pipelineId != null && !_NoCleanup) await _Driver.Pipelines.DeleteAsync(pipelineId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyObjectiveRefinementsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? sessionId = null;
            string? messageId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("refinement-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "refinement-round-trip", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "refinement-round-trip", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "refinement-round-trip", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "refinement-round-trip", token).ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "refinement-round-trip", token: token).ConfigureAwait(false);

                // Sub-second digits and a date far from the host's daylight-saving rules make a host-offset read visible.
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                ObjectiveRefinementSession session = new ObjectiveRefinementSession
                {
                    ObjectiveId = objective.Id,
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    CaptainId = captain.Id,
                    FleetId = fleet.Id,
                    VesselId = vessel.Id,
                    Title = "Refinement ユニコード",
                    Status = ObjectiveRefinementSessionStatusEnum.Failed,
                    ProcessId = 424242,
                    FailureReason = "Failure reason ユニコード",
                    CreatedUtc = baseUtc.AddMinutes(-5),
                    StartedUtc = baseUtc.AddMinutes(-4),
                    CompletedUtc = baseUtc,
                    LastUpdateUtc = baseUtc.AddMinutes(1)
                };
                ObjectiveRefinementSession createdSession = await _Driver.ObjectiveRefinementSessions.CreateAsync(session, token).ConfigureAwait(false);
                sessionId = createdSession.Id;
                DatabaseAssert.AllProperties(createdSession, await _Driver.ObjectiveRefinementSessions.ReadAsync(createdSession.Id, token).ConfigureAwait(false), "ObjectiveRefinementSession");
                DatabaseAssert.AllProperties(createdSession, await _Driver.ObjectiveRefinementSessions.ReadAsync(tenant.Id, user.Id, createdSession.Id, token).ConfigureAwait(false), "ObjectiveRefinementSession by tenant and user");
                List<ObjectiveRefinementSession> byObjective = await _Driver.ObjectiveRefinementSessions.EnumerateByObjectiveAsync(objective.Id, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(createdSession, byObjective.Find(item => item.Id == createdSession.Id), "ObjectiveRefinementSession by objective");

                ObjectiveRefinementMessage message = new ObjectiveRefinementMessage
                {
                    ObjectiveRefinementSessionId = createdSession.Id,
                    ObjectiveId = objective.Id,
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Role = "Assistant",
                    Sequence = 7,
                    Content = "Message content ユニコード",
                    IsSelected = true,
                    CreatedUtc = baseUtc.AddMinutes(-3),
                    LastUpdateUtc = baseUtc.AddMinutes(-2)
                };
                ObjectiveRefinementMessage createdMessage = await _Driver.ObjectiveRefinementMessages.CreateAsync(message, token).ConfigureAwait(false);
                messageId = createdMessage.Id;
                DatabaseAssert.AllProperties(createdMessage, await _Driver.ObjectiveRefinementMessages.ReadAsync(createdMessage.Id, token).ConfigureAwait(false), "ObjectiveRefinementMessage");
                List<ObjectiveRefinementMessage> bySession = await _Driver.ObjectiveRefinementMessages.EnumerateBySessionAsync(createdSession.Id, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(createdMessage, bySession.Find(item => item.Id == createdMessage.Id), "ObjectiveRefinementMessage by session");

                createdSession.FleetId = null;
                createdSession.VesselId = null;
                createdSession.Status = ObjectiveRefinementSessionStatusEnum.Created;
                createdSession.ProcessId = null;
                createdSession.FailureReason = null;
                createdSession.StartedUtc = null;
                createdSession.CompletedUtc = null;
                ObjectiveRefinementSession updatedSession = await _Driver.ObjectiveRefinementSessions.UpdateAsync(createdSession, token).ConfigureAwait(false);
                createdMessage.Sequence = 0;
                createdMessage.Content = String.Empty;
                createdMessage.IsSelected = false;
                ObjectiveRefinementMessage updatedMessage = await _Driver.ObjectiveRefinementMessages.UpdateAsync(createdMessage, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updatedSession, await reopened.ObjectiveRefinementSessions.ReadAsync(createdSession.Id, token).ConfigureAwait(false), "Reopened ObjectiveRefinementSession");
                    DatabaseAssert.AllProperties(updatedMessage, await reopened.ObjectiveRefinementMessages.ReadAsync(createdMessage.Id, token).ConfigureAwait(false), "Reopened ObjectiveRefinementMessage");
                }
            }
            finally
            {
                if (messageId != null && !_NoCleanup) await _Driver.ObjectiveRefinementMessages.DeleteAsync(messageId, token).ConfigureAwait(false);
                if (sessionId != null && !_NoCleanup) await _Driver.ObjectiveRefinementSessions.DeleteAsync(sessionId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyPlanningSessionsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? sessionId = null;
            string? messageId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("planning-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "planning-round-trip", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "planning-round-trip", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "planning-round-trip", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "planning-round-trip", token).ConfigureAwait(false);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);

                // Sub-second digits and a date far from the host's daylight-saving rules make a host-offset read visible.
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                PlanningSession session = new PlanningSession
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    CaptainId = captain.Id,
                    VesselId = vessel.Id,
                    FleetId = fleet.Id,
                    DockId = "dck_planning_" + suffix,
                    BranchName = "armada/planning-" + suffix,
                    Title = "Planning ユニコード",
                    Status = PlanningSessionStatusEnum.Failed,
                    PipelineId = "ppl_planning_" + suffix,
                    ObjectiveId = "obj_planning_" + suffix,
                    SelectedPlaybooks = new List<SelectedPlaybook> { new SelectedPlaybook { PlaybookId = "pbk_" + suffix, DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference } },
                    ProcessId = 424242,
                    FailureReason = "Failure reason ユニコード",
                    CreatedUtc = baseUtc.AddMinutes(-5),
                    StartedUtc = baseUtc.AddMinutes(-4),
                    CompletedUtc = baseUtc,
                    LastUpdateUtc = baseUtc.AddMinutes(1)
                };
                PlanningSession createdSession = await _Driver.PlanningSessions.CreateAsync(session, token).ConfigureAwait(false);
                sessionId = createdSession.Id;
                DatabaseAssert.AllProperties(createdSession, await _Driver.PlanningSessions.ReadAsync(createdSession.Id, token).ConfigureAwait(false), "PlanningSession");
                DatabaseAssert.AllProperties(createdSession, await _Driver.PlanningSessions.ReadAsync(tenant.Id, user.Id, createdSession.Id, token).ConfigureAwait(false), "PlanningSession by tenant and user");
                List<PlanningSession> byCaptain = await _Driver.PlanningSessions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(createdSession, byCaptain.Find(item => item.Id == createdSession.Id), "PlanningSession by captain");

                PlanningSessionMessage message = new PlanningSessionMessage
                {
                    PlanningSessionId = createdSession.Id,
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Role = "Assistant",
                    Sequence = 7,
                    Content = "Message content ユニコード",
                    IsSelectedForDispatch = true,
                    CreatedUtc = baseUtc.AddMinutes(-3),
                    LastUpdateUtc = baseUtc.AddMinutes(-2)
                };
                PlanningSessionMessage createdMessage = await _Driver.PlanningSessionMessages.CreateAsync(message, token).ConfigureAwait(false);
                messageId = createdMessage.Id;
                DatabaseAssert.AllProperties(createdMessage, await _Driver.PlanningSessionMessages.ReadAsync(createdMessage.Id, token).ConfigureAwait(false), "PlanningSessionMessage");
                List<PlanningSessionMessage> bySession = await _Driver.PlanningSessionMessages.EnumerateBySessionAsync(createdSession.Id, token).ConfigureAwait(false);
                DatabaseAssert.AllProperties(createdMessage, bySession.Find(item => item.Id == createdMessage.Id), "PlanningSessionMessage by session");

                createdSession.FleetId = null;
                createdSession.DockId = null;
                createdSession.BranchName = null;
                createdSession.Status = PlanningSessionStatusEnum.Created;
                createdSession.PipelineId = null;
                createdSession.ObjectiveId = null;
                createdSession.SelectedPlaybooks = new List<SelectedPlaybook>();
                createdSession.ProcessId = null;
                createdSession.FailureReason = null;
                createdSession.StartedUtc = null;
                createdSession.CompletedUtc = null;
                PlanningSession updatedSession = await _Driver.PlanningSessions.UpdateAsync(createdSession, token).ConfigureAwait(false);
                createdMessage.Sequence = 0;
                createdMessage.Content = String.Empty;
                createdMessage.IsSelectedForDispatch = false;
                PlanningSessionMessage updatedMessage = await _Driver.PlanningSessionMessages.UpdateAsync(createdMessage, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updatedSession, await reopened.PlanningSessions.ReadAsync(createdSession.Id, token).ConfigureAwait(false), "Reopened PlanningSession");
                    DatabaseAssert.AllProperties(updatedMessage, await reopened.PlanningSessionMessages.ReadAsync(createdMessage.Id, token).ConfigureAwait(false), "Reopened PlanningSessionMessage");
                }
            }
            finally
            {
                if (messageId != null && !_NoCleanup) await _Driver.PlanningSessionMessages.DeleteAsync(messageId, token).ConfigureAwait(false);
                if (sessionId != null && !_NoCleanup) await _Driver.PlanningSessions.DeleteAsync(sessionId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyCoordinationLeasesAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string name = "round-trip-lease-" + Guid.NewGuid().ToString("N").Substring(0, 12) + "-ユニコード";
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("lease-round-trip", token: token).ConfigureAwait(false);
                DateTime before = DateTime.UtcNow.AddSeconds(-1);
                DatabaseAssert.True(await _Driver.CoordinationLeases.TryAcquireAsync(name, "holder-ユニコード", TimeSpan.FromMinutes(7), tenant.Id, token).ConfigureAwait(false), "Acquire lease");
                CoordinationLease acquired = DatabaseAssert.NotNull(await _Driver.CoordinationLeases.ReadAsync(name, token).ConfigureAwait(false), "Lease read");
                DatabaseAssert.Equal(name, acquired.Name, "CoordinationLease.Name");
                DatabaseAssert.Equal("holder-ユニコード", acquired.Holder, "CoordinationLease.Holder");
                DatabaseAssert.Equal(tenant.Id, acquired.TenantId, "CoordinationLease.TenantId");
                DatabaseAssert.True(acquired.AcquiredUtc.Kind == DateTimeKind.Utc && acquired.AcquiredUtc >= before && acquired.AcquiredUtc <= DateTime.UtcNow.AddSeconds(1),
                    "Lease acquisition reads back as the UTC instant it was written, got " + acquired.AcquiredUtc.ToString("O"));
                DatabaseAssert.UtcInstant(acquired.AcquiredUtc.AddMinutes(7), acquired.ExpiresUtc, "CoordinationLease.ExpiresUtc is the acquisition plus the time to live");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(acquired, await reopened.CoordinationLeases.ReadAsync(name, token).ConfigureAwait(false), "Reopened CoordinationLease");
                }
            }
            finally
            {
                if (!_NoCleanup) await _Driver.CoordinationLeases.ReleaseAsync(name, "holder-ユニコード", token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyMissionHistoryPointsAndVoyagePlaybooksAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            List<string> playbookIds = new List<string>();
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("projection-round-trip", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "projection-round-trip", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "projection-round-trip", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "projection-round-trip", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "projection-round-trip", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "projection-round-trip", token).ConfigureAwait(false);

                // Sub-second digits and a date far from the host's daylight-saving rules make a host-offset read visible.
                DateTime baseUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                Mission first = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "projection-first", token,
                    configure: item => { item.CreatedUtc = baseUtc; item.Status = MissionStatusEnum.Failed; }).ConfigureAwait(false);
                Mission second = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "projection-second", token,
                    configure: item => { item.CreatedUtc = baseUtc.AddMinutes(1); item.Status = MissionStatusEnum.Complete; }).ConfigureAwait(false);
                List<MissionHistoryPoint> points = await _Driver.Missions.EnumerateHistoryPointsAsync(tenant.Id,
                    new MissionHistoryQuery { FromUtc = baseUtc.AddHours(-1), ToUtc = baseUtc.AddHours(1), VesselId = vessel.Id }, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2, points.Count, "History points in the window");
                Mission[] expected = new[] { first, second };
                for (int i = 0; i < expected.Length; i++)
                {
                    DatabaseAssert.UtcInstant(expected[i].CreatedUtc, points[i].CreatedUtc, "MissionHistoryPoint[" + i + "].CreatedUtc");
                    DatabaseAssert.Equal(expected[i].Status, points[i].Status, "MissionHistoryPoint[" + i + "].Status");
                    DatabaseAssert.Equal(vessel.Id, points[i].VesselId, "MissionHistoryPoint[" + i + "].VesselId");
                }

                List<SelectedPlaybook> selections = new List<SelectedPlaybook>();
                foreach (PlaybookDeliveryModeEnum mode in new[] { PlaybookDeliveryModeEnum.AttachIntoWorktree, PlaybookDeliveryModeEnum.InstructionWithReference })
                {
                    Playbook playbook = await _Driver.Playbooks.CreateAsync(new Playbook
                    {
                        TenantId = tenant.Id,
                        UserId = user.Id,
                        FileName = "projection-" + mode + "-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".md",
                        Content = "# Playbook"
                    }, token).ConfigureAwait(false);
                    playbookIds.Add(playbook.Id);
                    selections.Add(new SelectedPlaybook { PlaybookId = playbook.Id, DeliveryMode = mode });
                }

                await _Driver.Playbooks.SetVoyageSelectionsAsync(voyage.Id, selections, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    List<SelectedPlaybook> read = await reopened.Playbooks.GetVoyageSelectionsAsync(voyage.Id, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(selections.Count, read.Count, "Voyage playbook selections");
                    for (int i = 0; i < selections.Count; i++)
                        DatabaseAssert.AllProperties(selections[i], read[i], "Voyage playbook selection[" + i + "]");
                }
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
                if (!_NoCleanup) foreach (string id in playbookIds) await _Driver.Playbooks.DeleteAsync(id, token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyDamagedDeliveryJsonIsNamedAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? endpointId = null;
            try
            {
                DeliveryGraph graph = await CreateDeliveryGraphAsync(fixture, "damaged-delivery-json", token).ConfigureAwait(false);
                DeploymentEnvironment environment = await fixture.CreateDeploymentEnvironmentAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, "damaged-delivery-json", token: token).ConfigureAwait(false);
                Release release = await fixture.CreateReleaseAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, checkRunIds: new[] { graph.CheckRun.Id }, token: token).ConfigureAwait(false);
                Deployment deployment = await fixture.CreateDeploymentAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, environment.Id, environment.Name, token: token).ConfigureAwait(false);
                string damagedEndpointId = "mep_damaged_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                endpointId = damagedEndpointId;
                await _Driver.ModelEndpoints.CreateAsync(new ModelEndpoint
                {
                    Id = damagedEndpointId,
                    TenantId = graph.Tenant.Id,
                    Name = "Damaged history endpoint " + damagedEndpointId,
                    BaseUrl = "http://localhost:9999/v1"
                }, token).ConfigureAwait(false);

                // A damaged document is reported with its entity and column. Reading it as empty would let the
                // next update of the record write the empty value over the stored one. Every entity is checked
                // before the case fails, so one run names every reader that swallows a damaged document.
                List<string> problems = new List<string>();
                await ExpectDamagedAsync(problems, "check_runs", "artifacts_json", "CheckRun", graph.CheckRun.Id,
                    () => _Driver.CheckRuns.ReadAsync(graph.CheckRun.Id, null, token), token).ConfigureAwait(false);
                await ExpectDamagedAsync(problems, "workflow_profiles", "environments_json", "WorkflowProfile", graph.Profile.Id,
                    () => _Driver.WorkflowProfiles.ReadAsync(graph.Profile.Id, null, token), token).ConfigureAwait(false);
                await ExpectDamagedAsync(problems, "environments", "verification_definitions_json", "DeploymentEnvironment", environment.Id,
                    () => _Driver.Environments.ReadAsync(environment.Id, null, token), token).ConfigureAwait(false);
                await ExpectDamagedAsync(problems, "releases", "check_run_ids_json", "Release", release.Id,
                    () => _Driver.Releases.ReadAsync(release.Id, null, token), token).ConfigureAwait(false);
                await ExpectDamagedAsync(problems, "deployments", "check_run_ids_json", "Deployment", deployment.Id,
                    () => _Driver.Deployments.ReadAsync(deployment.Id, null, token), token).ConfigureAwait(false);
                await ExpectDamagedAsync(problems, "model_endpoints", "health_history_json", "ModelEndpoint", damagedEndpointId,
                    () => _Driver.ModelEndpoints.ReadAsync(damagedEndpointId, token), token).ConfigureAwait(false);
                DatabaseAssert.True(problems.Count == 0, String.Join("; ", problems));
            }
            finally
            {
                if (endpointId != null && !_NoCleanup) await _Driver.ModelEndpoints.DeleteAsync(endpointId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task ExpectDamagedAsync<T>(List<string> problems, string table, string column, string entity, string id, Func<Task<T>> read, CancellationToken token)
        {
            await ExecuteForIdAsync("UPDATE " + table + " SET " + column + " = '[\"damaged\"' WHERE id = @id;", id, token).ConfigureAwait(false);
            string? failure = null;
            try
            {
                await read().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            if (failure == null)
                problems.Add("a damaged " + table + "." + column + " document read as empty instead of failing");
            else if (!failure.Contains(entity) || !failure.Contains(column))
                problems.Add("the " + table + "." + column + " failure does not name the entity and column: " + failure);
        }

        private async Task<DeliveryGraph> CreateDeliveryGraphAsync(DatabaseFixture fixture, string prefix, CancellationToken token)
        {
            DeliveryGraph graph = new DeliveryGraph();
            graph.Tenant = await fixture.CreateTenantAsync(prefix, token: token).ConfigureAwait(false);
            graph.User = await fixture.CreateUserAsync(graph.Tenant.Id, prefix, token: token).ConfigureAwait(false);
            Fleet fleet = await fixture.CreateFleetAsync(graph.Tenant.Id, graph.User.Id, prefix, token).ConfigureAwait(false);
            graph.Vessel = await fixture.CreateVesselAsync(graph.Tenant.Id, graph.User.Id, fleet.Id, prefix, token).ConfigureAwait(false);
            graph.Captain = await fixture.CreateCaptainAsync(graph.Tenant.Id, graph.User.Id, prefix, token).ConfigureAwait(false);
            graph.Voyage = await fixture.CreateVoyageAsync(graph.Tenant.Id, graph.User.Id, prefix, token).ConfigureAwait(false);
            graph.Mission = await fixture.CreateMissionAsync(graph.Tenant.Id, graph.User.Id, graph.Voyage.Id, graph.Vessel.Id, graph.Captain.Id, prefix, token).ConfigureAwait(false);
            graph.Profile = await fixture.CreateWorkflowProfileAsync(graph.Tenant.Id, graph.User.Id, prefix, token: token).ConfigureAwait(false);
            graph.CheckRun = await fixture.CreateCheckRunAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, graph.Profile.Id, token: token).ConfigureAwait(false);
            return graph;
        }

        private sealed class DeliveryGraph
        {
            internal TenantMetadata Tenant { get; set; } = null!;
            internal UserMaster User { get; set; } = null!;
            internal Vessel Vessel { get; set; } = null!;
            internal Captain Captain { get; set; } = null!;
            internal Voyage Voyage { get; set; } = null!;
            internal Mission Mission { get; set; } = null!;
            internal WorkflowProfile Profile { get; set; } = null!;
            internal CheckRun CheckRun { get; set; } = null!;
        }

        internal async Task VerifyDamagedJsonIsNamedAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? profileId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("damaged-json-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "damaged-json-user", token: token).ConfigureAwait(false);
                ProjectProfile profile = new ProjectProfile
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Name = "damaged-json-profile-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Skills = new List<string> { "kept-skill" }
                };
                ProjectProfile created = await _Driver.ProjectProfiles.CreateAsync(profile, token).ConfigureAwait(false);
                profileId = created.Id;
                await ExecuteForIdAsync("UPDATE project_profiles SET skills_json = '[\"kept-skill\"' WHERE id = @id;", created.Id, token).ConfigureAwait(false);

                // A damaged document is reported with its entity and column. Reading it as an empty list would
                // let the next update of the profile write the empty list over the stored skills.
                string? failure = null;
                ProjectProfile? read = null;
                try
                {
                    read = await _Driver.ProjectProfiles.ReadAsync(created.Id, new ProjectProfileQuery { TenantId = tenant.Id }, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }

                DatabaseAssert.True(failure != null,
                    "A damaged skills document must fail the read, but it read as " + (read == null ? "<null>" : read.Skills.Count + " skills"));
                DatabaseAssert.True(failure!.Contains("ProjectProfile") && failure.Contains("skills_json"),
                    "The failure names the entity and column: " + failure);
            }
            finally
            {
                if (profileId != null && !_NoCleanup) await _Driver.ProjectProfiles.DeleteAsync(profileId, null, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task ExecuteForIdAsync(string sql, string id, CancellationToken token)
        {
            using (System.Data.Common.DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (System.Data.Common.DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    System.Data.Common.DbParameter parameter = command.CreateParameter();
                    parameter.ParameterName = "@id";
                    parameter.Value = id;
                    command.Parameters.Add(parameter);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }
    }
}
