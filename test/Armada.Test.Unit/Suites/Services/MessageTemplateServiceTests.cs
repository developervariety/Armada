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

        // The launch prompt plus commit instructions is sent on every launch, so it stays small whatever the
        // instruction-file budget is set to.
        private const int _LaunchPromptCapBytes = 32768;

        private MessageTemplateService CreateService()
        {
            LoggingModule logging = new LoggingModule();
            return new MessageTemplateService(logging);
        }

        protected override async Task RunTestsAsync()
        {
            // RenderCommitInstructions

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
                        + " launchWithInstructions=" + deliveredBytes + " cap=" + _LaunchPromptCapBytes);

                    AssertTrue(instructionBytes <= 1024, "commit instructions are " + instructionBytes + " bytes, over the 1024-byte cap for this block");
                    AssertTrue(deliveredBytes <= _LaunchPromptCapBytes,
                        "launch prompt with commit instructions is " + deliveredBytes + " bytes, over the " + _LaunchPromptCapBytes + " byte cap");
                }
            });
        }
    }
}
