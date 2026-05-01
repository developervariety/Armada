namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests for <see cref="GitService.IsPrMergedAsync"/> platform routing (gh vs glab).
    /// </summary>
    public sealed class GitServiceIsPrMergedTests : TestSuite
    {
        public override string Name => "GitServiceIsPrMerged";

        protected override async Task RunTestsAsync()
        {
            await RunTest("IsPrMergedAsync_GitHubUrl_RoutesToGhCli", async () =>
            {
                PullRequestPlatform? capturedPlatform = null;
                CapturingPullRequestService capturing = new CapturingPullRequestService("MERGED");

                Func<PullRequestPlatform, string, IPullRequestService> factory = (platform, wd) =>
                {
                    capturedPlatform = platform;
                    return capturing;
                };

                GitService svc = BuildGitService(factory);
                await svc.IsPrMergedAsync(@"C:\repo", "https://github.com/org/repo/pull/5").ConfigureAwait(false);

                AssertTrue(capturedPlatform.HasValue, "factory was called");
                AssertEqual(PullRequestPlatform.GitHub, capturedPlatform!.Value, "platform routed to GitHub");
            }).ConfigureAwait(false);

            await RunTest("IsPrMergedAsync_GitLabUrl_RoutesToGlabCli", async () =>
            {
                PullRequestPlatform? capturedPlatform = null;
                CapturingPullRequestService capturing = new CapturingPullRequestService("merged");

                Func<PullRequestPlatform, string, IPullRequestService> factory = (platform, wd) =>
                {
                    capturedPlatform = platform;
                    return capturing;
                };

                GitService svc = BuildGitService(factory);
                await svc.IsPrMergedAsync(@"C:\repo", "https://gitlab.com/group/project/-/merge_requests/3").ConfigureAwait(false);

                AssertTrue(capturedPlatform.HasValue, "factory was called");
                AssertEqual(PullRequestPlatform.GitLab, capturedPlatform!.Value, "platform routed to GitLab");
            }).ConfigureAwait(false);

            await RunTest("IsPrMergedAsync_GitHub_Merged_ReturnsTrue", async () =>
            {
                Func<PullRequestPlatform, string, IPullRequestService> factory =
                    (platform, wd) => new CapturingPullRequestService("MERGED");

                GitService svc = BuildGitService(factory);
                bool result = await svc.IsPrMergedAsync(@"C:\repo", "https://github.com/org/repo/pull/1").ConfigureAwait(false);

                AssertTrue(result, "merged GitHub PR returns true");
            }).ConfigureAwait(false);

            await RunTest("IsPrMergedAsync_GitLab_Merged_ReturnsTrue", async () =>
            {
                Func<PullRequestPlatform, string, IPullRequestService> factory =
                    (platform, wd) => new CapturingPullRequestService("merged");

                GitService svc = BuildGitService(factory);
                bool result = await svc.IsPrMergedAsync(@"C:\repo", "https://gitlab.com/a/b/-/merge_requests/7").ConfigureAwait(false);

                AssertTrue(result, "merged GitLab MR returns true");
            }).ConfigureAwait(false);

            await RunTest("IsPrMergedAsync_NonMergedState_ReturnsFalse", async () =>
            {
                Func<PullRequestPlatform, string, IPullRequestService> factory =
                    (platform, wd) => new CapturingPullRequestService("OPEN");

                GitService svc = BuildGitService(factory);
                bool result = await svc.IsPrMergedAsync(@"C:\repo", "https://github.com/org/repo/pull/2").ConfigureAwait(false);

                AssertFalse(result, "open PR returns false");
            }).ConfigureAwait(false);

            await RunTest("IsPrMergedAsync_CliFailure_ReturnsFalse_AndLogsWarning", async () =>
            {
                // When the CLI invocation throws, IsPrMergedAsync must return false and
                // log a Warn (verified by code inspection; LoggingModule does not expose capture API).
                Func<PullRequestPlatform, string, IPullRequestService> factory =
                    (platform, wd) => new ThrowingPullRequestService();

                GitService svc = BuildGitService(factory);
                bool result = await svc.IsPrMergedAsync(@"C:\repo", "https://github.com/org/repo/pull/3").ConfigureAwait(false);

                AssertFalse(result, "CLI failure returns false");
            }).ConfigureAwait(false);

            await RunTest("IsPrMergedAsync_UnknownHost_ReturnsFalse", async () =>
            {
                Func<PullRequestPlatform, string, IPullRequestService> factory =
                    (platform, wd) => new CapturingPullRequestService("merged");

                GitService svc = BuildGitService(factory);
                bool result = await svc.IsPrMergedAsync(@"C:\repo", "https://bitbucket.org/acme/repo/pull-requests/9").ConfigureAwait(false);

                AssertFalse(result, "unsupported host returns false");
            }).ConfigureAwait(false);
        }

        private static GitService BuildGitService(Func<PullRequestPlatform, string, IPullRequestService> factory)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new GitService(logging, factory);
        }

        private sealed class CapturingPullRequestService : IPullRequestService
        {
            private readonly string _State;

            /// <summary>URL passed to IsMergedAsync, or null if not yet called.</summary>
            public string? LastCheckedUrl { get; private set; }

            public CapturingPullRequestService(string state)
            {
                _State = state ?? throw new ArgumentNullException(nameof(state));
            }

            public Task<string> CreateAsync(string branch, string baseBranch, string title, string body, CancellationToken token = default)
                => throw new NotSupportedException();

            public Task<bool> IsMergedAsync(string prUrl, CancellationToken token = default)
            {
                LastCheckedUrl = prUrl;
                bool merged = String.Equals(_State, "merged", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(_State, "MERGED", StringComparison.Ordinal);
                return Task.FromResult(merged);
            }
        }

        private sealed class ThrowingPullRequestService : IPullRequestService
        {
            public Task<string> CreateAsync(string branch, string baseBranch, string title, string body, CancellationToken token = default)
                => throw new NotSupportedException();

            public Task<bool> IsMergedAsync(string prUrl, CancellationToken token = default)
                => throw new InvalidOperationException("gh: exit code 1 - could not find PR");
        }
    }
}
