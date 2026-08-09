namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Covers the mission Mode field: its default, its parsing, its database round trip, and the
    /// completion-gate exemption it exists for. Before modes existed, a correct read-only mission was
    /// marked Failed for producing no commit, because the gate assumed every Worker commits.
    /// </summary>
    public class MissionModeTests : TestSuite
    {
        public override string Name => "Mission Modes";

        protected override async Task RunTestsAsync()
        {
            await RunTest("A mission defaults to Implementation mode", async () =>
            {
                Mission mission = new Mission();
                AssertEqual(MissionModeEnum.Implementation, mission.Mode, "default mode must be Implementation");
                AssertFalse(mission.IsReadOnlyMode, "Implementation must not be read-only");

                await Task.CompletedTask;
            });

            await RunTest("Audit and Research are read-only; Implementation is not", async () =>
            {
                Mission audit = new Mission();
                audit.Mode = MissionModeEnum.Audit;
                AssertTrue(audit.IsReadOnlyMode, "Audit must be read-only");

                Mission research = new Mission();
                research.Mode = MissionModeEnum.Research;
                AssertTrue(research.IsReadOnlyMode, "Research must be read-only");

                Mission implementation = new Mission();
                implementation.Mode = MissionModeEnum.Implementation;
                AssertFalse(implementation.IsReadOnlyMode, "Implementation must not be read-only");

                await Task.CompletedTask;
            });

            await RunTest("Unknown or absent stored modes resolve to Implementation", async () =>
            {
                AssertEqual(MissionModeEnum.Implementation, MissionModes.Parse(null), "null must resolve to Implementation");
                AssertEqual(MissionModeEnum.Implementation, MissionModes.Parse(""), "empty must resolve to Implementation");
                AssertEqual(MissionModeEnum.Implementation, MissionModes.Parse("   "), "whitespace must resolve to Implementation");
                AssertEqual(MissionModeEnum.Implementation, MissionModes.Parse("nonsense"), "an unknown value must resolve to Implementation");
                AssertEqual(MissionModeEnum.Audit, MissionModes.Parse("audit"), "parsing must be case-insensitive");
                AssertEqual(MissionModeEnum.Research, MissionModes.Parse(" Research "), "parsing must tolerate surrounding space");

                AssertFalse(MissionModes.IsKnown(null), "null is not a known mode");
                AssertFalse(MissionModes.IsKnown("nonsense"), "a typo is not a known mode");
                AssertTrue(MissionModes.IsKnown("Audit"), "Audit is a known mode");

                await Task.CompletedTask;
            });

            await RunTest("Mission mode survives a database round trip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Mission audit = new Mission();
                    audit.Title = "Read-only probe";
                    audit.Mode = MissionModeEnum.Audit;
                    Mission createdAudit = await testDb.Driver.Missions.CreateAsync(audit);

                    Mission? readAudit = await testDb.Driver.Missions.ReadAsync(createdAudit.Id);
                    AssertNotNull(readAudit, "the created mission must be readable");
                    AssertEqual(MissionModeEnum.Audit, readAudit!.Mode, "Audit mode must persist");
                    AssertTrue(readAudit.IsReadOnlyMode, "the round-tripped mission must still be read-only");

                    // Update to a different mode and read it back, which exercises the UPDATE path
                    // separately from INSERT. Postgres binds both through one shared parameter method,
                    // but the other drivers bind them independently.
                    readAudit.Mode = MissionModeEnum.Research;
                    await testDb.Driver.Missions.UpdateAsync(readAudit);

                    Mission? afterUpdate = await testDb.Driver.Missions.ReadAsync(createdAudit.Id);
                    AssertNotNull(afterUpdate, "the updated mission must be readable");
                    AssertEqual(MissionModeEnum.Research, afterUpdate!.Mode, "an updated mode must persist");

                    Mission plain = new Mission();
                    plain.Title = "Ordinary implementation mission";
                    Mission createdPlain = await testDb.Driver.Missions.CreateAsync(plain);
                    Mission? readPlain = await testDb.Driver.Missions.ReadAsync(createdPlain.Id);
                    AssertNotNull(readPlain, "the plain mission must be readable");
                    AssertEqual(MissionModeEnum.Implementation, readPlain!.Mode, "an unset mode must persist as Implementation");
                }
            });

            await RunTest("An Audit brief drops commit, merge-conflict and learned-fact modules", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_mode_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_mode_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LearnedFactsEnabled = true;

                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService service = new MissionService(logging, testDb.Driver, settings, dockService, captainService);

                    string auditDir = Path.Combine(Path.GetTempPath(), "armada_mode_audit_" + Guid.NewGuid().ToString("N"));
                    string implDir = Path.Combine(Path.GetTempPath(), "armada_mode_impl_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(auditDir);
                    Directory.CreateDirectory(implDir);

                    try
                    {
                        Vessel vessel = new Vessel("ModeVessel", "https://github.com/test/repo");
                        vessel.EnableModelContext = true;
                        vessel.ModelContext = "Vessel model context.";

                        Mission audit = new Mission();
                        audit.Title = "Read-only probe";
                        audit.Description = "Measure the received context.";
                        audit.Persona = "Worker";
                        audit.Mode = MissionModeEnum.Audit;
                        await service.GenerateClaudeMdAsync(auditDir, audit, vessel);
                        string auditBrief = await File.ReadAllTextAsync(Path.Combine(auditDir, "CLAUDE.md"));

                        Mission implementation = new Mission();
                        implementation.Title = "Ordinary work";
                        implementation.Description = "Change the code.";
                        implementation.Persona = "Worker";
                        await service.GenerateClaudeMdAsync(implDir, implementation, vessel);
                        string implBrief = await File.ReadAllTextAsync(Path.Combine(implDir, "CLAUDE.md"));

                        // The audit brief must not carry implementation-only instructions.
                        AssertFalse(auditBrief.Contains("Commit all changes to the current branch", StringComparison.Ordinal), "audit brief must not order commits");
                        AssertFalse(auditBrief.Contains("Avoiding Merge Conflicts", StringComparison.Ordinal), "audit brief must not carry merge-conflict guidance");
                        AssertFalse(auditBrief.Contains("LEARNED-FACT-PROPOSAL", StringComparison.Ordinal), "audit brief must not request learned facts");
                        AssertContains("read-only", auditBrief, "audit brief must state that it is read-only");
                        // The test-ownership directive tells a producing captain to run tests and commit
                        // them, which contradicts a read-only brief.
                        AssertFalse(auditBrief.Contains("You own the tests for this change", StringComparison.Ordinal),
                            "an audit brief must not carry a test-ownership directive");
                        AssertFalse(auditBrief.Contains("{TestOwnership}", StringComparison.Ordinal),
                            "the placeholder must never survive into a brief");
                        // The producing persona templates carry their own commit language, which would
                        // contradict the read-only rules in the same brief.
                        AssertFalse(auditBrief.Contains("Commit your scoped implementation changes", StringComparison.Ordinal),
                            "the worker persona template must not reach a read-only brief");
                        AssertFalse(auditBrief.Contains("before committing", StringComparison.Ordinal),
                            "no pre-commit check instruction may reach a read-only brief");
                        AssertContains("your deliverable is a report", auditBrief,
                            "a read-only brief states the deliverable instead");
                        AssertContains("Producing no commit is the expected outcome", auditBrief, "audit brief must state the completion contract");

                        // The implementation brief must be unchanged in those respects.
                        AssertContains("Commit all changes to the current branch", implBrief, "implementation brief must still order commits");
                        AssertContains("Avoiding Merge Conflicts", implBrief, "implementation brief must still carry merge-conflict guidance");

                        AssertTrue(auditBrief.Length < implBrief.Length, "an audit brief must be smaller than the implementation brief");

                        // The implementation context-conservation module forbids reading any file over
                        // 200 lines and tells the captain to grep for the section first. In an audit
                        // that compares whole files against a reference, that rule made captains grep,
                        // measure, then read the same file anyway -- three steps per file -- and it
                        // ordered a commit a read-only mission cannot make.
                        AssertFalse(auditBrief.Contains("NEVER read entire large files", StringComparison.Ordinal),
                            "an audit brief must not forbid whole-file reads");
                        AssertFalse(auditBrief.Contains("commit what you have", StringComparison.Ordinal),
                            "an audit brief must not resolve an oversized scope with a commit");
                        AssertContains("Read each file you need once, in full", auditBrief,
                            "an audit brief must permit one whole-file read");
                        AssertContains("Never read the same path twice", auditBrief,
                            "an audit brief must forbid repeat reads of the same path");

                        // The implementation brief keeps the line-budget rule: an edit needs one region
                        // of one file, so reading the whole file there is waste.
                        AssertContains("NEVER read entire large files", implBrief,
                            "an implementation brief must keep the whole-file-read rule");

                        // Batching applies to both modes. Captains issued one tool call per turn
                        // because no module told them independent calls can share a turn.
                        AssertContains("Tool Batching", auditBrief, "an audit brief must carry the batching directive");
                        AssertContains("Tool Batching", implBrief, "an implementation brief must carry the batching directive");
                        AssertContains("one step, not six", auditBrief, "the batching directive must give a concrete example");
                    }
                    finally
                    {
                        try { Directory.Delete(auditDir, true); } catch { }
                        try { Directory.Delete(implDir, true); } catch { }
                    }
                }
            });

            await RunTest("A read-only Worker contract does not ask for code changes", async () =>
            {
                string implementationContract = MissionPromptBuilder.GetPersonaOutputContract("Worker", MissionModeEnum.Implementation);
                AssertContains("make the requested changes", implementationContract, "an implementation Worker is still asked to make changes");

                string auditContract = MissionPromptBuilder.GetPersonaOutputContract("Worker", MissionModeEnum.Audit);
                AssertFalse(auditContract.Contains("make the requested changes", StringComparison.Ordinal), "an audit Worker must not be asked to make changes");
                AssertContains("your deliverable is a report", auditContract, "an audit Worker must be told the deliverable is a report");

                string researchContract = MissionPromptBuilder.GetPersonaOutputContract("TestEngineer", MissionModeEnum.Research);
                AssertContains("your deliverable is a report", researchContract, "a research TestEngineer must not be asked to write tests");

                // A reviewer persona already reports rather than changes, so its contract is untouched.
                string judgeContract = MissionPromptBuilder.GetPersonaOutputContract("Judge", MissionModeEnum.Audit);
                AssertContains("[ARMADA:VERDICT]", judgeContract, "a Judge keeps its verdict contract in every mode");

                await Task.CompletedTask;
            });

            await RunTest("An Audit TestEngineer brief never receives a write-tests role line", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_mode_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_mode_repos_" + Guid.NewGuid().ToString("N"));
                    settings.LearnedFactsEnabled = true;

                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService service = new MissionService(logging, testDb.Driver, settings, dockService, captainService);

                    string auditDir = Path.Combine(Path.GetTempPath(), "armada_mode_audit_te_" + Guid.NewGuid().ToString("N"));
                    string implDir = Path.Combine(Path.GetTempPath(), "armada_mode_impl_te_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(auditDir);
                    Directory.CreateDirectory(implDir);

                    try
                    {
                        Vessel vessel = new Vessel("ModeVessel", "https://github.com/test/repo");
                        vessel.EnableModelContext = true;
                        vessel.ModelContext = "Vessel model context.";

                        Mission audit = new Mission();
                        audit.Title = "Read-only test engineer probe";
                        audit.Description = "Review the prior stage diff for coverage gaps.";
                        audit.Persona = "TestEngineer";
                        audit.Mode = MissionModeEnum.Audit;
                        await service.GenerateClaudeMdAsync(auditDir, audit, vessel);
                        string auditBrief = await File.ReadAllTextAsync(Path.Combine(auditDir, "CLAUDE.md"));

                        Mission implementation = new Mission();
                        implementation.Title = "Test engineer implementation";
                        implementation.Description = "Write tests for the change.";
                        implementation.Persona = "TestEngineer";
                        await service.GenerateClaudeMdAsync(implDir, implementation, vessel);
                        string implBrief = await File.ReadAllTextAsync(Path.Combine(implDir, "CLAUDE.md"));

                        // The contradiction: the TestEngineer persona template is write-tests language.
                        // A read-only brief must not carry any of it, while the implementation brief keeps it.
                        AssertFalse(auditBrief.Contains("Coverage Added", StringComparison.Ordinal),
                            "an Audit TestEngineer brief must not demand a Coverage Added section");
                        AssertFalse(auditBrief.Contains("Commit test files only", StringComparison.Ordinal),
                            "an Audit TestEngineer brief must not order test-file commits");
                        AssertFalse(auditBrief.Contains("You own validation and test coverage", StringComparison.Ordinal),
                            "an Audit TestEngineer brief must not assign test authorship");
                        AssertContains("your deliverable is a report", auditBrief,
                            "an Audit TestEngineer brief states the report deliverable instead");

                        AssertContains("Coverage Added", implBrief,
                            "an implementation TestEngineer brief keeps its coverage contract");

                        await Task.CompletedTask;
                    }
                    finally
                    {
                        try { Directory.Delete(auditDir, true); } catch { }
                        try { Directory.Delete(implDir, true); } catch { }
                    }
                }
            });

            await RunTest("The no-commit gate exempts read-only modes and still catches implementation misses", async () =>
            {
                // PersonaMustProduceChanges is the persona half of the gate; the mode half is
                // mission.IsReadOnlyMode. Both must be true for a mission to be failed for producing
                // no commit, so a Worker in Audit mode is exempt while a Worker in Implementation
                // mode is not.
                AssertTrue(Armada.Server.MissionLandingHandler.PersonaMustProduceChanges("Worker"), "a Worker must produce changes");
                AssertFalse(Armada.Server.MissionLandingHandler.PersonaMustProduceChanges("Judge"), "a Judge need not produce changes");

                Mission auditWorker = new Mission();
                auditWorker.Persona = "Worker";
                auditWorker.Mode = MissionModeEnum.Audit;
                AssertTrue(
                    Armada.Server.MissionLandingHandler.PersonaMustProduceChanges(auditWorker.Persona) && auditWorker.IsReadOnlyMode,
                    "an Audit Worker is persona-required but mode-exempt, so the gate must not fail it");

                Mission implementationWorker = new Mission();
                implementationWorker.Persona = "Worker";
                AssertTrue(
                    Armada.Server.MissionLandingHandler.PersonaMustProduceChanges(implementationWorker.Persona) && !implementationWorker.IsReadOnlyMode,
                    "an Implementation Worker must still be failed when it produces no commit");

                await Task.CompletedTask;
            });
        }
    }
}
