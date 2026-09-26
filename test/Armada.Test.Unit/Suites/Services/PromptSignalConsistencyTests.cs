namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    public class PromptSignalConsistencyTests : TestSuite
    {
        private sealed class PromptFixtureResult
        {
            public Mission Mission { get; set; } = null!;

            public Vessel Vessel { get; set; } = null!;

            public Captain Captain { get; set; } = null!;

            public Dock Dock { get; set; } = null!;
        }

        public override string Name => "Prompt Signal Consistency";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static PromptFixtureResult CreatePromptFixture(string persona)
        {
            string slug = persona.ToLowerInvariant().Replace(" ", "-");
            Vessel vessel = new Vessel("PromptSignalVessel", "https://github.com/test/repo");
            Captain captain = new Captain(slug + "-captain");
            captain.Runtime = AgentRuntimeEnum.Codex;

            Mission mission = new Mission(persona + " mission", "Validate the signal contract.");
            mission.Persona = persona;
            mission.BranchName = "armada/" + slug + "-signal-contract";

            Dock dock = new Dock(vessel.Id);
            dock.BranchName = mission.BranchName;

            return new PromptFixtureResult
            {
                Mission = mission,
                Vessel = vessel,
                Captain = captain,
                Dock = dock
            };
        }

        private async Task<string> ResolveTemplateContentAsync(PromptTemplateService templates, string templateName)
        {
            PromptTemplate? template = await templates.ResolveAsync(templateName).ConfigureAwait(false);
            AssertNotNull(template, templateName + " should resolve");
            return template!.Content;
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Linter prompt surfaces require the lint report sections", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    PromptTemplateService templates = new PromptTemplateService(testDb.Driver, logging);
                    string embeddedPrompt = await ResolveTemplateContentAsync(templates, "persona.linter").ConfigureAwait(false);
                    PromptFixtureResult fixture = CreatePromptFixture("Linter");
                    Dictionary<string, string> templateParams = MissionPromptBuilder.BuildTemplateParams(fixture.Mission, fixture.Vessel, fixture.Captain, fixture.Dock);
                    string fallbackPrompt = await MissionPromptBuilder.ResolvePersonaPromptAsync("Linter", templateParams, null).ConfigureAwait(false);
                    string launchPrompt = await MissionPromptBuilder.BuildLaunchPromptAsync(fixture.Mission, fixture.Vessel, fixture.Captain, fixture.Dock, null).ConfigureAwait(false);
                    string handoffPreamble = MissionService.BuildPersonaPreamble("Linter", MissionModeEnum.Implementation);

                    foreach (string section in new[] { "## Code Style", "## Code Correctness", "## Documentation", "## Fixes Applied", "## Residual Issues" })
                    {
                        AssertContains(section, embeddedPrompt, "persona.linter embedded template");
                        AssertContains(section, MissionPromptBuilder.GetPersonaOutputContract("Linter"), "Linter output contract");
                        AssertContains(section, fallbackPrompt, "Linter fallback prompt");
                        AssertFalse(launchPrompt.Contains(section, StringComparison.Ordinal), "the Linter launch prompt points at the contract rather than restating " + section);
                        AssertContains(section, handoffPreamble, "Linter handoff preamble");
                    }

                    AssertContains("## Your Role: Linter", handoffPreamble, "Linter handoff preamble names the role");
                    AssertContains("## Recall Existing Memory", embeddedPrompt, "The Linter is a working persona and reads memory");
                    AssertContains("linter agent", launchPrompt, "The launch prompt role line names the Linter");
                }
            });
        }
    }
}
