namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for DockBoundaryScanner: protected-path blocking, CORE_RULE_5 secret
    /// detection, private-identifier denylist, and finding message quality.
    /// </summary>
    public class DockBoundaryScannerTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Dock Boundary Scanner";

        #region Private-Methods

        private static string MakeDiff(string path, string addedContent)
        {
            return "diff --git a/" + path + " b/" + path + "\n" +
                   "index 0000000..1111111 100644\n" +
                   "--- a/" + path + "\n" +
                   "+++ b/" + path + "\n" +
                   "@@ -0,0 +1 @@\n" +
                   "+" + addedContent + "\n";
        }

        private static string MakeDiffWithContext(string path, string contextLine, string addedContent, string deletedContent)
        {
            return "diff --git a/" + path + " b/" + path + "\n" +
                   "index 0000000..1111111 100644\n" +
                   "--- a/" + path + "\n" +
                   "+++ b/" + path + "\n" +
                   "@@ -1,3 +1,3 @@\n" +
                   " " + contextLine + "\n" +
                   "+" + addedContent + "\n" +
                   "-" + deletedContent + "\n";
        }

        private static DockBoundarySettings DefaultSettings()
        {
            return new DockBoundarySettings
            {
                SecretScanEnabled = true,
                PrivateIdentifierScanEnabled = true,
                PublicRepoPatterns = new List<string>(),
                PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>()
            };
        }

        private static DockBoundarySettings PublicRepoSettings(
            string publicPattern,
            string identifierLabel,
            string identifierPattern)
        {
            return new DockBoundarySettings
            {
                SecretScanEnabled = true,
                PrivateIdentifierScanEnabled = true,
                PublicRepoPatterns = new List<string> { publicPattern },
                PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>
                {
                    new DockBoundaryPrivateIdentifierEntry { Label = identifierLabel, Pattern = identifierPattern }
                }
            };
        }

        #endregion

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            DockBoundaryScanner scanner = new DockBoundaryScanner();

            // -----------------------------------------------------------------------
            // Protected-path findings
            // -----------------------------------------------------------------------

            await RunTest("CODEX.md is blocked by built-in protected paths", () =>
            {
                string diff = MakeDiff("CODEX.md", "placeholder");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "CODEX.md must be blocked by built-in paths");
                AssertEqual(1, result.Findings.Count);
                AssertEqual(DockBoundaryFindingKindEnum.ProtectedPath, result.Findings[0].Kind);
                AssertEqual("CODEX.md", result.Findings[0].Path);
                return Task.CompletedTask;
            });

            await RunTest("CURSOR.md is blocked by built-in protected paths", () =>
            {
                string diff = MakeDiff("CURSOR.md", "placeholder");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed);
                AssertEqual(DockBoundaryFindingKindEnum.ProtectedPath, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("AGENTS.md is blocked by built-in protected paths", () =>
            {
                string diff = MakeDiff("AGENTS.md", "placeholder");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed);
                AssertEqual(DockBoundaryFindingKindEnum.ProtectedPath, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("GEMINI.md is blocked by built-in protected paths", () =>
            {
                string diff = MakeDiff("GEMINI.md", "placeholder");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed);
                AssertEqual(DockBoundaryFindingKindEnum.ProtectedPath, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("MUX.md is blocked by built-in protected paths", () =>
            {
                string diff = MakeDiff("MUX.md", "placeholder");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed);
                AssertEqual(DockBoundaryFindingKindEnum.ProtectedPath, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("Subdirectory runtime instruction file is blocked (subdir/CODEX.md)", () =>
            {
                string diff = MakeDiff("subdir/CODEX.md", "placeholder");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "subdir/CODEX.md must be blocked by **/CODEX.md");
                return Task.CompletedTask;
            });

            await RunTest(".armada/LEARNED.md is blocked by built-in protected paths", () =>
            {
                // .armada/LEARNED.md is the LearnedFactsFile path
                string learnedPath = Armada.Core.Memory.LearnedFactsFile.RelativePath;
                string diff = MakeDiff(learnedPath, "placeholder");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, learnedPath + " must be blocked by built-in paths");
                AssertEqual(DockBoundaryFindingKindEnum.ProtectedPath, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("_briefing/context-pack.md is blocked by built-in protected paths", () =>
            {
                string diff = MakeDiff("_briefing/context-pack.md", "placeholder");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "_briefing/** must be blocked");
                AssertEqual(DockBoundaryFindingKindEnum.ProtectedPath, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("Vessel-configured protected path blocks a matching file", () =>
            {
                string diff = MakeDiff("generated/schema.json", "placeholder");
                List<string> vesselPaths = new List<string> { "generated/**" };
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, vesselPaths, DefaultSettings());
                AssertFalse(result.Passed, "Vessel-configured generated/** must block generated/schema.json");
                AssertEqual(DockBoundaryFindingKindEnum.ProtectedPath, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("Normal source file is not blocked by built-in paths", () =>
            {
                string diff = MakeDiff("src/Foo.cs", "public class Foo {}");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed, "src/Foo.cs should not be blocked");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // Secret scan findings
            // -----------------------------------------------------------------------

            await RunTest("RSA private key header in added line triggers Secret finding", () =>
            {
                string diff = MakeDiff("src/Config.cs", "-----BEGIN RSA PRIVATE KEY-----");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "RSA private key must trigger a Secret finding");
                AssertTrue(result.Findings.Count >= 1);
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                AssertEqual("src/Config.cs", result.Findings[0].Path);
                AssertContains("CORE_RULE_5", result.Findings[0].FindingLabel);
                return Task.CompletedTask;
            });

            await RunTest("EC private key header in added line triggers Secret finding", () =>
            {
                string diff = MakeDiff("src/Keys.cs", "-----BEGIN EC PRIVATE KEY-----");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed);
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("Password literal in added line triggers Secret finding", () =>
            {
                string diff = MakeDiff("src/Auth.cs", @"string password = ""supersecret123"";");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "Password literal must trigger a Secret finding");
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                AssertContains("CORE_RULE_5", result.Findings[0].FindingLabel);
                return Task.CompletedTask;
            });

            await RunTest("Seed literal in added line triggers Secret finding", () =>
            {
                string diff = MakeDiff("src/Wallet.cs", @"string seed = ""abandon ability able about above"";");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "Seed literal must trigger a Secret finding");
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                AssertContains("CORE_RULE_5_seed_literal", result.Findings[0].FindingLabel);
                return Task.CompletedTask;
            });

            await RunTest("Context line with secret does NOT trigger Secret finding", () =>
            {
                // Context lines start with ' ' (space), not '+'; they must be ignored.
                string diff = MakeDiffWithContext(
                    "src/Foo.cs",
                    "-----BEGIN RSA PRIVATE KEY-----",
                    "public class Foo {}",
                    "old line");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed, "Context lines must not trigger Secret findings");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            await RunTest("Deleted line with secret does NOT trigger Secret finding", () =>
            {
                // Deleted lines start with '-'; only '+' lines are scanned.
                string diff = "diff --git a/src/Foo.cs b/src/Foo.cs\n" +
                              "index 0000000..1111111 100644\n" +
                              "--- a/src/Foo.cs\n" +
                              "+++ b/src/Foo.cs\n" +
                              "@@ -1 +1 @@\n" +
                              "-string password = \"oldpass12345\";\n" +
                              "+// removed credential\n";
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed, "Deleted lines must not trigger Secret findings");
                return Task.CompletedTask;
            });

            await RunTest("Secret scan disabled means no Secret finding even for RSA key", () =>
            {
                DockBoundarySettings noSecrets = new DockBoundarySettings
                {
                    SecretScanEnabled = false,
                    PrivateIdentifierScanEnabled = true,
                    PublicRepoPatterns = new List<string>(),
                    PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>()
                };
                string diff = MakeDiff("src/Keys.cs", "-----BEGIN RSA PRIVATE KEY-----");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, noSecrets);
                AssertTrue(result.Passed, "SecretScanEnabled=false must suppress Secret findings");
                return Task.CompletedTask;
            });

            await RunTest("Secret finding message does not contain raw secret bytes", () =>
            {
                string secretToken = "AAABBBCCCDDDEEEFFFGGGHHHIIIJJJKKKLLLMMMNNN";
                string diff = MakeDiff("src/Token.cs", "Bearer " + secretToken);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed);
                foreach (DockBoundaryFinding finding in result.Findings)
                {
                    AssertFalse(finding.Message.Contains(secretToken),
                        "Finding message must not echo raw secret bytes");
                    AssertFalse(finding.FindingLabel.Contains(secretToken),
                        "Finding label must not echo raw secret bytes");
                    AssertTrue(finding.Message.Length > 0, "Finding message must be non-empty");
                }
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // Private-identifier findings
            // -----------------------------------------------------------------------

            await RunTest("Private identifier triggers finding when vessel matches public pattern", () =>
            {
                DockBoundarySettings settings = PublicRepoSettings(
                    "github.com/acme",
                    "internal-org-name",
                    @"ACME-INTERNAL-[0-9]+");

                string diff = MakeDiff("src/Readme.cs", "// ACME-INTERNAL-9999 reference");
                DockBoundaryScanResult result = scanner.Scan(
                    diff,
                    null,
                    "vsl_test",
                    "my-vessel",
                    "https://github.com/acme/repo.git",
                    null,
                    settings);

                AssertFalse(result.Passed, "Private identifier must trigger when vessel is public");
                AssertTrue(result.Findings.Count >= 1);
                DockBoundaryFinding finding = result.Findings[result.Findings.Count - 1];
                AssertEqual(DockBoundaryFindingKindEnum.PrivateIdentifier, finding.Kind);
                AssertEqual("internal-org-name", finding.FindingLabel);
                AssertContains("internal-org-name", finding.Message);
                return Task.CompletedTask;
            });

            await RunTest("Private identifier does NOT trigger when vessel does not match public pattern", () =>
            {
                DockBoundarySettings settings = PublicRepoSettings(
                    "github.com/acme",
                    "internal-org-name",
                    @"ACME-INTERNAL-[0-9]+");

                string diff = MakeDiff("src/Readme.cs", "// ACME-INTERNAL-9999 reference");
                DockBoundaryScanResult result = scanner.Scan(
                    diff,
                    null,
                    "vsl_test",
                    "my-vessel",
                    "https://github.com/private-org/repo.git",
                    null,
                    settings);

                AssertTrue(result.Passed, "Private identifier must NOT fire for a private vessel");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            await RunTest("Private identifier does NOT trigger when PrivateIdentifierScanEnabled is false", () =>
            {
                DockBoundarySettings settings = new DockBoundarySettings
                {
                    SecretScanEnabled = false,
                    PrivateIdentifierScanEnabled = false,
                    PublicRepoPatterns = new List<string> { "github.com/acme" },
                    PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>
                    {
                        new DockBoundaryPrivateIdentifierEntry { Label = "org", Pattern = @"ACME-INTERNAL" }
                    }
                };

                string diff = MakeDiff("src/Readme.cs", "// ACME-INTERNAL-9999 reference");
                DockBoundaryScanResult result = scanner.Scan(
                    diff,
                    null,
                    "vsl_test",
                    "my-vessel",
                    "https://github.com/acme/repo.git",
                    null,
                    settings);

                AssertTrue(result.Passed, "PrivateIdentifierScanEnabled=false must suppress findings");
                return Task.CompletedTask;
            });

            await RunTest("Private identifier finding message does not contain raw identifier value", () =>
            {
                string rawIdentifier = "ACME-INTERNAL-9999";
                DockBoundarySettings settings = PublicRepoSettings(
                    "github.com/acme",
                    "internal-org-name",
                    @"ACME-INTERNAL-[0-9]+");

                string diff = MakeDiff("src/File.cs", "ref = " + rawIdentifier + ";");
                DockBoundaryScanResult result = scanner.Scan(
                    diff,
                    null,
                    "vsl_test",
                    "my-vessel",
                    "https://github.com/acme/repo.git",
                    null,
                    settings);

                AssertFalse(result.Passed);
                foreach (DockBoundaryFinding finding in result.Findings)
                {
                    if (finding.Kind == DockBoundaryFindingKindEnum.PrivateIdentifier)
                    {
                        AssertFalse(finding.Message.Contains(rawIdentifier),
                            "Private identifier finding must not echo raw matched value");
                        AssertTrue(finding.Message.Length > 0, "Finding message must be non-empty");
                        AssertTrue(finding.FindingLabel.Length > 0, "Finding label must be non-empty");
                    }
                }
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // Every finding has non-empty message and label
            // -----------------------------------------------------------------------

            await RunTest("All finding types have non-empty actionable messages", () =>
            {
                DockBoundarySettings settings = PublicRepoSettings(
                    "github.com/acme",
                    "org-label",
                    @"ACME-SECRET-[0-9]+");

                // This diff touches a protected path AND contains a secret AND a private identifier.
                string diff =
                    MakeDiff("CODEX.md", "generated content") +
                    MakeDiff("src/Config.cs", "-----BEGIN RSA PRIVATE KEY-----") +
                    MakeDiff("src/Org.cs", "// ACME-SECRET-1234");

                DockBoundaryScanResult result = scanner.Scan(
                    diff,
                    null,
                    "vsl_test",
                    "my-vessel",
                    "https://github.com/acme/repo.git",
                    null,
                    settings);

                AssertFalse(result.Passed);
                foreach (DockBoundaryFinding finding in result.Findings)
                {
                    AssertTrue(finding.Message.Length > 0,
                        "Finding of kind " + finding.Kind + " must have a non-empty message");
                    AssertTrue(finding.FindingLabel.Length > 0,
                        "Finding of kind " + finding.Kind + " must have a non-empty label");
                }
                return Task.CompletedTask;
            });

            await RunTest("Null settings throws ArgumentNullException", () =>
            {
                bool threw = false;
                try
                {
                    scanner.Scan("", null, null, null, null, null, null!);
                }
                catch (System.ArgumentNullException)
                {
                    threw = true;
                }
                AssertTrue(threw, "Null settings must throw ArgumentNullException");
                return Task.CompletedTask;
            });

            await RunTest("Empty diff with changedFilePaths still checks protected paths", () =>
            {
                List<string> paths = new List<string> { "CURSOR.md" };
                DockBoundaryScanResult result = scanner.Scan(
                    null, paths, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "Protected path from changedFilePaths must block even with null diff");
                AssertEqual(DockBoundaryFindingKindEnum.ProtectedPath, result.Findings[0].Kind);
                return Task.CompletedTask;
            });
        }
    }
}
