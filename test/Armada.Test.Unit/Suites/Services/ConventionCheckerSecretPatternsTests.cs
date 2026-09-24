namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for <see cref="ConventionChecker.BuiltInSecretPatternStrings"/>, the surface
    /// that feeds the dock boundary hook config with CORE_RULE_5 secret patterns. Verifies
    /// the exported strings are usable regexes, actually detect known secrets, and agree
    /// with the server-side <see cref="ConventionChecker.CheckSecretLine"/> gate while
    /// excluding non-secret convention rules.
    /// </summary>
    public sealed class ConventionCheckerSecretPatternsTests : TestSuite
    {
        private const string _RsaHeader = "-----BEGIN RSA PRIVATE KEY-----";

        /// <summary>Suite name.</summary>
        public override string Name => "Convention Checker Secret Patterns";


        private sealed class ParityFixture
        {
            public ParityFixture(string line, bool secret) { Line = line; Secret = secret; }
            public string Line { get; }
            public bool Secret { get; }
        }

        private sealed class ProcessRun
        {
            public int ExitCode { get; set; }
            public string Output { get; set; } = "";
        }

        private sealed class HookVerdict
        {
            public bool PreCommit { get; set; }
            public bool PrePush { get; set; }
        }

        // A PEM private-key header, assembled so this source file carries no literal header.
        private static string PemHeader(string kind) => "-----BEGIN " + kind + "PRIVATE" + " KEY-----";

        // Lines the server gate and both dock hooks must judge alike: the added line, and whether it is secret material.
        private static readonly ParityFixture[] _ParityFixtures =
        {
            new ParityFixture(PemHeader("RSA "), true),
            new ParityFixture(PemHeader("OPENSSH "), true),
            new ParityFixture(PemHeader("ENCRYPTED "), true),
            new ParityFixture(PemHeader("DSA "), true),
            new ParityFixture(PemHeader(""), true),
            new ParityFixture("Authorization: Bearer AbCdEfGhIjKlMnOpQrStUv12", true),
            new ParityFixture("authorization: bearer abcdefghij0123456789XY", true),
            new ParityFixture("Password = \"hunter2hunter2\"", true),
            new ParityFixture("API_KEY = \"" + "ABCDEFGHIJKLMNOP01" + "\"", true),
            new ParityFixture("Seed: \"abcd1234efgh\"", true),
            new ParityFixture("var key = \"q8Zr4TmW2xLp9VbN3cKd7HsJ5fGy1AeU6oRi0nTw\";", true),
            // Random bytes with padding: measured without the padding, the run falls below both branches.
            new ParityFixture("var key = \"YVHHYkQ2ZvHPxYaahuMNQejAuj2lxgMdSyCaJuVCDg==\";", false),
            // Base64 of plain text, padded.
            new ParityFixture("var note = \"cmVsZWFzZSBub3RlcyBzdGFnaW5nIGZvciBoZWxsbw==\";", false),
            new ParityFixture("var name = \"TruncatesLongestLeafAndStaysValidJsonText\";", false),
            new ParityFixture("using Moq;", false),
            new ParityFixture("string password = GetPassword();", false),
        };

        private static string? FindSh()
        {
            if (File.Exists("/bin/sh")) return "/bin/sh";
            foreach (string candidate in new[] { @"C:\Program Files\Git\bin\sh.exe", @"C:\Program Files (x86)\Git\bin\sh.exe" })
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        private static ProcessRun Run(string workingDirectory, string fileName, string? stdin, params string[] args)
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = stdin != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            using (Process process = Process.Start(info)!)
            {
                if (stdin != null)
                {
                    process.StandardInput.Write(stdin);
                    process.StandardInput.Close();
                }
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return new ProcessRun { ExitCode = process.ExitCode, Output = output };
            }
        }

        private static string Git(string repo, params string[] args)
        {
            string[] full = new string[args.Length + 4];
            full[0] = "-c"; full[1] = "user.name=Parity"; full[2] = "-c"; full[3] = "user.email=parity@example.invalid";
            Array.Copy(args, 0, full, 4, args.Length);
            ProcessRun run = Run(repo, "git", null, full);
            if (run.ExitCode != 0) throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + run.Output);
            return run.Output.Trim();
        }

        // Run both installed hooks over one added line in a fresh repository carrying the generated patterns
        // file, as a dock would, and return whether each blocked.
        private static HookVerdict HookVerdicts(string sh, string line)
        {
            string repo = Path.Combine(Path.GetTempPath(), "armada-secret-parity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repo);
            try
            {
                Git(repo, "init", "-q", "-b", "main");
                Directory.CreateDirectory(Path.Combine(repo, ".armada"));
                File.WriteAllText(Path.Combine(repo, ".armada", "boundary.patterns"),
                    DockService.BoundaryPatternsFileContent(ConventionChecker.BuiltInSecretPatternStrings, new List<string>()));
                File.WriteAllText(Path.Combine(repo, "pre-commit"), DockService.PreCommitHookScript);
                File.WriteAllText(Path.Combine(repo, "pre-push"), DockService.PrePushHookScript);
                File.WriteAllText(Path.Combine(repo, "added.txt"), line + "\n");
                Git(repo, "add", "added.txt");

                bool preCommit = Run(repo, sh, null, "pre-commit").ExitCode != 0;

                Git(repo, "commit", "-q", "-m", "fixture");
                string sha = Git(repo, "rev-parse", "HEAD");
                string zero = new string('0', 40);
                bool prePush = Run(repo, sh, "refs/heads/main " + sha + " refs/heads/main " + zero + "\n", "pre-push", "origin", "https://example.invalid/repo.git").ExitCode != 0;
                return new HookVerdict { PreCommit = preCommit, PrePush = prePush };
            }
            finally
            {
                try { Directory.Delete(repo, true); } catch (Exception) { }
            }
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("BuiltInSecretPatternStrings is non-empty", () =>
            {
                IReadOnlyList<string> patterns = ConventionChecker.BuiltInSecretPatternStrings;
                AssertNotNull(patterns, "Exported secret patterns must not be null");
                AssertTrue(patterns.Count > 0, "Exported secret patterns must not be empty");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Every exported secret pattern is a compilable regex", () =>
            {
                foreach (string pattern in ConventionChecker.BuiltInSecretPatternStrings)
                {
                    AssertTrue(pattern.Length > 0, "Exported pattern must be a non-empty string");
                    // Will throw if the exported string is not a valid regex.
                    Regex compiled = new Regex(pattern);
                    AssertNotNull(compiled, "Exported pattern must compile to a Regex");
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("At least one exported pattern matches a known RSA private key header", () =>
            {
                bool anyMatch = false;
                foreach (string pattern in ConventionChecker.BuiltInSecretPatternStrings)
                {
                    if (new Regex(pattern).IsMatch(_RsaHeader)) { anyMatch = true; break; }
                }
                AssertTrue(anyMatch, "Exported patterns must detect a known private-key header so the hook config blocks it");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Exported patterns agree with CheckSecretLine on a secret line", () =>
            {
                // The server-side gate (CheckSecretLine) and the exported hook patterns must
                // agree that the same line is a secret, otherwise the dock hook and the gate diverge.
                IReadOnlyList<string> serverFired = ConventionChecker.CheckSecretLine(_RsaHeader);
                AssertTrue(serverFired.Count > 0, "CheckSecretLine must flag the RSA header as a secret");

                bool exportedMatch = false;
                foreach (string pattern in ConventionChecker.BuiltInSecretPatternStrings)
                {
                    if (new Regex(pattern).IsMatch(_RsaHeader)) { exportedMatch = true; break; }
                }
                AssertTrue(exportedMatch, "Exported hook patterns must flag the same line the server gate flags");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Exported patterns exclude non-secret convention rules (using Moq)", () =>
            {
                // CORE_RULE_2 (mocking-lib) is a convention rule, not a secret rule, and must not
                // leak into the secret-only hook config. A benign mocking-import line must match none.
                string mockingLine = "using Moq;";
                foreach (string pattern in ConventionChecker.BuiltInSecretPatternStrings)
                {
                    AssertFalse(new Regex(pattern).IsMatch(mockingLine),
                        "Non-secret convention rules must not appear among exported secret patterns");
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            await RunTest("TheServerGate_JudgesEachFixtureAsSecretOrNot", () =>
            {
                foreach (ParityFixture fixture in _ParityFixtures)
                {
                    bool fired = ConventionChecker.CheckSecretLine(fixture.Line).Count > 0;
                    AssertEqual(fixture.Secret, fired, "server gate on: " + fixture.Line);
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("BothDockHooks_AgreeWithTheServerGate_OnEveryFixture", () =>
            {
                string? sh = FindSh();
                if (sh == null)
                {
                    Console.WriteLine("  [SKIP] no POSIX sh on this host; hook parity not executed");
                    return Task.CompletedTask;
                }

                List<string> disagreements = new List<string>();
                foreach (ParityFixture fixture in _ParityFixtures)
                {
                    bool server = ConventionChecker.CheckSecretLine(fixture.Line).Count > 0;
                    HookVerdict hooks = HookVerdicts(sh, fixture.Line);
                    if (hooks.PreCommit != server || hooks.PrePush != server)
                        disagreements.Add("[server=" + server + " pre-commit=" + hooks.PreCommit + " pre-push=" + hooks.PrePush + "] " + fixture.Line);
                }
                AssertEqual(0, disagreements.Count, "hook and server disagree:\n  " + String.Join("\n  ", disagreements));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
