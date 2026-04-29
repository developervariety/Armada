namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for <see cref="GhPullRequestService"/>, <see cref="GlabPullRequestService"/>, and <see cref="OriginUrlParser"/>.
    /// </summary>
    public sealed class PullRequestServiceTests : TestSuite
    {
        private sealed record InvokedCli(string Path, string[] Args);

        public override string Name => "PullRequestService";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Gh_CreateAsync_InvokesExpectedArgv", async () =>
            {
                List<InvokedCli> calls = new List<InvokedCli>();
                Func<string, string, string[], CancellationToken, Task<string>> runner =
                    (wd, exe, argv, ct) =>
                    {
                        calls.Add(new InvokedCli(wd, argv));
                        return Task.FromResult("https://github.com/o/r/pull/99");
                    };

                GhPullRequestService svc = new GhPullRequestService("gh", @"C:\work\repo", runner);
                string url = await svc.CreateAsync("feat/x", "main", "hello", "body").ConfigureAwait(false);

                AssertEqual("https://github.com/o/r/pull/99", url, "url");
                AssertEqual(1, calls.Count, "call count");
                AssertEqual(@"C:\work\repo", calls[0].Path, "cwd");

                string[] expected = new[]
                {
                    "pr", "create",
                    "--base", "main",
                    "--head", "feat/x",
                    "--title", "hello",
                    "--body", "body"
                };

                AssertArgvEqual(expected, calls[0].Args, "gh create argv");
            }).ConfigureAwait(false);

            await RunTest("Gh_IsMergedAsync_InvokesExpectedArgv", async () =>
            {
                List<InvokedCli> calls = new List<InvokedCli>();
                Func<string, string, string[], CancellationToken, Task<string>> runner =
                    (wd, exe, argv, ct) =>
                    {
                        calls.Add(new InvokedCli(wd, argv));
                        return Task.FromResult("MERGED");
                    };

                GhPullRequestService svc = new GhPullRequestService("gh", @"D:\r", runner);
                bool merged = await svc.IsMergedAsync("https://github.com/o/r/pull/1").ConfigureAwait(false);

                AssertTrue(merged, "merged");
                AssertEqual(1, calls.Count, "call count");

                string[] expected = new[]
                {
                    "pr", "view",
                    "https://github.com/o/r/pull/1",
                    "--json", "state",
                    "--jq", ".state"
                };

                AssertArgvEqual(expected, calls[0].Args, "gh view argv");
            }).ConfigureAwait(false);

            await RunTest("Glab_CreateAsync_InvokesExpectedArgv", async () =>
            {
                List<InvokedCli> calls = new List<InvokedCli>();
                Func<string, string, string[], CancellationToken, Task<string>> runner =
                    (wd, exe, argv, ct) =>
                    {
                        calls.Add(new InvokedCli(wd, argv));
                        return Task.FromResult("https://gitlab.com/g/p/-/merge_requests/3");
                    };

                GlabPullRequestService svc = new GlabPullRequestService("glab", @"E:\gl", runner);
                string url = await svc.CreateAsync("topic", "develop", "mr title", "desc").ConfigureAwait(false);

                AssertEqual("https://gitlab.com/g/p/-/merge_requests/3", url, "url");
                AssertEqual(1, calls.Count, "call count");

                string[] expected = new[]
                {
                    "mr", "create",
                    "--source-branch", "topic",
                    "--target-branch", "develop",
                    "--title", "mr title",
                    "--description", "desc",
                    "--yes"
                };

                AssertArgvEqual(expected, calls[0].Args, "glab create argv");
            }).ConfigureAwait(false);

            await RunTest("Glab_IsMergedAsync_UsesDashFJsonArgv", async () =>
            {
                List<InvokedCli> calls = new List<InvokedCli>();
                Func<string, string, string[], CancellationToken, Task<string>> runner =
                    (wd, exe, argv, ct) =>
                    {
                        calls.Add(new InvokedCli(wd, argv));
                        return Task.FromResult("{\"state\":\"opened\"}");
                    };

                GlabPullRequestService svc = new GlabPullRequestService("glab", @"E:\gl", runner);
                bool merged = await svc.IsMergedAsync("https://gitlab.com/a/b/-/merge_requests/7").ConfigureAwait(false);

                AssertFalse(merged, "opened is not merged");
                AssertEqual(1, calls.Count, "call count");

                string[] expected = new[]
                {
                    "mr", "view",
                    "https://gitlab.com/a/b/-/merge_requests/7",
                    "-F", "json"
                };

                AssertArgvEqual(expected, calls[0].Args, "glab view argv");

                for (int i = 0; i < calls[0].Args.Length; i++)
                {
                    AssertFalse(
                        String.Equals(calls[0].Args[i], "--json", StringComparison.Ordinal),
                        "glab mr view must not use invalid --json flag at index " + i);
                }
            }).ConfigureAwait(false);

            await RunTest("Glab_IsMergedAsync_MergedJson_ReturnsTrue", async () =>
            {
                Func<string, string, string[], CancellationToken, Task<string>> runner =
                    (wd, exe, argv, ct) => Task.FromResult("{\"state\":\"merged\"}");

                GlabPullRequestService svc = new GlabPullRequestService("glab", @"E:\gl", runner);
                bool merged = await svc.IsMergedAsync("https://gitlab.com/a/b/-/merge_requests/7").ConfigureAwait(false);

                AssertTrue(merged, "merged state");
            }).ConfigureAwait(false);

            await RunTest("OriginUrlParser_HttpsGithubDotCom_ReturnsGitHub", () =>
            {
                PullRequestPlatform platform = OriginUrlParser.GetPlatform("https://github.com/org/repo.git");
                AssertEqual(PullRequestPlatform.GitHub, platform, "platform");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("OriginUrlParser_WwwGithub_ReturnsGitHub", () =>
            {
                PullRequestPlatform platform = OriginUrlParser.GetPlatform("https://www.github.com/org/repo");
                AssertEqual(PullRequestPlatform.GitHub, platform, "platform");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("OriginUrlParser_SshGithub_ReturnsGitHub", () =>
            {
                PullRequestPlatform platform = OriginUrlParser.GetPlatform("git@github.com:org/repo.git");
                AssertEqual(PullRequestPlatform.GitHub, platform, "platform");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("OriginUrlParser_HttpsGitlabDotCom_ReturnsGitLab", () =>
            {
                PullRequestPlatform platform = OriginUrlParser.GetPlatform("https://gitlab.com/group/sub/p.git");
                AssertEqual(PullRequestPlatform.GitLab, platform, "platform");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("OriginUrlParser_WwwGitlab_ReturnsGitLab", () =>
            {
                PullRequestPlatform platform = OriginUrlParser.GetPlatform("https://www.gitlab.com/a/b");
                AssertEqual(PullRequestPlatform.GitLab, platform, "platform");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("OriginUrlParser_SshGitlab_ReturnsGitLab", () =>
            {
                PullRequestPlatform platform = OriginUrlParser.GetPlatform("git@gitlab.com:grp/project.git");
                AssertEqual(PullRequestPlatform.GitLab, platform, "platform");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("OriginUrlParser_UnsupportedHost_ThrowsNotSupportedException", () =>
            {
                bool threw = false;
                try
                {
                    OriginUrlParser.GetPlatform("https://bitbucket.org/acme/r.git");
                }
                catch (NotSupportedException)
                {
                    threw = true;
                }

                AssertTrue(threw, "expected NotSupportedException");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        private void AssertArgvEqual(string[] expected, string[] actual, string label)
        {
            AssertEqual(expected.Length, actual.Length, label + " length");
            for (int i = 0; i < expected.Length; i++)
                AssertEqual(expected[i], actual[i], label + " idx " + i);
        }
    }
}
