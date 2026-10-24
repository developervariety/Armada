namespace Armada.Runtimes.Harbor
{
    using System;
    using Armada.Core.Services;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Runtimes.Tools;

    /// <summary>Validates checkout-relative paths and expected Git heads.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task CheckExpectedHeadAsync(HarborWorkspaceOperationRequest request, string checkout, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(request.ExpectedHead) || !IsFullCommit(request.ExpectedHead))
                throw Failure("workspace_expected_head_required", "The operation requires a full expected checkout commit.");
            string current = await ReadHeadAsync(checkout, token).ConfigureAwait(false);
            if (!String.Equals(current, request.ExpectedHead, StringComparison.OrdinalIgnoreCase))
                throw Failure("workspace_checkout_head_conflict", "The checkout HEAD changed since the operation was prepared.");
        }

        private static string ResolveRelativePath(string? relativePath, string checkout, bool requireExists)
        {
            if (String.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 4096 || Path.IsPathRooted(relativePath))
                throw Failure("workspace_path_invalid", "A bounded relative workspace path is required.");
            string resolved = WorkspacePathPolicy.ResolvePath(relativePath, checkout);
            if (!PathContainment.IsWithin(checkout, resolved)) throw Failure("workspace_path_refused", "The path leaves the checkout.");
            string relative = Path.GetRelativePath(checkout, resolved);
            string[] segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
                if (String.Equals(segment, ".git", PathComparison)) throw Failure("workspace_git_metadata_refused", "Git administrative files are not workspace files.");
            EnsureNoLinks(resolved, requireExists);
            return resolved;
        }

        private static byte[] ReadBoundedFile(string path, int maximumBytes)
        {
            if (!File.Exists(path)) throw Failure("workspace_file_not_found", "The selected file does not exist.");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] content = new byte[maximumBytes + 1];
                int total = 0;
                while (total < content.Length)
                {
                    int read = stream.Read(content, total, content.Length - total);
                    if (read == 0) break;
                    total += read;
                }
                if (total > maximumBytes) throw Failure("workspace_file_too_large", "The selected file exceeds the requested byte limit.");
                byte[] result = new byte[total];
                Buffer.BlockCopy(content, 0, result, 0, total);
                return result;
            }
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}
