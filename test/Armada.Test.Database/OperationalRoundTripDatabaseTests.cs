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
    }
}
