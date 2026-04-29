namespace Armada.Core.Services.Interfaces
{
    /// <summary>
    /// Remote hosting platform for pull or merge request CLI operations.
    /// </summary>
    public enum PullRequestPlatform
    {
        /// <summary>github.com (GitHub CLI).</summary>
        GitHub,

        /// <summary>gitlab.com (GitLab CLI).</summary>
        GitLab
    }
}
