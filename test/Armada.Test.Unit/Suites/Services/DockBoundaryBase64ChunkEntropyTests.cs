namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Regression coverage for the CORE_RULE_5_base64_chunk entropy gate (obj_msfid367).
    /// The raw regex matches any double-quoted 40+ char base64-alphabet run, which
    /// false-positived on long CamelCase identifiers, slash-joined path lists, and
    /// hex-ID runs inside single-line JSON catalogs (source-glossary
    /// certified-command-catalog.json blocked every landing). The gate fires only when
    /// a run looks like genuine base64 key/seed/password material: hex-alphabet runs
    /// never fire, and a run fires on a structural branch (balanced case, meaningful
    /// upper/lower/digit-or-slash fractions) or an entropy branch (Shannon entropy
    /// above 4.6 bits/char with case balance at or below 0.62).
    ///
    /// Every long literal below is assembled at runtime from segments under 40 chars so
    /// this file's raw source never carries a firing quoted run under the dock boundary
    /// gate. Identifier and path samples are the actual shapes observed in the catalog.
    /// </summary>
    public sealed class DockBoundaryBase64ChunkEntropyTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Dock Boundary Base64 Chunk Entropy Gate";

        #endregion

        #region Private-Members

        private const string _BaseChunkRule = "CORE_RULE_5_base64_chunk";

        // Real catalog identifier shapes: CamelCase words plus digit suffix.
        private static string CatalogIdentifier1()
        {
            return "Cummins" + "Request" + "AndVerify" + "Response" + "Step" + "J1939";
        }

        private static string CatalogIdentifier2()
        {
            return "International" + "WritableParameter" + "LargeGrid" + "Dlg";
        }

        // Slash-joined action-request path list.
        private static string CatalogPathList()
        {
            return "ActionRequests" + "/" + "RequestWriteDataByLocalIdentifier" + "/" + "KLine" + "/" + "Step";
        }

        // 64-char lowercase hex ID run (the hex-ID shape that previously tripped the gate).
        private static string HexIdRunLower()
        {
            return "a1b2c3d4e5f6a7b8" +
                   "c9d0e1f2a3b4c5d6" +
                   "e7f8a9b0c1d2e3f4" +
                   "a5b6c7d8e9f0a1b2";
        }

        private static string HexIdRunUpper()
        {
            return HexIdRunLower().ToUpperInvariant();
        }

        // Mixed-case hex run: upper and lower hex plus digits.
        private static string HexIdRunMixed()
        {
            return "AbCdEf0123456789" +
                   "aBcDeF0123456789" +
                   "9876543210FeDcBa" +
                   "0f1e2d3c4b5a6978";
        }

        // Genuine 256-bit key material: base64 of the fixed byte sequence 1..32.
        // Built at runtime so no source literal is a real secret-shaped run.
        private static string GenuineKeyChunk()
        {
            byte[] bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 1);
            return Convert.ToBase64String(bytes);
        }

        // Base64 encoding of the ASCII alphabet string (the seedKeyBlob test shape).
        private static string AlphabetChunk()
        {
            return "QUJDRGVmZ2hJSktMbW5vUHFyU3R1Vld4WXowMTIz" +
                   "NDU2Nzg5K2Yv";
        }

        // Real random-looking chunk whose case split is skewed (upper-heavy): fires only
        // through the entropy branch (entropy >= 4.6, balance <= 0.62, structural fails).
        private static string UnbalancedHighEntropyChunk()
        {
            return "5LRHFMWRPN3LYIIWKAm6n77LYQnn57HOXD" +
                   "LILF" + "/" + "QJ4L" + "/" + "cJXtU9D8J9dF";
        }

        // Single-line JSON catalog line in the certified-command-catalog.json shape:
        // identifier names, slash-joined flow paths, and a hex ID run on ONE line.
        private static string SingleLineCatalogLine(bool withSecret)
        {
            string secretField = withSecret
                ? ",\"seed\":\"" + GenuineKeyChunk() + "\""
                : "";
            return "{\"certifiedCommands\":[{\"id\":\"c1\",\"commandName\":\"" +
                   CatalogIdentifier1() +
                   "\",\"flowPath\":\"" + CatalogPathList() +
                   "\",\"requestId\":\"" + HexIdRunLower() + "\"}" +
                   ",\"id\":\"c2\",\"commandName\":\"" + CatalogIdentifier2() +
                   "\",\"flowPath\":\"Dialogs" + "/" + "WritableParameter" + "/" +
                   "LargeGrid" + "/" + "International\"" +
                   secretField + "]}";
        }

        private static string MakeDiff(string path, string addedContent)
        {
            return "diff --git a/" + path + " b/" + path + "\n" +
                   "index 0000000..1111111 100644\n" +
                   "--- a/" + path + "\n" +
                   "+++ b/" + path + "\n" +
                   "@@ -0,0 +1 @@\n" +
                   "+" + addedContent + "\n";
        }

        private static DockBoundarySettings DefaultSettings()
        {
            return new DockBoundarySettings
            {
                SecretScanEnabled = true,
                PrivateIdentifierScanEnabled = false,
                PublicRepoPatterns = new List<string>(),
                PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>()
            };
        }

        private static bool FiredBase64Chunk(IReadOnlyList<string> fired)
        {
            foreach (string rule in fired)
            {
                if (String.Equals(rule, _BaseChunkRule, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            // -----------------------------------------------------------------------
            // CheckSecretLine: catalog-shaped non-secrets must be suppressed
            // -----------------------------------------------------------------------

            await RunTest("CheckSecretLine suppresses a CamelCase catalog identifier", () =>
            {
                string line = "\"commandName\": \"" + CatalogIdentifier1() + "\",";
                IReadOnlyList<string> fired = ConventionChecker.CheckSecretLine(line);
                AssertEqual(0, fired.Count,
                    "A CamelCase identifier must not fire CORE_RULE_5_base64_chunk");
                return Task.CompletedTask;
            });

            await RunTest("CheckSecretLine suppresses a slash-joined path list", () =>
            {
                string line = "\"flowPath\": \"" + CatalogPathList() + "\",";
                IReadOnlyList<string> fired = ConventionChecker.CheckSecretLine(line);
                AssertEqual(0, fired.Count,
                    "A slash-joined path list must not fire CORE_RULE_5_base64_chunk");
                return Task.CompletedTask;
            });

            await RunTest("CheckSecretLine suppresses hex-ID runs in lower, upper, and mixed case", () =>
            {
                AssertEqual(0, ConventionChecker.CheckSecretLine("\"id\": \"" + HexIdRunLower() + "\"").Count,
                    "Lowercase hex-ID run must be suppressed");
                AssertEqual(0, ConventionChecker.CheckSecretLine("\"id\": \"" + HexIdRunUpper() + "\"").Count,
                    "Uppercase hex-ID run must be suppressed");
                AssertEqual(0, ConventionChecker.CheckSecretLine("\"id\": \"" + HexIdRunMixed() + "\"").Count,
                    "Mixed-case hex-ID run must be suppressed");
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // CheckSecretLine: genuine secret material must still fire
            // -----------------------------------------------------------------------

            await RunTest("CheckSecretLine fires on a genuine base64 key chunk", () =>
            {
                string line = "\"seed\": \"" + GenuineKeyChunk() + "\"";
                IReadOnlyList<string> fired = ConventionChecker.CheckSecretLine(line);
                AssertTrue(FiredBase64Chunk(fired), "A genuine base64 key must fire the base64-chunk rule");
                return Task.CompletedTask;
            });

            await RunTest("CheckSecretLine fires on a base64 alphabet-string chunk", () =>
            {
                string line = "\"seedKeyBlob\": \"" + AlphabetChunk() + "\",";
                IReadOnlyList<string> fired = ConventionChecker.CheckSecretLine(line);
                AssertTrue(FiredBase64Chunk(fired),
                    "A base64 alphabet-string chunk must still fire (existing seedKeyBlob policy)");
                return Task.CompletedTask;
            });

            await RunTest("CheckSecretLine fires on an unbalanced-case high-entropy chunk via the entropy branch", () =>
            {
                string line = "\"token\": \"" + UnbalancedHighEntropyChunk() + "\"";
                IReadOnlyList<string> fired = ConventionChecker.CheckSecretLine(line);
                AssertTrue(FiredBase64Chunk(fired),
                    "A skewed-case high-entropy chunk must fire through the entropy branch");
                return Task.CompletedTask;
            });

            await RunTest("CheckSecretLine fires when a line mixes a suppressed identifier and a real secret", () =>
            {
                string line = "\"commandName\": \"" + CatalogIdentifier1() +
                              "\",\"token\": \"" + GenuineKeyChunk() + "\"";
                IReadOnlyList<string> fired = ConventionChecker.CheckSecretLine(line);
                AssertTrue(FiredBase64Chunk(fired),
                    "Any high-entropy run on the line must fire, even with a benign identifier present");
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // LooksLikeBase64Secret guards
            // -----------------------------------------------------------------------

            await RunTest("LooksLikeBase64Secret rejects empty, short, and hex-only input", () =>
            {
                AssertFalse(ConventionChecker.LooksLikeBase64Secret(""), "Empty chunk must be rejected");
                AssertFalse(ConventionChecker.LooksLikeBase64Secret(null!), "Null chunk must be rejected");
                AssertFalse(ConventionChecker.LooksLikeBase64Secret("A"), "Single-char chunk must be rejected");
                AssertFalse(ConventionChecker.LooksLikeBase64Secret(HexIdRunLower()),
                    "Hex-only chunk must be rejected");
                AssertFalse(ConventionChecker.LooksLikeBase64Secret(HexIdRunUpper()),
                    "Uppercase hex-only chunk must be rejected");
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // DockBoundaryScanner through the full gate: single-line JSON catalog
            // -----------------------------------------------------------------------

            await RunTest("Single-line JSON catalog with identifiers, paths, and hex IDs passes the scan", () =>
            {
                string diff = MakeDiff("unified/certified-command-catalog.json", SingleLineCatalogLine(false));
                DockBoundaryScanResult result = new DockBoundaryScanner().Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed,
                    "A single-line JSON catalog of identifiers/paths/hex IDs must pass the dock boundary scan");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            await RunTest("Single-line JSON catalog with a genuine base64 secret is blocked", () =>
            {
                string diff = MakeDiff("unified/certified-command-catalog.json", SingleLineCatalogLine(true));
                DockBoundaryScanResult result = new DockBoundaryScanner().Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed,
                    "A real base64 secret embedded in the same catalog shape must block the scan");
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                AssertEqual(_BaseChunkRule, result.Findings[0].FindingLabel);
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // ConventionChecker.Check (diff-level evaluator) uses the same gate
            // -----------------------------------------------------------------------

            await RunTest("ConventionChecker.Check passes a diff of catalog identifiers", () =>
            {
                ConventionCheckResult result = new ConventionChecker().Check(
                    MakeDiff("unified/certified-command-catalog.json", SingleLineCatalogLine(false)));
                AssertTrue(result.Passed,
                    "The diff-level evaluator must also suppress catalog identifiers");
                AssertEqual(0, result.Violations.Count);
                return Task.CompletedTask;
            });

            await RunTest("ConventionChecker.Check still flags a real secret in a diff", () =>
            {
                ConventionCheckResult result = new ConventionChecker().Check(
                    MakeDiff("unified/certified-command-catalog.json", SingleLineCatalogLine(true)));
                AssertFalse(result.Passed,
                    "The diff-level evaluator must still flag a genuine base64 secret");
                AssertContains(_BaseChunkRule, result.Violations[0].Rule);
                return Task.CompletedTask;
            });
        }

        #endregion
    }
}
