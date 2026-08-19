namespace Armada.Test.Unit
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Pins the consumption half of project profiles. Skills and per-persona prompt overrides were
    /// persisted, exposed over REST, and selectable by ProjectProfileService, while nothing in brief
    /// assembly ever read them: an operator could create a skill and attach it to a profile, and no
    /// captain would ever see it. These tests hold the two resolvers and the fact that brief assembly
    /// calls them.
    /// </summary>
    public sealed class ProjectProfileBriefInjectionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Project Profile Brief Injection";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Skills attached to a vessel profile render into a markdown block", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    MissionService service = CreateService(testDb, out Vessel vesselTemplate);
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(vesselTemplate);

                    Skill byName = new Skill();
                    byName.Name = "Bench Safety";
                    byName.Content = "Always isolate the bench before probing.";
                    byName = await testDb.Driver.Skills.CreateAsync(byName);

                    Skill byId = new Skill();
                    byId.Name = "Log Discipline";
                    byId.Content = "Quote the failing line, never a paraphrase.";
                    byId = await testDb.Driver.Skills.CreateAsync(byId);

                    ProjectProfile profile = new ProjectProfile();
                    profile.Name = "vessel-profile";
                    profile.VesselId = vessel.Id;
                    // One reference by name and one by id: the resolver must accept both forms.
                    profile.Skills = new List<string> { "Bench Safety", byId.Id };
                    await testDb.Driver.ProjectProfiles.CreateAsync(profile);

                    string markdown = await service.ResolveSkillsMarkdownAsync(vessel, CancellationToken.None);

                    Assert(markdown.Contains("### Bench Safety"), "Skill referenced by name should render");
                    Assert(markdown.Contains("Always isolate the bench before probing."), "Named skill content should render");
                    Assert(markdown.Contains("### Log Discipline"), "Skill referenced by id should render");
                    Assert(markdown.Contains("Quote the failing line, never a paraphrase."), "Id skill content should render");
                }
            });

            await RunTest("A vessel with no profile resolves no skills", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    MissionService service = CreateService(testDb, out Vessel vesselTemplate);
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(vesselTemplate);

                    string markdown = await service.ResolveSkillsMarkdownAsync(vessel, CancellationToken.None);
                    AssertEqual(String.Empty, markdown, "No profile means no skills section");
                }
            });

            await RunTest("An unresolvable skill reference is skipped rather than rendered empty", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    MissionService service = CreateService(testDb, out Vessel vesselTemplate);
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(vesselTemplate);

                    Skill real = new Skill();
                    real.Name = "Real Skill";
                    real.Content = "Present and resolvable.";
                    await testDb.Driver.Skills.CreateAsync(real);

                    ProjectProfile profile = new ProjectProfile();
                    profile.Name = "partial-profile";
                    profile.VesselId = vessel.Id;
                    profile.Skills = new List<string> { "Real Skill", "No Such Skill" };
                    await testDb.Driver.ProjectProfiles.CreateAsync(profile);

                    string markdown = await service.ResolveSkillsMarkdownAsync(vessel, CancellationToken.None);

                    Assert(markdown.Contains("### Real Skill"), "The resolvable skill should render");
                    Assert(
                        !markdown.Contains("No Such Skill"),
                        "A dangling reference must be skipped, not rendered as an empty heading");
                }
            });

            await RunTest("A profile persona override resolves for the mission persona", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    MissionService service = CreateService(testDb, out Vessel vesselTemplate);
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(vesselTemplate);

                    PersonaOverride personaOverride = new PersonaOverride();
                    personaOverride.PersonaName = "Worker";
                    personaOverride.PromptTemplateName = "persona.worker_custom";

                    ProjectProfile profile = new ProjectProfile();
                    profile.Name = "override-profile";
                    profile.VesselId = vessel.Id;
                    profile.PersonaOverrides = new List<PersonaOverride> { personaOverride };
                    await testDb.Driver.ProjectProfiles.CreateAsync(profile);

                    PersonaOverride? resolved =
                        await service.ResolvePersonaOverrideAsync(vessel, "Worker", CancellationToken.None);

                    AssertNotNull(resolved, "The profile's Worker override should resolve");
                    AssertEqual("persona.worker_custom", resolved!.PromptTemplateName, "Override template name");

                    PersonaOverride? unrelated =
                        await service.ResolvePersonaOverrideAsync(vessel, "Judge", CancellationToken.None);
                    AssertNull(unrelated, "A persona the profile does not customize resolves to no override");
                }
            });

            // The resolvers reach the database through method sets the concrete driver must assign. Those
            // are declared null! and were never assigned, so every call threw a NullReferenceException
            // from a stack naming neither the provider nor the entity. Pin the wiring on the provider the
            // tests and the deployed admiral both use.

            await RunTest("The SQLite driver wires every new entity method set", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    List<string> unwired = testDb.Driver.FindUnwiredMethodSets();
                    Assert(
                        unwired.Count == 0,
                        "SQLite driver left method sets unassigned: " + String.Join(", ", unwired));

                    AssertNotNull(testDb.Driver.ProjectProfiles, "ProjectProfiles must be assigned");
                    AssertNotNull(testDb.Driver.Skills, "Skills must be assigned");
                    AssertNotNull(testDb.Driver.CoordinationLeases, "CoordinationLeases must be assigned");
                }
            });

            // A persona's default captain seeds the dispatch UI's per-step assignment. The column was
            // added by a migration and then touched by no INSERT, UPDATE, or mapper, so the value could
            // be set and never stored -- the seed would silently always be empty.

            await RunTest("A persona default captain survives create, read, and update", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Persona persona = new Persona();
                    persona.Name = "persona-default-captain-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    persona.PromptTemplateName = "persona.worker";
                    persona.DefaultCaptainId = "cpt_exampledefault";
                    Persona created = await testDb.Driver.Personas.CreateAsync(persona);

                    Persona? readBack = await testDb.Driver.Personas.ReadAsync(created.Id);
                    AssertNotNull(readBack, "Persona should exist after create");
                    AssertEqual(
                        "cpt_exampledefault",
                        readBack!.DefaultCaptainId,
                        "The default captain must persist through create; null here is the original defect");

                    readBack.DefaultCaptainId = "cpt_examplereassigned";
                    await testDb.Driver.Personas.UpdateAsync(readBack);

                    Persona? updated = await testDb.Driver.Personas.ReadAsync(created.Id);
                    AssertEqual(
                        "cpt_examplereassigned",
                        updated!.DefaultCaptainId,
                        "An updated default captain must persist");
                }
            });

            // Both resolvers are best-effort and return empty on failure, so a wiring mistake in brief
            // assembly is invisible at runtime: the brief simply renders without the section, exactly as
            // it did when nothing called them at all. The guard asserts the calls exist.

            await RunTest("Brief assembly consumes both project-profile resolvers", () =>
            {
                string source = File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(), "src", "Armada.Core", "Services", "MissionService.cs"));

                Assert(
                    source.Contains("await ResolveSkillsMarkdownAsync(vessel, token)"),
                    "Brief assembly must resolve the vessel's profile skills");
                Assert(
                    source.Contains("ledger.Track(\"mission.skills\""),
                    "The skills section must be tracked in the prompt-budget ledger like every other module");
                Assert(
                    source.Contains("await ResolvePersonaOverrideAsync(vessel, mission.Persona, token)"),
                    "Brief assembly must resolve the vessel's persona override");
                Assert(
                    source.Contains("ResolvePersonaPromptAsync(mission.Persona, templateParams, personaOverride, token)"),
                    "The resolved override must reach the persona prompt builder, not be dropped as null");
            });
        }

        /// <summary>
        /// Build a MissionService over the test database, along with an unsaved vessel to persist.
        /// </summary>
        /// <param name="testDb">Test database.</param>
        /// <param name="vessel">Receives an unsaved vessel configured for temp paths.</param>
        /// <returns>The constructed mission service.</returns>
        private static MissionService CreateService(TestDatabase testDb, out Vessel vessel)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_profile_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_profile_repos_" + Guid.NewGuid().ToString("N"));

            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);

            vessel = new Vessel("profile-vessel-" + Guid.NewGuid().ToString("N").Substring(0, 8), "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_profile_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_profile_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";

            return new MissionService(logging, testDb.Driver, settings, docks, captains, git: git);
        }

        /// <summary>
        /// Walk up from the test binary until the directory containing the solution's src folder is found,
        /// so the source guard does not depend on the working directory a runner chose.
        /// </summary>
        /// <returns>Absolute path to the repository root.</returns>
        private static string FindRepositoryRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "Armada.Core"))) return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
        }
    }
}
