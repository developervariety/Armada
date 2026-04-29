namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Maps git remote URLs to <see cref="PullRequestPlatform"/> for CLI selection.
    /// </summary>
    public static class OriginUrlParser
    {
        /// <summary>
        /// Resolves the pull-request platform for a git remote URL.
        /// Supports github.com and gitlab.com over HTTPS and SSH, optional www prefix, and .git suffix.
        /// </summary>
        /// <param name="remoteUrl">Remote URL (e.g. https://github.com/org/repo.git).</param>
        /// <returns>Detected platform.</returns>
        /// <exception cref="ArgumentException"><paramref name="remoteUrl"/> is null or whitespace.</exception>
        /// <exception cref="NotSupportedException">Host is not github.com or gitlab.com.</exception>
        public static PullRequestPlatform GetPlatform(string remoteUrl)
        {
            if (String.IsNullOrWhiteSpace(remoteUrl))
                throw new ArgumentException("Remote URL is required.", nameof(remoteUrl));

            string host = ExtractHost(remoteUrl.Trim());
            if (String.IsNullOrEmpty(host))
                throw new NotSupportedException("Unable to determine git remote host for: " + remoteUrl);

            string normalized = NormalizeHost(host);

            if (String.Equals(normalized, "github.com", StringComparison.OrdinalIgnoreCase))
                return PullRequestPlatform.GitHub;

            if (String.Equals(normalized, "gitlab.com", StringComparison.OrdinalIgnoreCase))
                return PullRequestPlatform.GitLab;

            throw new NotSupportedException("Unsupported git remote host: " + host);
        }

        private static string NormalizeHost(string host)
        {
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                return host.Substring("www.".Length);

            return host;
        }

        private static string ExtractHost(string remoteUrl)
        {
            if (remoteUrl.StartsWith("git@", StringComparison.Ordinal))
            {
                int at = remoteUrl.IndexOf('@');
                int colon = remoteUrl.IndexOf(':', at + 1);
                if (at < 0 || colon < 0 || colon <= at + 1)
                    return String.Empty;

                return remoteUrl.Substring(at + 1, colon - at - 1);
            }

            if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out Uri? uri) || String.IsNullOrEmpty(uri.Host))
                return String.Empty;

            return uri.Host;
        }
    }
}
