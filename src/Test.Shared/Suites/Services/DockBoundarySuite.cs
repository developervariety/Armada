namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="DockBoundaryScanner"/>. Positive cases confirm secrets, protected paths,
    /// and private identifiers are flagged (without echoing secret bytes); negative cases confirm clean
    /// diffs, removed lines, disabled scanning, and a null policy produce nothing.
    /// </summary>
    public sealed class DockBoundarySuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the dock-boundary scanner suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("secret_in_added_line_flagged_without_bytes", "Secret in an added line is flagged, no secret bytes", TestTags.Positive, () =>
            {
                string diff = "+++ b/config.py\n+AWS_KEY = \"AKIAIOSFODNN7EXAMPLE\"\n physical = 1";
                DockBoundaryPolicy policy = new DockBoundaryPolicy { SecretScanEnabled = true };
                List<BoundaryFinding> findings = DockBoundaryScanner.Scan(diff, null, policy);
                AssertTrue(findings.Count >= 1, "expected a secret finding");
                BoundaryFinding f = findings[0];
                AssertEqual("secret", f.Kind);
                AssertEqual("config.py", f.Path);
                // The finding must NOT carry the secret value anywhere.
                AssertFalse(f.RuleId.Contains("AKIA"), "rule id leaked secret");
                AssertFalse(DockBoundaryScanner.Summarize(findings).Contains("AKIAIOSFODNN7EXAMPLE"), "summary leaked secret");
            }));

            cases.Add(Case("private_key_block_flagged", "PEM private-key block is flagged", TestTags.Positive, () =>
            {
                string diff = "+++ b/id_rsa\n+-----BEGIN RSA PRIVATE KEY-----";
                List<BoundaryFinding> findings = DockBoundaryScanner.Scan(diff, null, new DockBoundaryPolicy { SecretScanEnabled = true });
                AssertTrue(findings.Count >= 1);
                AssertEqual("secret", findings[0].Kind);
            }));

            cases.Add(Case("protected_path_flagged", "A changed protected path is flagged", TestTags.Positive, () =>
            {
                DockBoundaryPolicy policy = new DockBoundaryPolicy { ProtectedPathGlobs = new List<string> { ".github/**", "LICENSE" } };
                List<BoundaryFinding> findings = DockBoundaryScanner.Scan(null, new List<string> { ".github/workflows/ci.yml", "src/app.py" }, policy);
                AssertEqual(1, findings.Count);
                AssertEqual("protected-path", findings[0].Kind);
                AssertEqual(".github/workflows/ci.yml", findings[0].Path);
            }));

            cases.Add(Case("private_identifier_flagged", "A private identifier in an added line is flagged", TestTags.Positive, () =>
            {
                string diff = "+++ b/readme.md\n+Deployed for AcmeCorp internal use";
                DockBoundaryPolicy policy = new DockBoundaryPolicy { PrivateIdentifiers = new List<string> { "AcmeCorp" } };
                List<BoundaryFinding> findings = DockBoundaryScanner.Scan(diff, null, policy);
                AssertTrue(findings.Count >= 1);
                AssertEqual("private-identifier", findings[0].Kind);
            }));

            cases.Add(Case("clean_diff_no_findings", "A clean diff produces no findings", TestTags.Negative, () =>
            {
                string diff = "+++ b/app.py\n+x = compute(y)\n+return x";
                List<BoundaryFinding> findings = DockBoundaryScanner.Scan(diff, new List<string> { "app.py" }, new DockBoundaryPolicy { SecretScanEnabled = true, ProtectedPathGlobs = new List<string> { ".github/**" } });
                AssertEqual(0, findings.Count);
            }));

            cases.Add(Case("secret_in_removed_line_not_flagged", "A secret on a removed line is not flagged", TestTags.Negative, () =>
            {
                string diff = "+++ b/config.py\n-AWS_KEY = \"AKIAIOSFODNN7EXAMPLE\"";
                List<BoundaryFinding> findings = DockBoundaryScanner.Scan(diff, null, new DockBoundaryPolicy { SecretScanEnabled = true });
                AssertEqual(0, findings.Count);
            }));

            cases.Add(Case("disabled_scanning_no_findings", "Secret scanning disabled -> no findings", TestTags.Negative, () =>
            {
                string diff = "+++ b/config.py\n+token = \"ghp_abcdefghijklmnopqrstuvwxyz0123456789\"";
                List<BoundaryFinding> findings = DockBoundaryScanner.Scan(diff, null, new DockBoundaryPolicy { SecretScanEnabled = false });
                AssertEqual(0, findings.Count);
            }));

            cases.Add(Case("null_policy_no_findings", "A null policy produces no findings", TestTags.Negative, () =>
            {
                string diff = "+++ b/config.py\n+AWS_KEY = \"AKIAIOSFODNN7EXAMPLE\"";
                List<BoundaryFinding> findings = DockBoundaryScanner.Scan(diff, new List<string> { "config.py" }, null);
                AssertEqual(0, findings.Count);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.DockBoundary",
                displayName: "Dock Boundary Scanner",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.DockBoundary",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
