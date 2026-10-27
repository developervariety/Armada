namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// The C# pack against the real toolchain (dotnet and git on this machine): for every worktree-graded task the
    /// reset fixture fails its grader for the reason the task is about (the failing test is present for the bug fix, the
    /// test is unregistered for the two-place edit, mutants survive without tests, and so on), and a reference
    /// solution passes. Proves the fixture and the graders agree before any model is measured with them.
    /// </summary>
    public sealed class BatteryCSharpPackSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Battery.CSharpPack";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite.
        /// </summary>
        /// <returns>Suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("fixture_builds_and_tests_pass", "The reset fixture builds and its registered tests pass; the toolchain probe finds dotnet and git", async () =>
            {
                using Workspace ws = await Workspace.CreateAsync(null).ConfigureAwait(false);
                HostCommandResult build = await ws.Local.RunAsync("dotnet", new List<string> { "build", "Inventory.sln" }, 600000).ConfigureAwait(false);
                AssertTrue(build.Success, "build: " + build.StandardOutput + build.StandardError);
                HostCommandResult tests = await ws.Local.RunAsync("dotnet", new List<string> { "run", "--project", "tests/Inventory.Tests" }, 600000).ConfigureAwait(false);
                AssertTrue(tests.Success, "tests: " + tests.StandardOutput);
                AssertContains("6 passed, 0 failed", tests.StandardOutput);
                BatteryToolchainCheck check = await BatteryTaskGrader.CheckToolchainAsync(ws.Pack, Task("add-member"), ws.Local).ConfigureAwait(false);
                AssertEqual(0, check.Missing.Count, check.Reason);
            }));

            cases.Add(CaseAsync("add_member", "Add a member: the hidden check fails without Item.TotalValue and passes with it", async () =>
            {
                using Workspace ws = await Workspace.CreateAsync(Task("add-member")).ConfigureAwait(false);
                BatteryGrade before = await ws.GradeAsync("add-member").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Failed, before.Outcome);
                AssertContains("tests/Inventory.Grading", before.Reason!);

                using Workspace solved = await Workspace.CreateAsync(Task("add-member")).ConfigureAwait(false);
                solved.Replace("src/Inventory/Item.cs", "        /// <summary>\n        /// Instantiate.",
                    "        /// <summary>\n        /// Unit price times quantity.\n        /// </summary>\n        public decimal TotalValue\n        {\n            get { return UnitPrice * Quantity; }\n        }\n\n        /// <summary>\n        /// Instantiate.");
                BatteryGrade after = await solved.GradeAsync("add-member").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Passed, after.Outcome, after.Reason + " " + Last(after));
            }));

            cases.Add(CaseAsync("two_place_edit", "Two-place edit: the test method alone is not enough; it passes only when it is also registered", async () =>
            {
                string method = "\n        /// <summary>\n        /// Removing more than is on hand throws.\n        /// </summary>\n        public static void Stock_Remove_MoreThanAvailable_Throws()\n        {\n            Stock stock = new Stock();\n            stock.Add(new Item(\"A-1\", \"Anchor\", 1m, 2));\n            Check.Throws<System.InvalidOperationException>(() => stock.Remove(\"A-1\", 3));\n        }\n    }\n}\n";
                using Workspace half = await Workspace.CreateAsync(Task("two-place-edit")).ConfigureAwait(false);
                half.ReplaceLast("tests/Inventory.Tests/StockTests.cs", "    }\n}\n", method);
                BatteryGrade unregistered = await half.GradeAsync("two-place-edit").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Failed, unregistered.Outcome, "method written but not registered");
                AssertEqual(2, unregistered.Details.Commands.Last().ExitCode, "the runner reports the missing registration");

                using Workspace both = await Workspace.CreateAsync(Task("two-place-edit")).ConfigureAwait(false);
                both.ReplaceLast("tests/Inventory.Tests/StockTests.cs", "    }\n}\n", method);
                both.Replace("tests/Inventory.Tests/TestRegistry.cs", "                new TestCase(nameof(PriceCalculatorTests",
                    "                new TestCase(nameof(StockTests.Stock_Remove_MoreThanAvailable_Throws), StockTests.Stock_Remove_MoreThanAvailable_Throws),\n                new TestCase(nameof(PriceCalculatorTests");
                BatteryGrade registered = await both.GradeAsync("two-place-edit").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Passed, registered.Outcome, registered.Reason + " " + Last(registered));
            }));

            cases.Add(CaseAsync("fix_bug", "Fix a bug: the task setup adds the failing test; fixing ApplyDiscount makes it pass", async () =>
            {
                using Workspace ws = await Workspace.CreateAsync(Task("fix-bug")).ConfigureAwait(false);
                BatteryGrade before = await ws.GradeAsync("fix-bug").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Failed, before.Outcome);
                AssertEqual(1, before.Details.Commands.Single().ExitCode, "a test fails, not a missing registration");
                AssertContains("FAIL PriceCalculator_ApplyDiscount_TenPercent", before.Details.Commands.Single().OutputExcerpt!);

                ws.Replace("src/Inventory/PriceCalculator.cs", "Math.Round(price - percent, 2", "Math.Round(price * (100 - percent) / 100, 2");
                BatteryGrade after = await ws.GradeAsync("fix-bug").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Passed, after.Outcome, after.Reason + " " + Last(after));
            }));

            cases.Add(CaseAsync("bom_crlf", "BOM and CRLF: the untouched file, a BOM-dropping edit and an LF edit fail with the cause; the exact edit passes", async () =>
            {
                string path = "src/Inventory/LegacyCodes.cs";
                using Workspace ws = await Workspace.CreateAsync(Task("bom-crlf")).ConfigureAwait(false);
                byte[] original = File.ReadAllBytes(ws.Full(path));
                AssertTrue(original.Length > 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF, "the fixture file has a BOM after the reset");
                AssertContains("\r\n", Encoding.UTF8.GetString(original), "the fixture file has CRLF after the reset");
                BatteryGrade untouched = await ws.GradeAsync("bom-crlf").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Failed, untouched.Outcome);
                AssertContains("content differs", untouched.Reason!);

                byte[] expected = File.ReadAllBytes(ws.Pack.Resolve("tasks/bom-crlf/expected/src/Inventory/LegacyCodes.cs"));
                File.WriteAllBytes(ws.Full(path), expected.Skip(3).ToArray());
                AssertContains("byte order mark was dropped", (await ws.GradeAsync("bom-crlf").ConfigureAwait(false)).Reason!);
                File.WriteAllBytes(ws.Full(path), Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(expected.Skip(3).ToArray()).Replace("\r\n", "\n")).Prepend((byte)0xBF).Prepend((byte)0xBB).Prepend((byte)0xEF).ToArray());
                AssertContains("CRLF line endings were converted to LF", (await ws.GradeAsync("bom-crlf").ConfigureAwait(false)).Reason!);

                File.WriteAllBytes(ws.Full(path), expected);
                BatteryGrade exact = await ws.GradeAsync("bom-crlf").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Passed, exact.Outcome, exact.Reason);
            }));

            cases.Add(CaseAsync("write_tests_mutation", "Write tests: no tests let every mutant survive; null-only tests kill one; full tests kill all four", async () =>
            {
                using Workspace none = await Workspace.CreateAsync(Task("write-tests")).ConfigureAwait(false);
                BatteryGrade untested = await none.GradeAsync("write-tests").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Failed, untested.Outcome);
                AssertEqual(4, untested.Details.Mutants.Count(m => m.Applied && !m.Killed), "all four mutants survive");
                AssertEqual((double?)0.0, untested.Score);
                AssertEqual(File.ReadAllText(none.Full("src/Inventory/TextUtil.cs")), File.ReadAllText(Path.Combine(none.Pack.Directory, "fixture", "src", "Inventory", "TextUtil.cs")), "mutants are reverted");

                using Workspace partial = await Workspace.CreateAsync(Task("write-tests")).ConfigureAwait(false);
                partial.AddTests(new[] { "Truncate_Null_Throws" });
                BatteryGrade some = await partial.GradeAsync("write-tests").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Failed, some.Outcome);
                AssertTrue(some.Details.Mutants.Single(m => m.Id == "drop-null-guard").Killed, "the null test kills the null-guard mutant");
                AssertEqual((double?)0.25, some.Score);

                using Workspace full = await Workspace.CreateAsync(Task("write-tests")).ConfigureAwait(false);
                full.AddTests(new[] { "Truncate_Null_Throws", "Truncate_TooSmall_Throws", "Truncate_ExactLength_Unchanged", "Truncate_Long_Cut" });
                BatteryGrade all = await full.GradeAsync("write-tests").ConfigureAwait(false);
                AssertEqual(BatteryTaskOutcomeEnum.Passed, all.Outcome, all.Reason);
                AssertEqual((double?)1.0, all.Score);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Battery: the C# pack against the real toolchain",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static BatteryPack Pack()
        {
            BatteryPack? pack = new BatteryPackCatalog(() => null).Find("csharp");
            AssertNotNull(pack, "C# pack");
            return pack!;
        }

        private static BatteryTaskDefinition Task(string id)
        {
            return Pack().Manifest!.Tasks.Single(t => t.Id == id);
        }

        private static string Last(BatteryGrade grade)
        {
            BatteryCommandOutcome? last = grade.Details.Commands.LastOrDefault();
            return last == null ? String.Empty : (last.CommandLine + ": " + last.OutputExcerpt);
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { TestTags.Process });
        }

        #endregion

        #region Private-Classes

        private sealed class Workspace : IDisposable
        {
            public BatteryPack Pack { get; }

            public IBatteryWorkspace Local { get; }

            private readonly string _Root;
            private readonly string _Worktree;
            private readonly TestDatabase _Db;

            private Workspace(BatteryPack pack, string root, string worktree, TestDatabase db, IBatteryWorkspace local)
            {
                Pack = pack;
                _Root = root;
                _Worktree = worktree;
                _Db = db;
                Local = local;
            }

            public static async Task<Workspace> CreateAsync(BatteryTaskDefinition? task)
            {
                BatteryPack pack = BatteryCSharpPackSuite.Pack();
                string root = Path.Combine(Path.GetTempPath(), "armada-battery-cs-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                BatteryFixtureService fixtures = new BatteryFixtureService(db.Driver, logging, Path.Combine(root, "data"));
                await fixtures.ResetAsync(pack, task, "test").ConfigureAwait(false);
                string worktree = Path.Combine(root, "wt");
                CannedBatteryTaskDispatcher.Git(root, "clone", "--quiet", fixtures.OriginPathFor(pack.Id), worktree);
                IBatteryWorkspace local = new DockBatteryWorkspace(new DockHostResolver(new GitService(logging), logging, null).Local, worktree);
                return new Workspace(pack, root, worktree, db, local);
            }

            public string Full(string relative)
            {
                return Path.Combine(_Worktree, relative.Replace('/', Path.DirectorySeparatorChar));
            }

            public void Replace(string relative, string find, string replace)
            {
                string text = File.ReadAllText(Full(relative));
                AssertTrue(text.Contains(find, StringComparison.Ordinal), "anchor in " + relative + ": " + find);
                File.WriteAllText(Full(relative), ReplaceFirst(text, find, replace));
            }

            public void ReplaceLast(string relative, string find, string replace)
            {
                string text = File.ReadAllText(Full(relative));
                int index = text.LastIndexOf(find, StringComparison.Ordinal);
                AssertTrue(index >= 0, "anchor in " + relative);
                File.WriteAllText(Full(relative), text.Substring(0, index) + replace + text.Substring(index + find.Length));
            }

            public void AddTests(string[] names)
            {
                Dictionary<string, string> bodies = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Truncate_Null_Throws"] = "Check.Throws<ArgumentNullException>(() => TextUtil.Truncate(null!, 5));",
                    ["Truncate_TooSmall_Throws"] = "Check.Throws<ArgumentOutOfRangeException>(() => TextUtil.Truncate(\"abcdef\", 2));",
                    ["Truncate_ExactLength_Unchanged"] = "Check.Equal(\"abcde\", TextUtil.Truncate(\"abcde\", 5));",
                    ["Truncate_Long_Cut"] = "Check.Equal(\"abc...\", TextUtil.Truncate(\"abcdefghij\", 6));"
                };
                StringBuilder sb = new StringBuilder();
                sb.Append("namespace Inventory.Tests\n{\n    using System;\n\n    /// <summary>\n    /// TextUtil tests.\n    /// </summary>\n    public static class TextUtilTests\n    {\n");
                foreach (string name in names)
                    sb.Append("        /// <summary>\n        /// ").Append(name).Append(".\n        /// </summary>\n        public static void ").Append(name).Append("()\n        {\n            ").Append(bodies[name]).Append("\n        }\n\n");
                sb.Append("    }\n}\n");
                File.WriteAllText(Full("tests/Inventory.Tests/TextUtilTests.cs"), sb.ToString());
                string registrations = String.Concat(names.Select(n => "                new TestCase(nameof(TextUtilTests." + n + "), TextUtilTests." + n + "),\n"));
                Replace("tests/Inventory.Tests/TestRegistry.cs", "                new TestCase(nameof(PriceCalculatorTests", registrations + "                new TestCase(nameof(PriceCalculatorTests");
            }

            public Task<BatteryGrade> GradeAsync(string taskId)
            {
                return BatteryTaskGrader.GradeAsync(Pack, Pack.Manifest!.Tasks.Single(t => t.Id == taskId), "done", Local);
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

            private static string ReplaceFirst(string text, string find, string replace)
            {
                int index = text.IndexOf(find, StringComparison.Ordinal);
                return text.Substring(0, index) + replace + text.Substring(index + find.Length);
            }
        }

        #endregion
    }
}
