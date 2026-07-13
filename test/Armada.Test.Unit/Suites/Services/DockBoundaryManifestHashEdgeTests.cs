namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Edge- and negative-path coverage for the SHA-256 manifest-hash allowlist added to
    /// the dock boundary secret scanner (<see cref="ConventionChecker.IsManifestHashAllowed"/>
    /// and its wiring in <see cref="DockBoundaryScanner"/>).
    ///
    /// These tests complement <c>DockBoundaryManifestHashTests</c> by exercising:
    /// per-line and per-file suppression granularity in multi-line/multi-file diffs; the
    /// exact digest-shape boundaries (case sensitivity, length); case-insensitive hash-field
    /// keywords; additional known manifest/lockfile filenames; null/empty input guards; and
    /// the SRI-form recognition branch, which the standard base64_chunk trigger does not
    /// reach through the Scan path (see Residual Risks in the mission report).
    ///
    /// All secret- and digest-shaped inputs are assembled at runtime so this file's raw
    /// source does not itself trip CORE_RULE_5 when scanned by the dock boundary gate.
    /// </summary>
    public sealed class DockBoundaryManifestHashEdgeTests : TestSuite
    {
        // Synthetic 64-char lowercase-hex SHA-256 digest placeholder (no real secret).
        // Split into 16-char segments so no single source literal is a >=40-char base64 run.
        private static readonly string _SyntheticHexDigest =
            "a1b2c3d4e5f6a7b8" +
            "c9d0e1f2a3b4c5d6" +
            "e7f8a9b0c1d2e3f4" +
            "a5b6c7d8e9f0a1b2";

        // A second, distinct synthetic digest for multi-line diffs.
        private static readonly string _SyntheticHexDigest2 =
            "0f1e2d3c4b5a6978" +
            "8796a5b4c3d2e1f0" +
            "1122334455667788" +
            "99aabbccddeeff00";

        // Synthetic SRI base64 value (sha256- prefix + 43 base64 chars + '='). Not a real hash.
        private const string _SyntheticSriValue =
            "sha256-AAECBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiM=";

        private const string _BaseChunkRule = "CORE_RULE_5_base64_chunk";

        /// <summary>Suite name.</summary>
        public override string Name => "Dock Boundary Manifest Hash Allowlist (Edge Cases)";

        #region Private-Methods

        /// <summary>Builds a single-file unified diff whose added lines are the supplied content.</summary>
        private static string FileBlock(string path, params string[] addedLines)
        {
            string block =
                "diff --git a/" + path + " b/" + path + "\n" +
                "index 0000000..1111111 100644\n" +
                "--- a/" + path + "\n" +
                "+++ b/" + path + "\n" +
                "@@ -0,0 +1," + addedLines.Length + " @@\n";
            foreach (string added in addedLines)
            {
                block += "+" + added + "\n";
            }
            return block;
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

        // Assembled at runtime so the raw source never contains a firing api_key literal.
        private static string ApiKeyLine()
        {
            return "api_key = \"" + "ABCDEF1234567890ABCDEF" + "\"";
        }

        #endregion

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            DockBoundaryScanner scanner = new DockBoundaryScanner();

            // -----------------------------------------------------------------------
            // Suppression granularity: a manifest hash and a genuine secret can share
            // the same file; only the hash line is suppressed.
            // -----------------------------------------------------------------------

            await RunTest("Manifest hash suppressed but genuine secret on another line in same file STILL flagged", () =>
            {
                string hashLine = "\"sha256\": \"" + _SyntheticHexDigest + "\"";
                string diff = FileBlock("package-lock.json", hashLine, ApiKeyLine());
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed,
                    "A genuine secret on a sibling line must still fail the scan");
                AssertEqual(1, result.Findings.Count,
                    "Only the api_key line should produce a finding; the hash line is suppressed");
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                AssertEqual("CORE_RULE_5_apikey_literal", result.Findings[0].FindingLabel);
                return Task.CompletedTask;
            });

            await RunTest("Suppression is file-scoped: manifest hash exempt in lockfile, bare hex still flagged in source file", () =>
            {
                string hashLine = "\"sha256\": \"" + _SyntheticHexDigest + "\"";
                string bareHexLine = "token = \"" + _SyntheticHexDigest2 + "\"";
                string diff = FileBlock("package-lock.json", hashLine) +
                              FileBlock("src/Config.cs", bareHexLine);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed,
                    "The bare-hex token in the source file must still be flagged");
                AssertEqual(1, result.Findings.Count,
                    "Only the source-file bare-hex line should be flagged");
                AssertEqual("src/Config.cs", result.Findings[0].Path);
                AssertEqual(_BaseChunkRule, result.Findings[0].FindingLabel);
                return Task.CompletedTask;
            });

            await RunTest("Two manifest hash lines in one file are both suppressed", () =>
            {
                string line1 = "\"sha256\": \"" + _SyntheticHexDigest + "\"";
                string line2 = "\"digest\": \"" + _SyntheticHexDigest2 + "\"";
                string diff = FileBlock("dist/manifest.json", line1, line2);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed, "Both manifest digest lines must be suppressed");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // File-type suppression path exercised through Scan (no hash keyword):
            // a bare quoted digest in a known lockfile is exempt purely on file type.
            // -----------------------------------------------------------------------

            await RunTest("Bare quoted digest with NO hash keyword in a .lockfile is suppressed via file type", () =>
            {
                // No hash-field keyword on the line; exemption must come from the file being a lockfile.
                string line = "\"" + _SyntheticHexDigest + "\"";
                string diff = FileBlock("deps.lockfile", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed,
                    ".lockfile digest with no keyword must be suppressed on file type alone");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // Digest-shape boundaries: the exemption recognizes ONLY exactly-64
            // lowercase hex. Uppercase or off-by-one length is NOT a digest and the
            // base64_chunk match remains flagged even in a manifest hash field.
            // -----------------------------------------------------------------------

            await RunTest("Uppercase 64-hex in a manifest hash field is STILL flagged (lowercase-only policy)", () =>
            {
                // Uppercase hex is still valid base64, so base64_chunk fires, but the digest
                // pattern is lowercase-only, so the token is not recognized as a content digest.
                string upper = _SyntheticHexDigest.ToUpperInvariant();
                string line = "\"sha256\": \"" + upper + "\"";
                string diff = FileBlock("package-lock.json", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed,
                    "Uppercase 64-hex is not a recognized digest and must remain flagged");
                AssertEqual(_BaseChunkRule, result.Findings[0].FindingLabel);
                return Task.CompletedTask;
            });

            await RunTest("65-hex token in a manifest hash field is STILL flagged (exactly-64 boundary)", () =>
            {
                // 65 contiguous hex chars have no internal word boundary, so the exactly-64
                // digest pattern does not match; base64_chunk still fires and stays flagged.
                string tooLong = _SyntheticHexDigest + "a";
                string line = "\"sha256\": \"" + tooLong + "\"";
                string diff = FileBlock("package-lock.json", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed,
                    "A 65-char hex token is not a 64-char digest and must remain flagged");
                AssertEqual(_BaseChunkRule, result.Findings[0].FindingLabel);
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // ConventionChecker.IsManifestHashAllowed direct edge/negative coverage.
            // -----------------------------------------------------------------------

            await RunTest("IsManifestHashAllowed recognizes the SRI sha256-base64 form (hex branch not required)", () =>
            {
                // Exercises the hasSriDigest branch directly. Through the real Scan path the
                // base64_chunk rule does not fire on an SRI line (the 'sha256-' prefix breaks
                // the quoted base64 run), so this branch is otherwise only reachable here.
                string line = "\"integrity\": \"" + _SyntheticSriValue + "\"";
                AssertTrue(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, line, "src/Config.cs"),
                    "SRI sha256-<base64> value should be recognized as a content digest");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed is true when filePath is null but the line has a hash keyword", () =>
            {
                string line = "\"sha256\": \"" + _SyntheticHexDigest + "\"";
                AssertTrue(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, line, null),
                    "A hash-field keyword should exempt even when the file path is unknown");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed is false when filePath is null and there is no hash keyword", () =>
            {
                string line = "value = \"" + _SyntheticHexDigest + "\"";
                AssertFalse(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, line, null),
                    "A digest with no context signal and unknown file must not be exempted");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed is false for empty and null added lines", () =>
            {
                AssertFalse(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, "", "package-lock.json"),
                    "Empty added line must not be exempted");
                AssertFalse(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, null!, "package-lock.json"),
                    "Null added line must not be exempted");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed hash-field keyword match is case-insensitive", () =>
            {
                // Uppercase keyword in a non-manifest file: exemption must still come from the keyword.
                string line = "\"INTEGRITY\": \"" + _SyntheticHexDigest + "\"";
                AssertTrue(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, line, "src/Config.cs"),
                    "Uppercase hash-field keyword should still provide hash context");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed is false for uppercase 64-hex even in a manifest file", () =>
            {
                // The digest pattern is lowercase-only; an uppercase token is not a recognized digest.
                string upper = _SyntheticHexDigest.ToUpperInvariant();
                string line = "value = \"" + upper + "\"";
                AssertFalse(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, line, "package-lock.json"),
                    "Uppercase hex is not a recognized digest, so no exemption applies");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed is false for a 65-char hex token even in a manifest file", () =>
            {
                string tooLong = _SyntheticHexDigest + "a";
                string line = "value = \"" + tooLong + "\"";
                AssertFalse(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, line, "package-lock.json"),
                    "A 65-char hex token is not an exactly-64 digest, so no exemption applies");
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // Additional known manifest/lockfile filenames recognized by file type
            // (bare digest, no hash keyword -> exemption must come from the filename).
            // -----------------------------------------------------------------------

            await RunTest("IsManifestHashAllowed recognizes additional known manifest filenames by file type", () =>
            {
                string bareDigestLine = "value = \"" + _SyntheticHexDigest + "\"";
                AssertTrue(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, bareDigestLine, "npm-shrinkwrap.json"),
                    "npm-shrinkwrap.json should be a known manifest file");
                AssertTrue(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, bareDigestLine, "pnpm-lock.yaml"),
                    "pnpm-lock.yaml should be a known manifest file");
                AssertTrue(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, bareDigestLine, "go.sum"),
                    "go.sum should be a known manifest file");
                AssertTrue(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, bareDigestLine, "vendor/composer-lock.json"),
                    "*-lock.json suffix should qualify as a known manifest file");
                AssertTrue(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, bareDigestLine, "build/out.lockfile"),
                    ".lockfile extension should qualify as a known manifest file");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed is false for a non-manifest JSON file with a bare digest", () =>
            {
                // appsettings.json is not a lockfile; a bare digest with no keyword is not exempt.
                string bareDigestLine = "value = \"" + _SyntheticHexDigest + "\"";
                AssertFalse(ConventionChecker.IsManifestHashAllowed(_BaseChunkRule, bareDigestLine, "src/appsettings.json"),
                    "A plain .json file is not a known manifest/lockfile");
                return Task.CompletedTask;
            });
        }
    }
}
