namespace Armada.Test.Unit.TestHelpers
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Hand-rolled <see cref="IPullRequestService"/> double that captures every
    /// pull-request create call. Used by recovery + PR-fallback tests to verify
    /// the right branches and body content are forwarded to the platform CLI
    /// without actually invoking <c>gh</c> or <c>glab</c>.
    /// </summary>
    public sealed class RecordingPullRequestService : IPullRequestService
    {
        /// <summary>
        /// One captured call to <see cref="CreateAsync"/>.
        /// </summary>
        public sealed class OpenedPr
        {
            /// <summary>Source/head branch.</summary>
            public string Branch { get; set; } = "";
            /// <summary>Base/target branch.</summary>
            public string BaseBranch { get; set; } = "";
            /// <summary>PR title.</summary>
            public string Title { get; set; } = "";
            /// <summary>PR body.</summary>
            public string Body { get; set; } = "";
        }

        /// <summary>
        /// Every PR opened on this service. Tests assert on Count and contents.
        /// </summary>
        public List<OpenedPr> OpenedPrs { get; } = new List<OpenedPr>();

        /// <summary>
        /// URL returned for each successful create. Override per-test if needed.
        /// </summary>
        public string CreateAsyncReturnUrl { get; set; } = "https://example.test/repo/pull/1";

        /// <summary>
        /// Result returned by <see cref="IsMergedAsync"/>. Override per-test.
        /// </summary>
        public bool IsMergedReturn { get; set; } = false;

        /// <inheritdoc />
        public Task<string> CreateAsync(string branch, string baseBranch, string title, string body, CancellationToken token = default)
        {
            OpenedPrs.Add(new OpenedPr
            {
                Branch = branch,
                BaseBranch = baseBranch,
                Title = title,
                Body = body
            });
            return Task.FromResult(CreateAsyncReturnUrl);
        }

        /// <inheritdoc />
        public Task<bool> IsMergedAsync(string prUrl, CancellationToken token = default)
        {
            return Task.FromResult(IsMergedReturn);
        }
    }
}
