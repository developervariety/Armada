namespace Armada.Runtimes.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using Armada.Core.Services;

    /// <summary>
    /// Resolves tool paths inside one mission workspace and rejects traversal and reparse-point escapes.
    /// </summary>
    public static class WorkspacePathPolicy
    {
        /// <summary>
        /// Resolve a requested path below the supplied workspace.
        /// </summary>
        /// <param name="requestedPath">Absolute or workspace-relative path.</param>
        /// <param name="workingDirectory">Mission workspace directory.</param>
        /// <returns>The normalized path.</returns>
        public static string ResolvePath(string requestedPath, string workingDirectory)
        {
            if (String.IsNullOrWhiteSpace(requestedPath))
                throw new ArgumentException("A workspace path is required.", nameof(requestedPath));
            if (String.IsNullOrWhiteSpace(workingDirectory))
                throw new ArgumentException("A mission workspace is required.", nameof(workingDirectory));

            string root = Path.GetFullPath(workingDirectory);
            string candidate = Path.GetFullPath(Path.IsPathRooted(requestedPath)
                ? requestedPath
                : Path.Combine(root, requestedPath));
            if (!PathContainment.IsWithin(root, candidate))
                throw new WorkspaceBoundaryException();

            EnsureNoReparsePoint(root, candidate);
            return candidate;
        }

        private static void EnsureNoReparsePoint(string root, string candidate)
        {
            StringComparison comparison = StringComparison.Ordinal;
            string current = candidate;
            while (true)
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new WorkspaceBoundaryException();
                }

                if (String.Equals(current, root, comparison)) break;
                string? parent = Path.GetDirectoryName(current);
                if (String.IsNullOrEmpty(parent) || String.Equals(parent, current, comparison))
                    throw new WorkspaceBoundaryException();
                current = parent;
            }
        }

        /// <summary>
        /// Enumerates every file-system entry below a workspace without following reparse points.
        /// An inaccessible or changing entry fails the operation instead of being silently omitted.
        /// </summary>
        /// <param name="workingDirectory">Mission workspace directory.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="maximumEntries">Maximum entries to enumerate.</param>
        /// <returns>Validated file-system entries.</returns>
        public static IEnumerable<string> EnumerateEntries(string workingDirectory, CancellationToken cancellationToken, int maximumEntries)
        {
            string root = Path.GetFullPath(workingDirectory);
            EnsureNoReparsePoint(root, root);
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            int count = 0;

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string validated = ResolvePath(entry, root);
                    FileAttributes attributes = File.GetAttributes(validated);
                    count++;
                    if (count > maximumEntries)
                        throw new WorkspaceEnumerationLimitException(maximumEntries);

                    yield return validated;
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(validated);
                }
            }
        }

    }

}
