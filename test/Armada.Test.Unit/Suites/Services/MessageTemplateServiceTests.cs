namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class MessageTemplateServiceTests : TestSuite
    {
        public override string Name => "Message Template Service";

        private MessageTemplateService CreateService()
        {
            LoggingModule logging = new LoggingModule();
            return new MessageTemplateService(logging);
        }

        protected override async Task RunTestsAsync()
        {
            // RenderTemplate

            await RunTest("RenderTemplate ReplacesAllPlaceholders", () =>
            {
                MessageTemplateService service = CreateService();
                Dictionary<string, string> parameters = new Dictionary<string, string>
                {
                    ["MissionId"] = "msn_abc123",
                    ["CaptainId"] = "cpt_def456"
                };

                string result = service.RenderTemplate("Mission: {MissionId}, Captain: {CaptainId}", parameters);
                AssertEqual("Mission: msn_abc123, Captain: cpt_def456", result);
            });

            await RunTest("RenderTemplate EmptyTemplate ReturnsEmpty", () =>
            {
                MessageTemplateService service = CreateService();
                Dictionary<string, string> parameters = new Dictionary<string, string> { ["Key"] = "value" };
                string result = service.RenderTemplate("", parameters);
                AssertEqual("", result);
            });

            await RunTest("RenderTemplate NullTemplate ReturnsEmpty", () =>
            {
                MessageTemplateService service = CreateService();
                string result = service.RenderTemplate(null!, new Dictionary<string, string>());
                AssertEqual("", result);
            });

            await RunTest("RenderTemplate UnknownPlaceholder LeavesAsIs", () =>
            {
                MessageTemplateService service = CreateService();
                Dictionary<string, string> parameters = new Dictionary<string, string> { ["Known"] = "value" };
                string result = service.RenderTemplate("{Known} and {Unknown}", parameters);
                AssertEqual("value and {Unknown}", result);
            });

            await RunTest("RenderTemplate NullParameterValue ReplacesWithEmpty", () =>
            {
                MessageTemplateService service = CreateService();
                Dictionary<string, string> parameters = new Dictionary<string, string> { ["Key"] = null! };
                string result = service.RenderTemplate("Value: {Key}", parameters);
                AssertEqual("Value: ", result);
            });

            await RunTest("RenderTemplate EmptyParameters ReturnsTemplateUnchanged", () =>
            {
                MessageTemplateService service = CreateService();
                string result = service.RenderTemplate("Hello {World}", new Dictionary<string, string>());
                AssertEqual("Hello {World}", result);
            });

            // BuildContext

            await RunTest("BuildContext PopulatesAllFields", () =>
            {
                MessageTemplateService service = CreateService();
                Mission mission = new Mission("Test Mission", "Test description");
                Captain captain = new Captain("Captain-1");
                Vessel vessel = new Vessel("My Repo", "https://github.com/test/repo");
                vessel.FleetId = "flt_abc123";
                Voyage voyage = new Voyage("Test Voyage");
                Dock dock = new Dock(vessel.Id);
                dock.BranchName = "armada/test-branch";

                Dictionary<string, string> context = service.BuildContext(mission, captain, vessel, voyage, dock);

                AssertEqual(mission.Id, context["MissionId"]);
                AssertEqual("Test Mission", context["MissionTitle"]);
                AssertEqual(captain.Id, context["CaptainId"]);
                AssertEqual("Captain-1", context["CaptainName"]);
                AssertEqual(vessel.Id, context["VesselId"]);
                AssertEqual("My Repo", context["VesselName"]);
                AssertEqual("flt_abc123", context["FleetId"]);
                AssertEqual(voyage.Id, context["VoyageId"]);
                AssertEqual("Test Voyage", context["VoyageTitle"]);
                AssertEqual(dock.Id, context["DockId"]);
                AssertEqual("armada/test-branch", context["BranchName"]);
                AssertFalse(String.IsNullOrEmpty(context["Timestamp"]));
            });

            await RunTest("BuildContext NullOptionalObjects HandlesGracefully", () =>
            {
                MessageTemplateService service = CreateService();
                Mission mission = new Mission("Solo Mission");

                Dictionary<string, string> context = service.BuildContext(mission);

                AssertEqual(mission.Id, context["MissionId"]);
                AssertEqual("Solo Mission", context["MissionTitle"]);
                AssertEqual("", context["CaptainName"]);
                AssertEqual("", context["VesselName"]);
                AssertEqual("", context["FleetId"]);
                AssertEqual("", context["VoyageTitle"]);
                AssertEqual("", context["DockId"]);
            });

            await RunTest("BuildContext NullMission Throws", () =>
            {
                MessageTemplateService service = CreateService();
                AssertThrows<ArgumentNullException>(() => service.BuildContext(null!));
            });

            // RenderCommitInstructions

            await RunTest("RenderCommitInstructions EnabledSetting ReturnsInstructions", () =>
            {
                MessageTemplateService service = CreateService();
                MessageTemplateSettings settings = new MessageTemplateSettings();
                Dictionary<string, string> context = new Dictionary<string, string>
                {
                    ["MissionId"] = "msn_abc",
                    ["VoyageId"] = "vyg_def",
                    ["CaptainId"] = "cpt_ghi",
                    ["VesselId"] = "vsl_jkl"
                };

                string result = service.RenderCommitInstructions(settings, context);

                AssertContains("msn_abc", result);
                AssertContains("vyg_def", result);
                AssertContains("cpt_ghi", result);
                AssertContains("vsl_jkl", result);
                AssertContains("IMPORTANT", result);
            });

            await RunTest("RenderCommitInstructions requires a summary line and a full change manifest on both preamble paths", async () =>
            {
                Dictionary<string, string> context = new Dictionary<string, string>
                {
                    ["MissionId"] = "msn_abc",
                    ["VoyageId"] = "vyg_def",
                    ["CaptainId"] = "cpt_ghi",
                    ["VesselId"] = "vsl_jkl"
                };
                MessageTemplateSettings settings = new MessageTemplateSettings();

                string fallback = CreateService().RenderCommitInstructions(settings, context);

                string embedded;
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    PromptTemplateService templates = new PromptTemplateService(testDb.Driver, logging);
                    embedded = new MessageTemplateService(logging, templates).RenderCommitInstructions(settings, context);
                }

                foreach (string result in new[] { fallback, embedded })
                {
                    AssertContains("concise summary line", result, "the preamble asks for a summary line");
                    AssertContains("full manifest", result, "the preamble asks for a full change manifest");
                    AssertContains("every file added, modified, or deleted", result, "the manifest names every changed file");
                    AssertContains("msn_abc", result, "the trailers still follow the preamble");
                }
                AssertEqual(fallback, embedded, "the fallback preamble matches the embedded default");
            });

            await RunTest("Commit instructions and a worst-case worker launch prompt stay within the instruction byte budget", async () =>
            {
                Dictionary<string, string> context = new Dictionary<string, string>
                {
                    ["MissionId"] = "msn_" + new string('m', 24),
                    ["VoyageId"] = "vyg_" + new string('v', 24),
                    ["CaptainId"] = "cpt_" + new string('c', 24),
                    ["VesselId"] = "vsl_" + new string('s', 24)
                };
                ArmadaSettings armadaSettings = new ArmadaSettings();

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    PromptTemplateService templates = new PromptTemplateService(testDb.Driver, logging);
                    string instructions = new MessageTemplateService(logging, templates).RenderCommitInstructions(armadaSettings.MessageTemplates, context);
                    string preamble = templates.GetEmbeddedDefault("commit.instructions_preamble") ?? String.Empty;

                    Vessel vessel = new Vessel("BudgetVessel", "https://github.com/test/repo");
                    Captain captain = new Captain("budget-captain");
                    Mission mission = new Mission("Budget mission", "Worst-case mission description. " + new string('d', 4000));
                    mission.Persona = "Worker";
                    mission.BranchName = "armada/budget";
                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = mission.BranchName;
                    string launchPrompt = await MissionPromptBuilder.BuildLaunchPromptAsync(mission, vessel, captain, dock, templates).ConfigureAwait(false);
                    string delivered = launchPrompt + "\n\n" + instructions;

                    int preambleBytes = System.Text.Encoding.UTF8.GetByteCount(preamble);
                    int instructionBytes = System.Text.Encoding.UTF8.GetByteCount(instructions);
                    int deliveredBytes = System.Text.Encoding.UTF8.GetByteCount(delivered);
                    Console.WriteLine("COMMIT-BUDGET preamble=" + preambleBytes + " instructions=" + instructionBytes
                        + " launchWithInstructions=" + deliveredBytes + " budget=" + armadaSettings.CaptainInstructionByteBudget);

                    AssertTrue(instructionBytes <= 1024, "commit instructions are " + instructionBytes + " bytes, over the 1024-byte cap for this block");
                    AssertTrue(deliveredBytes <= armadaSettings.CaptainInstructionByteBudget,
                        "launch prompt with commit instructions is " + deliveredBytes + " bytes, over the " + armadaSettings.CaptainInstructionByteBudget + " byte budget");
                }
            });

            await RunTest("RenderCommitInstructions DisabledSetting ReturnsEmpty", () =>
            {
                MessageTemplateService service = CreateService();
                MessageTemplateSettings settings = new MessageTemplateSettings();
                settings.EnableCommitMetadata = false;

                string result = service.RenderCommitInstructions(settings, new Dictionary<string, string>());
                AssertEqual("", result);
            });

            // RenderPrDescription

            await RunTest("RenderPrDescription AppendsToBaseBody", () =>
            {
                MessageTemplateService service = CreateService();
                MessageTemplateSettings settings = new MessageTemplateSettings();
                Dictionary<string, string> context = new Dictionary<string, string>
                {
                    ["MissionId"] = "msn_abc",
                    ["VoyageId"] = "vyg_def",
                    ["CaptainId"] = "cpt_ghi",
                    ["VesselId"] = "vsl_jkl"
                };

                string result = service.RenderPrDescription(settings, "## My PR Body", context);

                AssertStartsWith("## My PR Body", result);
                AssertContains("msn_abc", result);
                AssertContains("Armada", result);
            });

            await RunTest("RenderPrDescription DisabledSetting ReturnsBaseBody", () =>
            {
                MessageTemplateService service = CreateService();
                MessageTemplateSettings settings = new MessageTemplateSettings();
                settings.EnablePrMetadata = false;

                string result = service.RenderPrDescription(settings, "Base body", new Dictionary<string, string>());
                AssertEqual("Base body", result);
            });

            // RenderMergeCommitMessage

            await RunTest("RenderMergeCommitMessage EnabledSetting ReturnsMessage", () =>
            {
                MessageTemplateService service = CreateService();
                MessageTemplateSettings settings = new MessageTemplateSettings();
                Dictionary<string, string> context = new Dictionary<string, string>
                {
                    ["BranchName"] = "armada/test-branch",
                    ["MissionId"] = "msn_abc",
                    ["VoyageId"] = "vyg_def"
                };

                string? result = service.RenderMergeCommitMessage(settings, context);

                AssertNotNull(result);
                AssertContains("armada/test-branch", result!);
                AssertContains("msn_abc", result);
            });

            await RunTest("RenderMergeCommitMessage DisabledSetting ReturnsNull", () =>
            {
                MessageTemplateService service = CreateService();
                MessageTemplateSettings settings = new MessageTemplateSettings();
                settings.EnableCommitMetadata = false;

                string? result = service.RenderMergeCommitMessage(settings, new Dictionary<string, string>());
                AssertNull(result);
            });

            // MessageTemplateSettings

            await RunTest("MessageTemplateSettings DefaultValues AreCorrect", () =>
            {
                MessageTemplateSettings settings = new MessageTemplateSettings();
                AssertTrue(settings.EnableCommitMetadata);
                AssertTrue(settings.EnablePrMetadata);
                AssertContains("Armada-Mission-Id", settings.CommitMessageTemplate);
                AssertContains("Armada", settings.PrDescriptionTemplate);
                AssertContains("Merge armada mission", settings.MergeCommitTemplate);
            });

            await RunTest("MessageTemplateSettings SetNull DefaultsToEmpty", () =>
            {
                MessageTemplateSettings settings = new MessageTemplateSettings();
                settings.CommitMessageTemplate = null!;
                settings.PrDescriptionTemplate = null!;
                settings.MergeCommitTemplate = null!;
                AssertEqual("", settings.CommitMessageTemplate);
                AssertEqual("", settings.PrDescriptionTemplate);
                AssertEqual("", settings.MergeCommitTemplate);
            });

            await RunTest("MessageTemplateSettings CustomTemplate IsPreserved", () =>
            {
                MessageTemplateSettings settings = new MessageTemplateSettings();
                settings.CommitMessageTemplate = "Custom: {MissionId}";
                AssertEqual("Custom: {MissionId}", settings.CommitMessageTemplate);
            });
        }
    }
}
