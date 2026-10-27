namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Every pack in the repository's battery/ folder: it parses and validates, its task ids are unique, every file it
    /// references exists (attachments, setup and overlay folders, expected files, rubrics, mutant targets and their
    /// find text), and every grader command runs a tool the pack declares, so a Harbor without it reports could-not-run.
    /// Per pack, where this machine has the pack's toolchain, the clean reset fixture passes its baseline test command
    /// and each command-graded task fails before the work is done; packs whose toolchain is absent are skipped with the
    /// missing tool named.
    /// </summary>
    public sealed class BatteryPacksSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.BatteryPacks";

        private static readonly string[] _LanguagePacks = new[] { "csharp", "javascript", "python", "go", "rust" };

        private static readonly string[] _TemplateTaskIds = new[]
        {
            "add-member", "two-place-edit", "fix-bug", "bom-crlf", "write-tests",
            "judge-negative", "judge-positive", "architect-plan", "lint-diff", "summarize-voyage"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite.
        /// </summary>
        /// <returns>Suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("every_pack_is_valid", "Every pack in battery/ parses and validates; the language packs are all present", TestTags.Positive, () =>
            {
                List<BatteryPack> packs = AllPacks();
                foreach (string id in _LanguagePacks)
                    AssertTrue(packs.Any(p => p.Id == id), "pack '" + id + "' is in battery/");
                foreach (BatteryPack pack in packs)
                {
                    AssertTrue(pack.IsValid, pack.Id + " is valid: " + String.Join(" | ", pack.Errors));
                    AssertEqual(pack.Id, pack.Manifest!.Id, pack.Id + ": the manifest id matches its folder");
                    AssertTrue(pack.Manifest.Version >= 1, pack.Id + ": version is 1 or more");
                    AssertEqual(64, pack.ToInfo().ManifestHash!.Length, pack.Id + ": manifest hash");
                }
            }));

            cases.Add(Case("task_ids_unique_and_templates_complete", "Task ids are unique in every pack; each language pack implements the ten template tasks with their personas", TestTags.Positive, () =>
            {
                foreach (BatteryPack pack in AllPacks())
                {
                    List<string> ids = pack.Manifest!.Tasks.Select(t => t.Id!).ToList();
                    List<string> duplicates = ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    AssertEqual(0, duplicates.Count, pack.Id + ": duplicate task ids " + String.Join(", ", duplicates));
                }

                Dictionary<string, string> csharpPersonas = Pack("csharp").Manifest!.Tasks.ToDictionary(t => t.Id!, t => t.Persona!, StringComparer.Ordinal);
                foreach (string id in _LanguagePacks)
                {
                    BatteryPackManifest manifest = Pack(id).Manifest!;
                    AssertEqual(String.Join(",", _TemplateTaskIds), String.Join(",", manifest.Tasks.Select(t => t.Id)), id + ": the ten template tasks in template order");
                    foreach (BatteryTaskDefinition task in manifest.Tasks)
                        AssertEqual(csharpPersonas[task.Id!], task.Persona, id + "/" + task.Id + ": persona matches the C# template");
                    foreach (BatteryGraderTypeEnum type in Enum.GetValues(typeof(BatteryGraderTypeEnum)))
                        AssertTrue(manifest.Tasks.Any(t => t.Grader!.Type == type), id + ": grader type used: " + type);
                    AssertEqual(3, TaskOf(id, "judge-negative").Grader!.Seeded.Count, id + ": three seeded Judge defects");
                    AssertEqual(0, TaskOf(id, "judge-positive").Grader!.Seeded.Count, id + ": the positive control seeds nothing");
                    AssertEqual(4, TaskOf(id, "lint-diff").Grader!.Seeded.Count, id + ": four seeded style violations");
                    AssertEqual(4, TaskOf(id, "write-tests").Grader!.Mutants.Count, id + ": four mutants");
                    AssertEqual("armada-plan", TaskOf(id, "architect-plan").Grader!.Schema, id + ": the architect plan is schema-checked");
                }
            }));

            cases.Add(Case("referenced_files_exist", "Every referenced file exists: fixture, attachments, setup, overlays, expected files, rubrics, mutant targets and their find text, compared paths", TestTags.Positive, () =>
            {
                foreach (BatteryPack pack in AllPacks())
                {
                    string fixture = pack.Resolve(pack.Manifest!.Fixture!.Path);
                    AssertTrue(Directory.Exists(fixture) && Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).Any(), pack.Id + ": the fixture folder has files");
                    foreach (BatteryTaskDefinition task in pack.Manifest.Tasks)
                    {
                        string where = pack.Id + "/" + task.Id;
                        BatteryGraderDefinition grader = task.Grader!;
                        foreach (string attachment in task.Attachments)
                            AssertTrue(File.Exists(pack.Resolve(attachment)), where + ": attachment " + attachment);
                        if (task.Setup != null) AssertTrue(Directory.EnumerateFiles(pack.Resolve(task.Setup), "*", SearchOption.AllDirectories).Any(), where + ": setup folder has files");
                        if (grader.Overlay != null) AssertTrue(Directory.EnumerateFiles(pack.Resolve(grader.Overlay), "*", SearchOption.AllDirectories).Any(), where + ": overlay folder has files");
                        if (grader.Rubric != null) AssertTrue(File.Exists(pack.Resolve(grader.Rubric)), where + ": rubric " + grader.Rubric);
                        foreach (BatteryExpectedFile file in grader.Files)
                        {
                            AssertTrue(File.Exists(pack.Resolve(file.Expected)), where + ": expected file " + file.Expected);
                            AssertNotNull(FixtureFile(pack, task, file.Path!), where + ": compared file " + file.Path + " is in the fixture");
                        }

                        foreach (BatteryMutant mutant in grader.Mutants)
                        {
                            string? target = FixtureFile(pack, task, mutant.File!);
                            AssertNotNull(target, where + ": mutant " + mutant.Id + " targets a fixture file " + mutant.File);
                            AssertContains(mutant.Find!, File.ReadAllText(target!), where + ": mutant " + mutant.Id + " find text is in " + mutant.File);
                            AssertTrue(!String.Equals(mutant.Find, mutant.Replace, StringComparison.Ordinal), where + ": mutant " + mutant.Id + " changes the code");
                        }

                        List<string> seededIds = grader.Seeded.Select(s => s.Id!).ToList();
                        AssertEqual(seededIds.Count, seededIds.Distinct(StringComparer.Ordinal).Count(), where + ": seeded ids are unique");
                        foreach (BatterySeededItem item in grader.Seeded)
                            AssertTrue(item.Keywords.Any(k => !String.IsNullOrWhiteSpace(k)) && !String.IsNullOrWhiteSpace(item.Description), where + ": seeded " + item.Id + " has keywords and a description");
                    }
                }
            }));

            cases.Add(Case("grader_tools_are_declared", "Every grader command runs a tool the task requires, so a Harbor without it reports could-not-run instead of failed", TestTags.Negative, () =>
            {
                foreach (BatteryPack pack in AllPacks())
                {
                    foreach (BatteryTaskDefinition task in pack.Manifest!.Tasks)
                    {
                        List<string> needed = pack.ToolchainFor(task);
                        HashSet<string> executables = new HashSet<string>(
                            pack.Manifest.Toolchain.Where(t => needed.Contains(t.Name!)).Select(t => t.Executable!), StringComparer.Ordinal);
                        foreach (BatteryCommand command in task.Grader!.Commands)
                            AssertTrue(executables.Contains(command.Executable!), pack.Id + "/" + task.Id + ": '" + command.Executable + "' is a declared toolchain executable of the task");
                        if (task.Grader.Type == BatteryGraderTypeEnum.ByteComparison)
                            AssertTrue(executables.Contains("git"), pack.Id + "/" + task.Id + ": byte comparison hashes with git, so the task needs git");
                    }
                }
            }));

            foreach (BatteryPack pack in AllPacksOrEmpty())
            {
                string? missing = MissingToolchain(pack);
                cases.Add(CaseAsync(
                    "baseline_" + pack.Id.Replace('-', '_'),
                    "Pack " + pack.Id + ": the clean fixture passes its baseline test command and every command-graded task fails before the work is done",
                    missing,
                    async () =>
                    {
                        using FixtureWorkspace clean = await FixtureWorkspace.CreateAsync(pack, null).ConfigureAwait(false);
                        foreach (BatteryTaskDefinition task in pack.Manifest!.Tasks)
                        {
                            BatteryToolchainCheck check = await BatteryTaskGrader.CheckToolchainAsync(pack, task, clean.Local).ConfigureAwait(false);
                            AssertEqual(0, check.Missing.Count, pack.Id + "/" + task.Id + ": toolchain probe: " + check.Reason);
                        }

                        List<BatteryTaskDefinition> baselines = pack.Manifest.Tasks.Where(t => t.Grader!.Type == BatteryGraderTypeEnum.Mutation).ToList();
                        AssertTrue(baselines.Count > 0, pack.Id + ": a mutation task names the baseline test command");
                        foreach (BatteryTaskDefinition task in baselines)
                        {
                            using FixtureWorkspace ws = await FixtureWorkspace.CreateAsync(pack, task).ConfigureAwait(false);
                            foreach (BatteryCommand command in task.Grader!.Commands)
                            {
                                HostCommandResult result = await ws.Local.RunAsync(command.Executable!, new List<string>(command.Arguments), (command.TimeoutSeconds ?? 600) * 1000).ConfigureAwait(false);
                                AssertEqual(command.ExpectedExitCode, result.ExitCode,
                                    pack.Id + ": baseline '" + command.Executable + " " + String.Join(" ", command.Arguments) + "' on the clean fixture: " + Tail(result));
                            }
                        }

                        foreach (BatteryTaskDefinition task in pack.Manifest.Tasks.Where(t => t.Grader!.Type == BatteryGraderTypeEnum.Command || t.Grader.Type == BatteryGraderTypeEnum.ByteComparison))
                        {
                            using FixtureWorkspace ws = await FixtureWorkspace.CreateAsync(pack, task).ConfigureAwait(false);
                            BatteryGrade grade = await BatteryTaskGrader.GradeAsync(pack, task, "done", ws.Local).ConfigureAwait(false);
                            AssertEqual(BatteryTaskOutcomeEnum.Failed, grade.Outcome, pack.Id + "/" + task.Id + ": the untouched fixture must not pass (" + grade.Reason + ")");
                        }
                    }));
            }

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Battery: every pack validates, references real files and passes its baseline",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static List<BatteryPack> AllPacks()
        {
            BatteryPackCatalog catalog = new BatteryPackCatalog(() => null);
            AssertNotNull(catalog.ResolveDirectory(), "the repository battery folder is found from the test host");
            List<BatteryPack> packs = catalog.LoadAll();
            AssertTrue(packs.Count > 0, "battery/ has packs");
            return packs;
        }

        private static List<BatteryPack> AllPacksOrEmpty()
        {
            try
            {
                return new BatteryPackCatalog(() => null).LoadAll().Where(p => p.IsValid).ToList();
            }
            catch (Exception)
            {
                return new List<BatteryPack>();
            }
        }

        private static BatteryPack Pack(string id)
        {
            BatteryPack? pack = new BatteryPackCatalog(() => null).Find(id);
            AssertNotNull(pack, "pack " + id);
            return pack!;
        }

        private static BatteryTaskDefinition TaskOf(string packId, string taskId)
        {
            return Pack(packId).Manifest!.Tasks.Single(t => t.Id == taskId);
        }

        private static string? FixtureFile(BatteryPack pack, BatteryTaskDefinition task, string relative)
        {
            List<string?> roots = new List<string?> { task.Setup, pack.Manifest!.Fixture!.Path };
            foreach (string? root in roots)
            {
                if (root == null) continue;
                string full = Path.Combine(pack.Resolve(root), relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) return full;
            }

            return null;
        }

        private static string? MissingToolchain(BatteryPack pack)
        {
            List<string> missing = new List<string>();
            foreach (BatteryToolchainRequirement tool in pack.Manifest!.Toolchain)
            {
                if (!Probe(tool.Executable!, tool.Arguments)) missing.Add(tool.Name + " (" + tool.Hint + ")");
            }

            return missing.Count == 0 ? null : "This machine lacks the " + pack.Id + " pack's toolchain: " + String.Join("; ", missing) + ".";
        }

        private static bool Probe(string executable, List<string> arguments)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                using Process? process = Process.Start(start);
                if (process == null) return false;
                process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static string Tail(HostCommandResult result)
        {
            string text = (result.StandardOutput + "\n" + result.StandardError).Trim();
            return text.Length <= 1500 ? text : text.Substring(text.Length - 1500);
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => { body(); return Task.CompletedTask; },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string? skipReason, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { TestTags.Process },
                skip: skipReason != null,
                skipReason: skipReason);
        }

        #endregion

        #region Private-Classes

        private sealed class FixtureWorkspace : IDisposable
        {
            public IBatteryWorkspace Local { get; }

            private readonly string _Root;
            private readonly TestDatabase _Db;

            private FixtureWorkspace(string root, TestDatabase db, IBatteryWorkspace local)
            {
                _Root = root;
                _Db = db;
                Local = local;
            }

            public static async Task<FixtureWorkspace> CreateAsync(BatteryPack pack, BatteryTaskDefinition? task)
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-battery-packs-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                BatteryFixtureService fixtures = new BatteryFixtureService(db.Driver, logging, Path.Combine(root, "data"));
                await fixtures.ResetAsync(pack, task, "test").ConfigureAwait(false);
                string worktree = Path.Combine(root, "wt");
                CannedBatteryTaskDispatcher.Git(root, "clone", "--quiet", fixtures.OriginPathFor(pack.Id), worktree);
                IBatteryWorkspace local = new DockBatteryWorkspace(new DockHostResolver(new GitService(logging), logging, null).Local, worktree);
                return new FixtureWorkspace(root, db, local);
            }

            public void Dispose()
            {
                _Db.Dispose();
                try
                {
                    if (Directory.Exists(_Root)) Directory.Delete(_Root, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        #endregion
    }
}
