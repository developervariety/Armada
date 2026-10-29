namespace Armada.Test.Unit.Suites.Battery
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// The battery pack format and its graders: manifest validation, the shipped packs, and grading a worktree as
    /// Passed, Failed or CouldNotRun.
    /// </summary>
    public sealed class BatteryPackTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Battery Packs";

        private const string MissingTool = "armada-battery-no-such-tool";

        private sealed class InvalidCase
        {
            public string Name { get; }

            public string Find { get; }

            public string Replace { get; }

            public string ExpectedError { get; }

            public InvalidCase(string name, string find, string replace, string expectedError)
            {
                Name = name;
                Find = find;
                Replace = replace;
                ExpectedError = expectedError;
            }
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Pack_ValidManifestWithCommentsAndTrailingCommas_Loads", () =>
            {
                string battery = BatteryPackFixtures.NewDirectory("valid");
                try
                {
                    BatteryPackFixtures.WritePack(battery, "mini", BatteryPackFixtures.BaseManifest, new BatteryPackFixtures.PackFile("fixture/a.txt", "a"));
                    BatteryPack pack = BatteryPackLoader.LoadAll(battery).Single();
                    AssertEqual(String.Empty, String.Join(" ", pack.Errors), "no validation errors");
                    AssertTrue(pack.IsValid, "the pack is valid");
                    BatteryPackInfo info = pack.ToInfo();
                    AssertEqual("mini", info.Id);
                    AssertEqual(1, info.Tasks.Count);
                    AssertEqual(BatteryGraderTypeEnum.Command, info.Tasks[0].Grader);
                    AssertEqual("git", info.Tasks[0].Toolchain.Single(), "a task without its own toolchain needs every pack entry");
                }
                finally { Directory.Delete(battery, true); }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Pack_ManifestBreakingTheSchema_IsListedWithItsError", () =>
            {
                List<InvalidCase> cases = new List<InvalidCase>
                {
                    new InvalidCase("schema version", "\"schemaVersion\": 1", "\"schemaVersion\": 2", "schemaVersion must be 1"),
                    new InvalidCase("pack id", "\"id\": \"mini\"", "\"id\": \"Bad Id\"", "lowercase letters, digits and dashes"),
                    new InvalidCase("pack version", "\"version\": 1", "\"version\": 0", "version must be 1 or more"),
                    new InvalidCase("fixture outside the pack", "\"path\": \"fixture\"", "\"path\": \"../outside\"", "relative path inside the pack"),
                    new InvalidCase("fixture folder missing", "\"path\": \"fixture\"", "\"path\": \"nothing\"", "is not a directory"),
                    new InvalidCase("grader without commands", "\"commands\": [ { \"executable\": \"git\", \"arguments\": [ \"--version\" ] } ]", "\"commands\": [ ]", "commands must list at least one command"),
                    new InvalidCase("task toolchain not in pack", "\"title\": \"Task one\",", "\"title\": \"Task one\", \"toolchain\": [ \"ghost\" ],", "not in the pack's toolchain"),
                    new InvalidCase("timeout out of range", "\"title\": \"Task one\",", "\"title\": \"Task one\", \"timeoutMinutes\": 5000,", "timeoutMinutes must be 1 to 1440"),
                    new InvalidCase("task without prompt", "\"prompt\": \"Do the thing.\",", "", "prompt is required"),
                    new InvalidCase("grader type the runner does not know", "\"type\": \"command\"", "\"type\": \"rubric\"", "not valid JSON")
                };

                foreach (InvalidCase testCase in cases)
                {
                    string battery = BatteryPackFixtures.NewDirectory("invalid");
                    try
                    {
                        BatteryPackFixtures.WritePack(battery, "mini", BatteryPackFixtures.BaseManifest.Replace(testCase.Find, testCase.Replace), new BatteryPackFixtures.PackFile("fixture/a.txt", "a"));
                        AssertTrue(BatteryPackFixtures.BaseManifest.Contains(testCase.Find), testCase.Name + ": the case changes the base manifest");
                        BatteryPack pack = BatteryPackLoader.LoadAll(battery).Single();
                        AssertFalse(pack.IsValid, testCase.Name + ": the pack is invalid");
                        AssertTrue(pack.Errors.Any(e => e.Contains(testCase.ExpectedError)), testCase.Name + ": errors were [" + String.Join(" | ", pack.Errors) + "]");
                        AssertFalse(pack.ToInfo().Valid, testCase.Name + ": the listing says invalid");
                    }
                    finally { Directory.Delete(battery, true); }
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Pack_DuplicateTaskIdAndMissingManifest_AreReported", () =>
            {
                string battery = BatteryPackFixtures.NewDirectory("duplicate");
                try
                {
                    string twice = BatteryPackFixtures.BaseManifest.Replace("\"tasks\": [", "\"tasks\": [ { \"id\": \"t1\", \"title\": \"A\", \"prompt\": \"p\", \"grader\": { \"type\": \"command\", \"commands\": [ { \"executable\": \"git\" } ] } },");
                    string directory = BatteryPackFixtures.WritePack(battery, "mini", twice, new BatteryPackFixtures.PackFile("fixture/a.txt", "a"));
                    AssertTrue(BatteryPackLoader.Load(directory).Errors.Any(e => e.Contains("used twice")), "a repeated task id is an error");
                    string empty = Path.Combine(battery, "empty");
                    Directory.CreateDirectory(empty);
                    AssertTrue(BatteryPackLoader.Load(empty).Errors.Any(e => e.Contains("no battery-pack.json")), "a folder without a manifest is an error");
                }
                finally { Directory.Delete(battery, true); }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Pack_ShippedPacks_AreValidAndReferenceExistingFiles", () =>
            {
                string? directory = new BatteryPackCatalog(() => null).ResolveDirectory();
                AssertNotNull(directory, "the battery directory is found next to the binary or in a source checkout");
                List<BatteryPack> packs = BatteryPackLoader.LoadAll(directory);
                AssertEqual("csharp,javascript", String.Join(",", packs.Select(p => p.Id)), "the shipped packs");
                foreach (BatteryPack pack in packs)
                {
                    AssertEqual(String.Empty, String.Join(" ", pack.Errors), pack.Id + " is valid");
                    AssertEqual("add-member,two-place-edit,fix-bug,bom-crlf,write-tests", String.Join(",", pack.Manifest!.Tasks.Select(t => t.Id)), pack.Id + " tasks");
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Grader_Command_PassesOnExpectedExitAndFailsOtherwise", async () =>
            {
                string work = BatteryPackFixtures.NewDirectory("worktree");
                try
                {
                    File.WriteAllText(Path.Combine(work, "result.txt"), "all done\n");
                    BatteryPack pack = PackWith("command", "\"commands\": [ { \"executable\": \"git\", \"arguments\": [ \"grep\", \"--no-index\", \"-q\", \"done\", \"--\", \"result.txt\" ] } ]");
                    BatteryGrade passed = await BatteryTaskGrader.GradeAsync(pack, pack.Manifest!.Tasks[0], new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual(BatteryTaskGradeEnum.Passed, passed.Grade, passed.Reason);
                    AssertEqual(1.0, passed.Score);

                    File.WriteAllText(Path.Combine(work, "result.txt"), "not yet\n");
                    BatteryGrade failed = await BatteryTaskGrader.GradeAsync(pack, pack.Manifest.Tasks[0], new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual(BatteryTaskGradeEnum.Failed, failed.Grade, "the command exits 1 when the text is missing");
                    AssertContains("exit code 1", failed.Reason!);
                    AssertEqual(1, failed.Details.Commands.Count);
                    AssertFalse(failed.Details.Commands[0].Passed);
                }
                finally { Directory.Delete(work, true); }
            }).ConfigureAwait(false);

            await RunTest("Grader_CommandWithoutAWorktree_CouldNotRun", async () =>
            {
                BatteryPack pack = PackWith("command", "\"commands\": [ { \"executable\": \"git\", \"arguments\": [ \"--version\" ] } ]");
                BatteryGrade grade = await BatteryTaskGrader.GradeAsync(pack, pack.Manifest!.Tasks[0], null).ConfigureAwait(false);
                AssertEqual(BatteryTaskGradeEnum.CouldNotRun, grade.Grade, "no worktree means nothing was judged");
            }).ConfigureAwait(false);

            await RunTest("Grader_MissingTool_IsFoundByTheToolchainProbeAndNamedInTheReason", async () =>
            {
                string work = BatteryPackFixtures.NewDirectory("worktree");
                try
                {
                    string manifest = BatteryPackFixtures.BaseManifest.Replace(
                        "\"toolchain\": [ { \"name\": \"git\", \"executable\": \"git\", \"arguments\": [ \"--version\" ] }, ],",
                        "\"toolchain\": [ { \"name\": \"git\", \"executable\": \"git\", \"arguments\": [ \"--version\" ] }, { \"name\": \"ghost\", \"executable\": \"" + MissingTool + "\", \"hint\": \"install ghost\" } ],");
                    BatteryPack pack = LoadMini(manifest);
                    BatteryToolchainCheck check = await BatteryTaskGrader.CheckToolchainAsync(pack, pack.Manifest!.Tasks[0], new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual("ghost", String.Join(",", check.Missing), "only the absent tool is missing; git is present");
                    AssertContains("install ghost", check.Reason!);
                    AssertContains("The Admiral host lacks", check.Reason!);
                }
                finally { Directory.Delete(work, true); }
            }).ConfigureAwait(false);

            await RunTest("Grader_ByteComparison_PassesOnExactBytesAndNamesWhatWasLost", async () =>
            {
                string work = BatteryPackFixtures.NewDirectory("worktree");
                string battery = BatteryPackFixtures.NewDirectory("bytes");
                try
                {
                    byte[] expected = Encoding.UTF8.GetPreamble().Concat(new UTF8Encoding(false).GetBytes("line one\r\nline two\r\n")).ToArray();
                    string manifest = BatteryPackFixtures.BaseManifest.Replace(
                        "\"grader\": { \"type\": \"command\", \"commands\": [ { \"executable\": \"git\", \"arguments\": [ \"--version\" ] } ] }",
                        "\"grader\": { \"type\": \"byteComparison\", \"files\": [ { \"path\": \"codes.txt\", \"expected\": \"expected/codes.txt\" } ] }");
                    BatteryPackFixtures.WritePack(battery, "mini", manifest, new BatteryPackFixtures.PackFile("fixture/a.txt", "a"), new BatteryPackFixtures.PackFile("expected/codes.txt", expected));
                    BatteryPack pack = BatteryPackLoader.LoadAll(battery).Single();
                    AssertTrue(pack.IsValid, String.Join(" ", pack.Errors));
                    BatteryTaskDefinition task = pack.Manifest!.Tasks[0];

                    File.WriteAllBytes(Path.Combine(work, "codes.txt"), expected);
                    BatteryGrade exact = await BatteryTaskGrader.GradeAsync(pack, task, new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual(BatteryTaskGradeEnum.Passed, exact.Grade, exact.Reason);

                    File.WriteAllText(Path.Combine(work, "codes.txt"), "line one\nline two\n", new UTF8Encoding(true));
                    BatteryGrade converted = await BatteryTaskGrader.GradeAsync(pack, task, new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual(BatteryTaskGradeEnum.Failed, converted.Grade, "CRLF converted to LF is not the expected bytes");
                    AssertContains("CRLF line endings were converted to LF", converted.Reason!);

                    File.WriteAllBytes(Path.Combine(work, "codes.txt"), new UTF8Encoding(false).GetBytes("line one\r\nline two\r\n"));
                    BatteryGrade dropped = await BatteryTaskGrader.GradeAsync(pack, task, new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertContains("byte order mark was dropped", dropped.Reason!);
                }
                finally
                {
                    Directory.Delete(work, true);
                    Directory.Delete(battery, true);
                }
            }).ConfigureAwait(false);

            await RunTest("Grader_Mutation_KillsAndSurvivesAndRestoresTheFile", async () =>
            {
                string work = BatteryPackFixtures.NewDirectory("worktree");
                try
                {
                    string source = "guard line\nother line\n";
                    File.WriteAllText(Path.Combine(work, "source.txt"), source);
                    string command = "\"commands\": [ { \"executable\": \"git\", \"arguments\": [ \"grep\", \"--no-index\", \"-q\", \"guard\", \"--\", \"source.txt\" ] } ], ";
                    string dropGuard = "{ \"id\": \"drop-guard\", \"file\": \"source.txt\", \"find\": \"guard\", \"replace\": \"gone\" }";
                    string touchOther = "{ \"id\": \"touch-other\", \"file\": \"source.txt\", \"find\": \"other\", \"replace\": \"changed\" }";
                    string killOnly = command + "\"mutants\": [ " + dropGuard + " ]";
                    BatteryPack killing = PackWith("mutation", killOnly);
                    BatteryGrade killed = await BatteryTaskGrader.GradeAsync(killing, killing.Manifest!.Tasks[0], new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual(BatteryTaskGradeEnum.Passed, killed.Grade, killed.Reason);
                    AssertEqual(1.0, killed.Score);
                    AssertTrue(killed.Details.Mutants.Single().Killed, "the tests fail with the mutant in place");
                    AssertEqual(source, File.ReadAllText(Path.Combine(work, "source.txt")), "the mutated file is restored");

                    string withSurvivor = command + "\"mutants\": [ " + dropGuard + ", " + touchOther + " ]";
                    BatteryPack surviving = PackWith("mutation", withSurvivor);
                    BatteryGrade partial = await BatteryTaskGrader.GradeAsync(surviving, surviving.Manifest!.Tasks[0], new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual(BatteryTaskGradeEnum.Failed, partial.Grade, "a mutant the tests do not detect fails the task");
                    AssertEqual(0.5, partial.Score);
                    AssertContains("touch-other", partial.Reason!);
                    AssertEqual(source, File.ReadAllText(Path.Combine(work, "source.txt")), "the file is restored after a surviving mutant");

                    File.WriteAllText(Path.Combine(work, "source.txt"), "no marker here\n");
                    BatteryGrade broken = await BatteryTaskGrader.GradeAsync(killing, killing.Manifest.Tasks[0], new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual(BatteryTaskGradeEnum.Failed, broken.Grade, "tests that fail before any mutation fail the task");
                    AssertEqual(0, broken.Details.Mutants.Count, "no mutant is tried against failing tests");
                }
                finally { Directory.Delete(work, true); }
            }).ConfigureAwait(false);

            await RunTest("ShippedPacks_UntouchedFixtureFailsEveryTask_NotCouldNotRun", async () =>
            {
                string? battery = new BatteryPackCatalog(() => null).ResolveDirectory();
                foreach (string[] shipped in new[] { new[] { "javascript", "node" }, new[] { "csharp", "dotnet" } })
                {
                    if (!await ToolExistsAsync(shipped[1]).ConfigureAwait(false))
                    {
                        Console.WriteLine("  (" + shipped[0] + " pack not graded: " + shipped[1] + " is not installed on this host)");
                        continue;
                    }

                    BatteryPack pack = BatteryPackLoader.LoadAll(battery).Single(p => p.Id == shipped[0]);
                    foreach (BatteryTaskDefinition task in pack.Manifest!.Tasks)
                    {
                        string work = BatteryPackFixtures.NewDirectory("fixture");
                        try
                        {
                            CopyTree(pack.Resolve(pack.Manifest.Fixture!.Path), work);
                            if (!String.IsNullOrEmpty(task.Setup)) CopyTree(pack.Resolve(task.Setup), work);
                            BatteryGrade untouched = await BatteryTaskGrader.GradeAsync(pack, task, new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                            AssertEqual(BatteryTaskGradeEnum.Failed, untouched.Grade, pack.Id + "/" + task.Id + " fails on the untouched fixture: " + untouched.Reason);
                        }
                        finally { Directory.Delete(work, true); }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("ShippedJavaScriptPack_ReferenceEditsPassTheirGraders", async () =>
            {
                if (!await ToolExistsAsync("node").ConfigureAwait(false))
                {
                    Console.WriteLine("  (javascript reference edits not graded: node is not installed on this host)");
                    return;
                }

                string? battery = new BatteryPackCatalog(() => null).ResolveDirectory();
                BatteryPack pack = BatteryPackLoader.LoadAll(battery).Single(p => p.Id == "javascript");
                BatteryTaskDefinition fix = pack.Manifest!.Tasks.Single(t => t.Id == "fix-bug");
                BatteryTaskDefinition bytes = pack.Manifest.Tasks.Single(t => t.Id == "bom-crlf");

                string work = BatteryPackFixtures.NewDirectory("fixture");
                try
                {
                    CopyTree(pack.Resolve(pack.Manifest.Fixture!.Path), work);
                    CopyTree(pack.Resolve(fix.Setup), work);
                    string path = Path.Combine(work, "src", "priceCalculator.js");
                    File.WriteAllText(path, File.ReadAllText(path).Replace("(price - percent) * 100", "(price - price * percent / 100) * 100"));
                    BatteryGrade fixedGrade = await BatteryTaskGrader.GradeAsync(pack, fix, new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual(BatteryTaskGradeEnum.Passed, fixedGrade.Grade, "the one-line fix passes fix-bug: " + fixedGrade.Reason);
                }
                finally { Directory.Delete(work, true); }

                work = BatteryPackFixtures.NewDirectory("fixture");
                try
                {
                    CopyTree(pack.Resolve(pack.Manifest.Fixture.Path), work);
                    File.Copy(pack.Resolve(bytes.Grader!.Files[0].Expected), Path.Combine(work, bytes.Grader.Files[0].Path!), true);
                    BatteryGrade copied = await BatteryTaskGrader.GradeAsync(pack, bytes, new LocalBatteryWorkspace(work)).ConfigureAwait(false);
                    AssertEqual(BatteryTaskGradeEnum.Passed, copied.Grade, "the expected bytes pass bom-crlf: " + copied.Reason);
                }
                finally { Directory.Delete(work, true); }
            }).ConfigureAwait(false);
        }

        private static BatteryPack PackWith(string graderType, string graderBody)
        {
            string manifest = BatteryPackFixtures.BaseManifest.Replace(
                "\"grader\": { \"type\": \"command\", \"commands\": [ { \"executable\": \"git\", \"arguments\": [ \"--version\" ] } ] }",
                "\"grader\": { \"type\": \"" + graderType + "\", " + graderBody + " }");
            return LoadMini(manifest);
        }

        private static BatteryPack LoadMini(string manifest)
        {
            string battery = BatteryPackFixtures.NewDirectory("pack");
            BatteryPackFixtures.WritePack(battery, "mini", manifest, new BatteryPackFixtures.PackFile("fixture/a.txt", "a"));
            BatteryPack pack = BatteryPackLoader.LoadAll(battery).Single();
            if (!pack.IsValid) throw new InvalidOperationException("Test pack is invalid: " + String.Join(" ", pack.Errors));
            return pack;
        }

        private static async Task<bool> ToolExistsAsync(string executable)
        {
            string work = BatteryPackFixtures.NewDirectory("probe");
            try
            {
                BoundedProcessResult result = await new LocalBatteryWorkspace(work).RunAsync(executable, new List<string> { "--version" }, 30000).ConfigureAwait(false);
                return result.ExitCode == 0 && !result.TimedOut;
            }
            finally { Directory.Delete(work, true); }
        }

        private static void CopyTree(string source, string destination)
        {
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
    }
}
