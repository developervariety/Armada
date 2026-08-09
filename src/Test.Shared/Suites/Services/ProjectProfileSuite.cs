namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the project-profile foundation: the <see cref="ProjectProfile"/> model and its
    /// <see cref="PersonaOverride"/> children, <see cref="ProjectProfileService"/> validation and layered
    /// (vessel -> fleet -> global) resolution, and SQLite persistence round-trips. Positive cases assert
    /// correct defaults, resolution precedence, and persistence fidelity; negative cases assert rejection
    /// of invalid scope/name combinations, malformed persona overrides, and constructor misuse.
    /// </summary>
    public sealed class ProjectProfileSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.ProjectProfile";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Project Profile suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // --- Model ---

            cases.Add(Case("model_defaults_are_correct", "ProjectProfile Defaults AreCorrect", TestTags.Positive, () =>
            {
                ProjectProfile profile = new ProjectProfile();
                AssertTrue(profile.Id.StartsWith(Constants.ProjectProfileIdPrefix), "Id should use ppf_ prefix");
                AssertEqual(ProjectProfileScopeEnum.Global, profile.Scope);
                AssertTrue(profile.Active, "Active should default true");
                AssertFalse(profile.IsDefault, "IsDefault should default false");
                AssertNotNull(profile.PersonaOverrides);
                AssertEqual(0, profile.PersonaOverrides.Count);
                AssertNotNull(profile.Skills);
                AssertNull(profile.DefaultPipelineId);
                AssertNull(profile.WorkflowProfileId);
            }));

            cases.Add(Case("model_name_empty_throws", "ProjectProfile SetName Empty Throws", TestTags.Negative, () =>
            {
                ProjectProfile profile = new ProjectProfile();
                AssertThrows<ArgumentNullException>(() => profile.Name = "");
                AssertThrows<ArgumentNullException>(() => profile.Name = "   ");
            }));

            cases.Add(Case("model_id_empty_throws", "ProjectProfile SetId Empty Throws", TestTags.Negative, () =>
            {
                ProjectProfile profile = new ProjectProfile();
                AssertThrows<ArgumentNullException>(() => profile.Id = "");
            }));

            cases.Add(Case("persona_override_name_empty_throws", "PersonaOverride SetPersonaName Empty Throws", TestTags.Negative, () =>
            {
                PersonaOverride ovr = new PersonaOverride();
                AssertThrows<ArgumentNullException>(() => ovr.PersonaName = "");
            }));

            cases.Add(Case("persona_override_defaults_enabled", "PersonaOverride Defaults Enabled", TestTags.Positive, () =>
            {
                PersonaOverride ovr = new PersonaOverride();
                ovr.PersonaName = "  Architect  ";
                AssertEqual("Architect", ovr.PersonaName);
                AssertTrue(ovr.Enabled);
                AssertNull(ovr.PromptTemplateName);
            }));

            // --- Service constructor guards ---

            cases.Add(Case("service_null_database_throws", "ProjectProfileService NullDatabase Throws", TestTags.Negative, () =>
            {
                AssertThrows<ArgumentNullException>(() => new ProjectProfileService(null!, CreateLogging()));
            }));

            // --- Validation ---

            cases.Add(CaseAsync("validate_valid_global_profile_passes", "Validate ValidGlobalProfile Passes", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ProjectProfileService service = new ProjectProfileService(testDb.Driver, CreateLogging());
                    ProjectProfile profile = new ProjectProfile();
                    profile.Name = "Backend Default";
                    ProjectProfileValidationResult result = await service.ValidateAsync(profile);
                    AssertTrue(result.IsValid, "Global profile with a name should be valid");
                    AssertEqual(0, result.Errors.Count);
                }
            }));

            cases.Add(CaseAsync("validate_vessel_scope_without_vessel_id_fails", "Validate VesselScopeWithoutVesselId Fails", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ProjectProfileService service = new ProjectProfileService(testDb.Driver, CreateLogging());
                    ProjectProfile profile = new ProjectProfile();
                    profile.Name = "Bad Vessel Profile";
                    profile.Scope = ProjectProfileScopeEnum.Vessel;
                    ProjectProfileValidationResult result = await service.ValidateAsync(profile);
                    AssertFalse(result.IsValid, "Vessel-scoped profile without vesselId should be invalid");
                }
            }));

            cases.Add(CaseAsync("validate_duplicate_persona_overrides_fails", "Validate DuplicatePersonaOverrides Fails", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ProjectProfileService service = new ProjectProfileService(testDb.Driver, CreateLogging());
                    ProjectProfile profile = new ProjectProfile();
                    profile.Name = "Dup Overrides";
                    profile.PersonaOverrides.Add(new PersonaOverride { PersonaName = "Architect" });
                    profile.PersonaOverrides.Add(new PersonaOverride { PersonaName = "architect" });
                    ProjectProfileValidationResult result = await service.ValidateAsync(profile);
                    AssertFalse(result.IsValid, "Duplicate persona overrides (case-insensitive) should be invalid");
                }
            }));

            // --- Persistence round-trip ---

            cases.Add(CaseAsync("persistence_create_read_update_delete", "Persistence CreateReadUpdateDelete RoundTrip", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ProjectProfile profile = new ProjectProfile();
                    profile.Name = "Round Trip";
                    profile.Description = "desc";
                    profile.DefaultPipelineId = "ppl_test";
                    profile.WorkflowProfileId = "wfp_test";
                    profile.PersonaOverrides.Add(new PersonaOverride { PersonaName = "Worker", PromptTemplateName = "persona.worker.custom" });
                    profile.Skills.Add("dotnet");

                    await db.ProjectProfiles.CreateAsync(profile);

                    ProjectProfile? read = await db.ProjectProfiles.ReadAsync(profile.Id);
                    AssertNotNull(read);
                    AssertEqual("Round Trip", read!.Name);
                    AssertEqual("ppl_test", read.DefaultPipelineId);
                    AssertEqual("wfp_test", read.WorkflowProfileId);
                    AssertEqual(1, read.PersonaOverrides.Count);
                    AssertEqual("Worker", read.PersonaOverrides[0].PersonaName);
                    AssertEqual("persona.worker.custom", read.PersonaOverrides[0].PromptTemplateName);
                    AssertEqual(1, read.Skills.Count);
                    AssertEqual("dotnet", read.Skills[0]);

                    read.Name = "Updated";
                    read.Skills.Add("sqlite");
                    await db.ProjectProfiles.UpdateAsync(read);

                    ProjectProfile? reread = await db.ProjectProfiles.ReadAsync(profile.Id);
                    AssertNotNull(reread);
                    AssertEqual("Updated", reread!.Name);
                    AssertEqual(2, reread.Skills.Count);

                    await db.ProjectProfiles.DeleteAsync(profile.Id);
                    ProjectProfile? gone = await db.ProjectProfiles.ReadAsync(profile.Id);
                    AssertNull(gone);
                }
            }));

            cases.Add(CaseAsync("enumerate_filters_by_scope", "Enumerate FiltersByScope", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    await db.ProjectProfiles.CreateAsync(new ProjectProfile { Name = "G", Scope = ProjectProfileScopeEnum.Global });
                    await db.ProjectProfiles.CreateAsync(new ProjectProfile { Name = "V", Scope = ProjectProfileScopeEnum.Vessel, VesselId = "vsl_x" });

                    EnumerationResult<ProjectProfile> vesselOnly = await db.ProjectProfiles.EnumerateAsync(new ProjectProfileQuery
                    {
                        Scope = ProjectProfileScopeEnum.Vessel
                    });
                    AssertEqual(1, (int)vesselOnly.TotalRecords);
                    AssertEqual("V", vesselOnly.Objects[0].Name);
                }
            }));

            // --- Layered resolution ---

            cases.Add(CaseAsync("resolve_prefers_vessel_over_fleet_and_global", "Resolve PrefersVesselOverFleetAndGlobal", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ProjectProfileService service = new ProjectProfileService(db, CreateLogging());

                    Fleet fleet = new Fleet("Fleet A");
                    await db.Fleets.CreateAsync(fleet);
                    Vessel vessel = new Vessel("Vessel A", "https://github.com/test/a");
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    await db.ProjectProfiles.CreateAsync(new ProjectProfile { Name = "Global", Scope = ProjectProfileScopeEnum.Global });
                    await db.ProjectProfiles.CreateAsync(new ProjectProfile { Name = "Fleet", Scope = ProjectProfileScopeEnum.Fleet, FleetId = fleet.Id });
                    await db.ProjectProfiles.CreateAsync(new ProjectProfile { Name = "Vessel", Scope = ProjectProfileScopeEnum.Vessel, VesselId = vessel.Id });

                    AuthContext auth = new AuthContext { IsAdmin = true };
                    ProjectProfileResolutionResult resolved = await service.ResolveWithModeForVesselAsync(auth, vessel);
                    AssertNotNull(resolved.Profile);
                    AssertEqual("Vessel", resolved.Profile!.Name);
                    AssertEqual(ProjectProfileResolutionModeEnum.Vessel, resolved.Mode);
                }
            }));

            cases.Add(CaseAsync("resolve_falls_back_to_fleet_then_global", "Resolve FallsBackToFleetThenGlobal", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ProjectProfileService service = new ProjectProfileService(db, CreateLogging());

                    Fleet fleet = new Fleet("Fleet B");
                    await db.Fleets.CreateAsync(fleet);
                    Vessel vessel = new Vessel("Vessel B", "https://github.com/test/b");
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    await db.ProjectProfiles.CreateAsync(new ProjectProfile { Name = "Global", Scope = ProjectProfileScopeEnum.Global });
                    await db.ProjectProfiles.CreateAsync(new ProjectProfile { Name = "Fleet", Scope = ProjectProfileScopeEnum.Fleet, FleetId = fleet.Id });

                    AuthContext auth = new AuthContext { IsAdmin = true };
                    ProjectProfileResolutionResult fleetResolved = await service.ResolveWithModeForVesselAsync(auth, vessel);
                    AssertEqual("Fleet", fleetResolved.Profile!.Name);
                    AssertEqual(ProjectProfileResolutionModeEnum.Fleet, fleetResolved.Mode);
                }
            }));

            cases.Add(CaseAsync("resolve_empty_returns_none", "Resolve Empty ReturnsNone", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ProjectProfileService service = new ProjectProfileService(db, CreateLogging());
                    Vessel vessel = new Vessel("Lonely", "https://github.com/test/c");
                    await db.Vessels.CreateAsync(vessel);

                    AuthContext auth = new AuthContext { IsAdmin = true };
                    ProjectProfileResolutionResult resolved = await service.ResolveWithModeForVesselAsync(auth, vessel);
                    AssertNull(resolved.Profile);
                    AssertEqual(ProjectProfileResolutionModeEnum.None, resolved.Mode);
                }
            }));

            cases.Add(CaseAsync("resolve_explicit_id_wins", "Resolve ExplicitId Wins", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ProjectProfileService service = new ProjectProfileService(db, CreateLogging());
                    Vessel vessel = new Vessel("Explicit", "https://github.com/test/d");
                    await db.Vessels.CreateAsync(vessel);

                    ProjectProfile explicitProfile = new ProjectProfile { Name = "Explicit Choice", Scope = ProjectProfileScopeEnum.Global };
                    await db.ProjectProfiles.CreateAsync(explicitProfile);
                    await db.ProjectProfiles.CreateAsync(new ProjectProfile { Name = "Other Global", Scope = ProjectProfileScopeEnum.Global, IsDefault = true });

                    AuthContext auth = new AuthContext { IsAdmin = true };
                    ProjectProfileResolutionResult resolved = await service.ResolveWithModeForVesselAsync(auth, vessel, explicitProfile.Id);
                    AssertNotNull(resolved.Profile);
                    AssertEqual("Explicit Choice", resolved.Profile!.Name);
                    AssertEqual(ProjectProfileResolutionModeEnum.Explicit, resolved.Mode);
                }
            }));

            // --- Persona override resolution (#22) ---

            cases.Add(Case("resolve_persona_override_matches_case_insensitive", "ResolvePersonaOverride MatchesCaseInsensitive", TestTags.Positive, () =>
            {
                ProjectProfile profile = new ProjectProfile { Name = "P" };
                profile.PersonaOverrides.Add(new PersonaOverride { PersonaName = "Architect", PromptTemplateName = "persona.architect.custom" });
                PersonaOverride? ovr = ProjectProfileService.ResolvePersonaOverride(profile, "architect");
                AssertNotNull(ovr);
                AssertEqual("persona.architect.custom", ovr!.PromptTemplateName);
            }));

            cases.Add(Case("resolve_persona_override_skips_disabled", "ResolvePersonaOverride SkipsDisabled", TestTags.Negative, () =>
            {
                ProjectProfile profile = new ProjectProfile { Name = "P" };
                profile.PersonaOverrides.Add(new PersonaOverride { PersonaName = "Worker", Enabled = false });
                AssertNull(ProjectProfileService.ResolvePersonaOverride(profile, "Worker"));
            }));

            cases.Add(Case("resolve_persona_override_null_profile", "ResolvePersonaOverride NullProfile ReturnsNull", TestTags.Negative, () =>
            {
                AssertNull(ProjectProfileService.ResolvePersonaOverride(null, "Worker"));
            }));

            cases.Add(CaseAsync("persona_prompt_appends_additional_instructions", "ResolvePersonaPrompt AppendsAdditionalInstructions", TestTags.Positive, async () =>
            {
                PersonaOverride ovr = new PersonaOverride { PersonaName = "Worker", AdditionalInstructions = "Always write ADRs." };
                string prompt = await MissionPromptBuilder.ResolvePersonaPromptAsync(
                    "Worker", new Dictionary<string, string>(), null, ovr, CancellationToken.None);
                AssertContains("Always write ADRs.", prompt);
            }));

            cases.Add(CaseAsync("persona_prompt_disabled_override_not_applied", "ResolvePersonaPrompt DisabledOverride NotApplied", TestTags.Negative, async () =>
            {
                PersonaOverride ovr = new PersonaOverride { PersonaName = "Worker", AdditionalInstructions = "SHOULD NOT APPEAR", Enabled = false };
                string prompt = await MissionPromptBuilder.ResolvePersonaPromptAsync(
                    "Worker", new Dictionary<string, string>(), null, ovr, CancellationToken.None);
                AssertFalse(prompt.Contains("SHOULD NOT APPEAR"), "Disabled override must not be applied");
            }));

            cases.Add(CaseAsync("persona_preview_reflects_template_override", "BuildPersonaPreview ReflectsTemplateOverride", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PromptTemplateService templates = new PromptTemplateService(testDb.Driver, CreateLogging());

                    ProjectProfile profile = new ProjectProfile { Name = "Override Worker with Architect template" };
                    profile.PersonaOverrides.Add(new PersonaOverride { PersonaName = "Worker", PromptTemplateName = "persona.architect" });

                    PersonaPromptPreview preview = await ProjectProfileService.BuildPersonaPreviewAsync(profile, "Worker", templates);
                    AssertTrue(preview.IsOverridden, "Preview should be marked overridden");
                    AssertEqual("persona.worker", preview.BaseTemplateName);
                    AssertEqual("persona.architect", preview.EffectiveTemplateName);
                    AssertNotEqual(preview.BasePrompt, preview.EffectivePrompt, "Base and effective prompts should differ");
                }
            }));

            cases.Add(CaseAsync("persona_preview_no_override_base_equals_effective", "BuildPersonaPreview NoOverride BaseEqualsEffective", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    PromptTemplateService templates = new PromptTemplateService(testDb.Driver, CreateLogging());
                    ProjectProfile profile = new ProjectProfile { Name = "No overrides" };

                    PersonaPromptPreview preview = await ProjectProfileService.BuildPersonaPreviewAsync(profile, "Worker", templates);
                    AssertFalse(preview.IsOverridden, "No override should mean not overridden");
                    AssertEqual(preview.BasePrompt, preview.EffectivePrompt, "Without an override base and effective should match");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Project Profile",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
