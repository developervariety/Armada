namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="GitInference"/>: vessel-name extraction from HTTPS/SSH remotes,
    /// repository detection, and default-branch resolution. Negative cases cover empty/degenerate
    /// URLs and non-git directories, where the service returns fallbacks or null.
    /// </summary>
    public sealed class GitInferenceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Git Inference suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // InferVesselName

            cases.Add(Case("infer_vessel_name_https_url_extracts_repo_name", "InferVesselName HttpsUrl ExtractsRepoName", TestTags.Positive, () =>
            {
                string name = GitInference.InferVesselName("https://github.com/user/myapp.git");
                AssertEqual("myapp", name);
            }));

            cases.Add(Case("infer_vessel_name_https_url_no_git_extracts_repo_name", "InferVesselName HttpsUrlNoGit ExtractsRepoName", TestTags.Positive, () =>
            {
                string name = GitInference.InferVesselName("https://github.com/user/myapp");
                AssertEqual("myapp", name);
            }));

            cases.Add(Case("infer_vessel_name_ssh_url_extracts_repo_name", "InferVesselName SshUrl ExtractsRepoName", TestTags.Positive, () =>
            {
                string name = GitInference.InferVesselName("git@github.com:user/myapp.git");
                AssertEqual("myapp", name);
            }));

            cases.Add(Case("infer_vessel_name_ssh_url_no_git_extracts_repo_name", "InferVesselName SshUrlNoGit ExtractsRepoName", TestTags.Positive, () =>
            {
                string name = GitInference.InferVesselName("git@github.com:user/myapp");
                AssertEqual("myapp", name);
            }));

            cases.Add(Case("infer_vessel_name_trailing_slash_extracts_repo_name", "InferVesselName TrailingSlash ExtractsRepoName", TestTags.Positive, () =>
            {
                string name = GitInference.InferVesselName("https://github.com/user/myapp/");
                AssertEqual("myapp", name);
            }));

            cases.Add(Case("infer_vessel_name_nested_path_extracts_last_segment", "InferVesselName NestedPath ExtractsLastSegment", TestTags.Positive, () =>
            {
                string name = GitInference.InferVesselName("https://gitlab.com/org/group/myapp.git");
                AssertEqual("myapp", name);
            }));

            cases.Add(Case("infer_vessel_name_empty_string_returns_unnamed", "InferVesselName EmptyString ReturnsUnnamed", TestTags.Negative, () =>
            {
                string name = GitInference.InferVesselName("");
                AssertEqual("unnamed", name);
            }));

            cases.Add(Case("infer_vessel_name_slashes_only_returns_unnamed", "InferVesselName SlashesOnly ReturnsUnnamed", TestTags.Negative, () =>
            {
                // A URL that reduces to nothing after trimming slashes falls back to the unnamed sentinel.
                string name = GitInference.InferVesselName("///");
                AssertEqual("unnamed", name);
            }));

            // IsGitRepository

            cases.Add(Case("is_git_repository_current_directory_does_not_throw", "IsGitRepository CurrentDirectory DoesNotThrow", TestTags.Positive, () =>
            {
                bool result = GitInference.IsGitRepository(Directory.GetCurrentDirectory());
                AssertTrue(result || !result);
            }));

            cases.Add(Case("is_git_repository_temp_directory_returns_false", "IsGitRepository TempDirectory ReturnsFalse", TestTags.Negative, () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    bool result = GitInference.IsGitRepository(tempDir);
                    AssertFalse(result);
                }
                finally
                {
                    Directory.Delete(tempDir);
                }
            }));

            // GetRemoteUrl

            cases.Add(Case("get_remote_url_non_git_dir_returns_null", "GetRemoteUrl NonGitDir ReturnsNull", TestTags.Negative, () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string? url = GitInference.GetRemoteUrl(tempDir);
                    AssertNull(url);
                }
                finally
                {
                    Directory.Delete(tempDir);
                }
            }));

            // GetRepoRoot

            cases.Add(Case("get_repo_root_non_git_dir_returns_null", "GetRepoRoot NonGitDir ReturnsNull", TestTags.Negative, () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string? root = GitInference.GetRepoRoot(tempDir);
                    AssertNull(root);
                }
                finally
                {
                    Directory.Delete(tempDir);
                }
            }));

            // GetDefaultBranch

            cases.Add(Case("get_default_branch_non_git_dir_returns_main", "GetDefaultBranch NonGitDir ReturnsMain", TestTags.Positive, () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string branch = GitInference.GetDefaultBranch(tempDir);
                    AssertEqual("main", branch);
                }
                finally
                {
                    Directory.Delete(tempDir);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.GitInference",
                displayName: "Git Inference",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.GitInference",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.GitInference",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
