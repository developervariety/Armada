namespace Armada.Server
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;

    public sealed partial class VesselCheckoutImportService
    {
        private sealed class CheckoutMetadata
        {
            public string OriginUrl { get; set; } = String.Empty;
            public string DefaultBranch { get; set; } = "main";
        }

        private async Task<CheckoutMetadata?> ReadCheckoutMetadataAsync(string checkoutPath, CancellationToken token)
        {
            string? insideWorkTree = await RunGitOutputAsync(checkoutPath, token, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
            if (!String.Equals(insideWorkTree?.Trim(), "true", StringComparison.Ordinal)) return null;

            string? topLevel = await RunGitOutputAsync(checkoutPath, token, "rev-parse", "--show-toplevel").ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(topLevel) || !PathEquals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(topLevel.Trim())), checkoutPath))
                return null;

            string? origin = await RunGitOutputAsync(checkoutPath, token, "remote", "get-url", "origin").ConfigureAwait(false);
            string? defaultBranch = await ReadDefaultBranchAsync(checkoutPath, token).ConfigureAwait(false);
            return new CheckoutMetadata
            {
                OriginUrl = SanitizeOriginUrl(origin),
                DefaultBranch = String.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch
            };
        }

        private async Task<string?> ReadDefaultBranchAsync(string checkoutPath, CancellationToken token)
        {
            const string OriginHeadPrefix = "refs/remotes/origin/";
            string? originHead = await RunGitOutputAsync(
                checkoutPath, token, "symbolic-ref", "--quiet", "refs/remotes/origin/HEAD").ConfigureAwait(false);
            if (!String.IsNullOrWhiteSpace(originHead))
            {
                string target = originHead.Trim();
                if (target.StartsWith(OriginHeadPrefix, StringComparison.Ordinal))
                {
                    string branch = target.Substring(OriginHeadPrefix.Length);
                    if (!String.IsNullOrWhiteSpace(branch)) return branch;
                }
            }

            string? currentBranch = await RunGitOutputAsync(
                checkoutPath, token, "symbolic-ref", "--quiet", "--short", "HEAD").ConfigureAwait(false);
            return String.IsNullOrWhiteSpace(currentBranch) ? "main" : currentBranch.Trim();
        }

        private static string SanitizeOriginUrl(string? origin)
        {
            if (String.IsNullOrWhiteSpace(origin)) return String.Empty;
            string value = origin.Trim();
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.IsFile) return String.Empty;
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                UriBuilder builder = new UriBuilder(uri)
                {
                    UserName = String.Empty,
                    Password = String.Empty,
                    Query = String.Empty,
                    Fragment = String.Empty
                };
                return builder.Uri.ToString();
            }
            if (value.IndexOf('@') >= 0 || value.IndexOf('?') >= 0 || value.IndexOf('#') >= 0)
                return String.Empty;
            return value;
        }

        private static async Task<string?> RunGitOutputAsync(string workingDirectory, CancellationToken token, params string[] arguments)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ProcessStartInfo startInfo = GitProcessStartInfo.Create(workingDirectory, arguments);
                BoundedProcessRequest request = new BoundedProcessRequest(startInfo, TimeSpan.FromSeconds(5))
                {
                    OutputLimitBytes = 32768
                };
                BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!result.Succeeded || result.Truncated || result.OutputDrainTimedOut) return null;
                return result.StandardOutput;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return null;
            }
        }

        private static bool PathEquals(string left, string right)
        {
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return String.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), comparison);
        }
    }
}
