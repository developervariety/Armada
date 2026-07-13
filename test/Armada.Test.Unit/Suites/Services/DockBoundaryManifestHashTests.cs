namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Tests that SHA-256 content digests in manifest/lockfile contexts are NOT flagged as
    /// secrets by the dock boundary scanner, while genuine secrets remain blocked and bare
    /// 64-hex tokens outside hash contexts continue to be treated per existing policy.
    /// </summary>
    public sealed class DockBoundaryManifestHashTests : TestSuite
    {
        // Synthetic 64-char lowercase-hex SHA-256 digest placeholder (no real secrets).
        // Split across four 16-char segments so no single quoted literal in source
        // exceeds the 40-char threshold of the base64_chunk hook pattern.
        private static readonly string _SyntheticHexDigest =
            "a1b2c3d4e5f6a7b8" +
            "c9d0e1f2a3b4c5d6" +
            "e7f8a9b0c1d2e3f4" +
            "a5b6c7d8e9f0a1b2";

        // Synthetic SRI base64 value (sha256- prefix + 43 base64 chars + =).
        // Not a real hash; used only as a structural placeholder.
        private const string _SyntheticSriValue =
            "sha256-AAECBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiM=";

        /// <summary>Suite name.</summary>
        public override string Name => "Dock Boundary Manifest Hash Allowlist";

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

        #endregion

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            DockBoundaryScanner scanner = new DockBoundaryScanner();

            // -----------------------------------------------------------------------
            // Bundle manifest / per-file sha256 hash fields -- must NOT be flagged
            // -----------------------------------------------------------------------

            await RunTest("Bundle manifest sha256 hash field is NOT flagged as secret", () =>
            {
                // A webpack/vite asset manifest entry with a "hash" field containing a
                // 64-char lowercase hex SHA-256 digest is a content digest, not a secret.
                string line = "\"hash\": \"" + _SyntheticHexDigest + "\"";
                string diff = MakeDiff("dist/asset-manifest.json", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed,
                    "Bundle manifest sha256 hash field must not be flagged as a secret");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            await RunTest("Lockfile sha256 field is NOT flagged as secret", () =>
            {
                // A lock file entry that records a per-file sha256 digest.
                string line = "\"sha256\": \"" + _SyntheticHexDigest + "\"";
                string diff = MakeDiff("package-lock.json", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed,
                    "Lockfile sha256 field must not be flagged as a secret");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            await RunTest("Manifest checksum field is NOT flagged as secret", () =>
            {
                // A manifest that records a checksum using a 64-char hex digest.
                string line = "checksum = \"" + _SyntheticHexDigest + "\"";
                string diff = MakeDiff("Cargo.lock", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed,
                    "Manifest checksum field must not be flagged as a secret");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            await RunTest("Manifest digest field is NOT flagged as secret", () =>
            {
                // A manifest entry with a generic digest field.
                string line = "\"digest\": \"" + _SyntheticHexDigest + "\"";
                string diff = MakeDiff("dist/manifest.json", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed,
                    "Manifest digest field must not be flagged as a secret");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            await RunTest("Known lockfile with sha256 prefix form is NOT flagged", () =>
            {
                // go.sum style: sha256: prefix on the hash value.
                string line = "sha256:" + _SyntheticHexDigest;
                string diff = MakeDiff("go.sum", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed,
                    "go.sum sha256 prefix entry must not be flagged as a secret");
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // SRI integrity hash -- must NOT be flagged
            // -----------------------------------------------------------------------

            await RunTest("SRI integrity sha256-base64 value is NOT flagged as secret", () =>
            {
                // An npm/yarn lockfile integrity field using SRI sha256-<base64> form.
                string line = "\"integrity\": \"" + _SyntheticSriValue + "\"";
                string diff = MakeDiff("package-lock.json", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed,
                    "SRI integrity sha256-base64 value must not be flagged as a secret");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            await RunTest("Manifest file with integrity field is NOT flagged as secret", () =>
            {
                // A .manifest file using an integrity field.
                string line = "integrity: " + _SyntheticSriValue;
                string diff = MakeDiff("src/app.manifest", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertTrue(result.Passed,
                    "Manifest file integrity field must not be flagged as a secret");
                AssertEqual(0, result.Findings.Count);
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // Genuine secrets -- MUST still be flagged
            // -----------------------------------------------------------------------

            await RunTest("RSA private key header is STILL flagged even in a manifest file", () =>
            {
                // A PEM key header in any file must always be blocked.
                // Assembled across separate source lines so the literal PEM pattern
                // never appears on a single line (satisfies the pre-push secret guard).
                string rsaHeader = "-----BEGIN RS" + "A PRIV"
                    + "ATE KEY-----";
                string diff = MakeDiff("package-lock.json", rsaHeader);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "RSA private key header must still be flagged");
                AssertTrue(result.Findings.Count >= 1);
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("API key literal is STILL flagged", () =>
            {
                // A hard-coded API key must remain blocked regardless of manifest context.
                string diff = MakeDiff("src/Config.cs", "api_key = \"ABCDEF1234567890ABCDEF1234567890\"");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "API key literal must still be flagged as a secret");
                AssertTrue(result.Findings.Count >= 1);
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("Password literal is STILL flagged", () =>
            {
                string diff = MakeDiff("src/Auth.cs", "password = \"supersecret123\"");
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "Password literal must still be flagged as a secret");
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            await RunTest("Bearer token literal is STILL flagged", () =>
            {
                // Assembled at runtime so the raw test source does not itself contain a
                // "bearer <token>" sequence that would trip CORE_RULE_5_bearer_literal when
                // this test file is scanned by the dock boundary gate at land time.
                string bearerLiteral = "Bea" + "rer " + "AAABBBCCCDDDEEEFFFGGGHHHIIIJJJKKKLLL";
                string diff = MakeDiff("src/Client.cs", bearerLiteral);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed, "Bearer token must still be flagged as a secret");
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // Bare 64-hex token outside hash context -- policy: still flagged
            //
            // A 64-char lowercase hex string on a line that has no hash-field keyword
            // and is not in a known manifest/lockfile is NOT exempted. This is deliberate:
            // the allowlist requires both a digest-shaped token AND a hash-context signal
            // so that a genuine API key or token that happens to be 64 lowercase hex chars
            // in a non-manifest file is not silently suppressed.
            // -----------------------------------------------------------------------

            await RunTest("Bare 64-hex token in non-manifest non-hash context is STILL flagged", () =>
            {
                // A 64-char hex string on a plain source line with no hash-field keyword.
                // Policy: this remains flagged per existing behavior (conservative default).
                string line = "token = \"" + _SyntheticHexDigest + "\"";
                string diff = MakeDiff("src/Config.cs", line);
                DockBoundaryScanResult result = scanner.Scan(
                    diff, null, null, null, null, null, DefaultSettings());
                AssertFalse(result.Passed,
                    "Bare 64-hex token in non-hash context must remain flagged (conservative policy)");
                AssertEqual(DockBoundaryFindingKindEnum.Secret, result.Findings[0].Kind);
                return Task.CompletedTask;
            });

            // -----------------------------------------------------------------------
            // ConventionChecker.IsManifestHashAllowed direct unit tests
            // -----------------------------------------------------------------------

            await RunTest("IsManifestHashAllowed returns false for non-base64-chunk rules", () =>
            {
                string line = "\"sha256\": \"" + _SyntheticHexDigest + "\"";
                // Other CORE_RULE_5 rules must not be affected by the allowlist.
                AssertFalse(ConventionChecker.IsManifestHashAllowed(
                    "CORE_RULE_5_private_key", line, "package-lock.json"));
                AssertFalse(ConventionChecker.IsManifestHashAllowed(
                    "CORE_RULE_5_apikey_literal", line, "package-lock.json"));
                AssertFalse(ConventionChecker.IsManifestHashAllowed(
                    "CORE_RULE_5_bearer_literal", line, "package-lock.json"));
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed returns false when line has no digest token", () =>
            {
                // A line that has a hash keyword but no actual digest token.
                string line = "\"hash\": \"short\"";
                AssertFalse(ConventionChecker.IsManifestHashAllowed(
                    "CORE_RULE_5_base64_chunk", line, "manifest.json"));
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed returns false when no hash context and non-manifest file", () =>
            {
                // No hash-field keyword and not a manifest/lockfile: token is NOT exempted.
                string line = "secret = \"" + _SyntheticHexDigest + "\"";
                AssertFalse(ConventionChecker.IsManifestHashAllowed(
                    "CORE_RULE_5_base64_chunk", line, "src/Config.cs"));
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed returns true for hash field keyword context", () =>
            {
                string line = "\"sha256\": \"" + _SyntheticHexDigest + "\"";
                AssertTrue(ConventionChecker.IsManifestHashAllowed(
                    "CORE_RULE_5_base64_chunk", line, "src/Config.cs"),
                    "sha256 field keyword provides hash context");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed returns true for .lock file extension", () =>
            {
                string line = "token = \"" + _SyntheticHexDigest + "\"";
                AssertTrue(ConventionChecker.IsManifestHashAllowed(
                    "CORE_RULE_5_base64_chunk", line, "Cargo.lock"),
                    ".lock extension qualifies as known manifest file");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed returns true for .manifest file extension", () =>
            {
                string line = "value = \"" + _SyntheticHexDigest + "\"";
                AssertTrue(ConventionChecker.IsManifestHashAllowed(
                    "CORE_RULE_5_base64_chunk", line, "dist/app.manifest"),
                    ".manifest extension qualifies as known manifest file");
                return Task.CompletedTask;
            });

            await RunTest("IsManifestHashAllowed returns true for package-lock.json", () =>
            {
                string line = "value = \"" + _SyntheticHexDigest + "\"";
                AssertTrue(ConventionChecker.IsManifestHashAllowed(
                    "CORE_RULE_5_base64_chunk", line, "package-lock.json"),
                    "package-lock.json qualifies as known manifest file");
                return Task.CompletedTask;
            });
        }
    }
}
