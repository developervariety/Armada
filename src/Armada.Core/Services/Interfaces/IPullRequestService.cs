namespace Armada.Core.Services.Interfaces
{
    /// <summary>
    /// Platform-neutral pull request operations backed by host CLIs (gh, glab).
    /// </summary>
    public interface IPullRequestService
    {
        /// <summary>
        /// Creates a pull or merge request from <paramref name="branch"/> into <paramref name="baseBranch"/>.
        /// </summary>
        /// <param name="branch">Head or source branch name.</param>
        /// <param name="baseBranch">Base or target branch name.</param>
        /// <param name="title">Request title.</param>
        /// <param name="body">Request body (may be empty).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Canonical web URL of the created request.</returns>
        Task<string> CreateAsync(string branch, string baseBranch, string title, string body, CancellationToken token = default);

        /// <summary>
        /// Returns whether the request at <paramref name="prUrl"/> has been merged.
        /// </summary>
        /// <param name="prUrl">Web URL of the pull or merge request.</param>
        /// <param name="token">Cancellation token.</param>
        Task<bool> IsMergedAsync(string prUrl, CancellationToken token = default);
    }
}
