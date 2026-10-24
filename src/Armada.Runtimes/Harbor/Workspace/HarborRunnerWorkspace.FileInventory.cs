namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using Armada.Core.Harbor;

    /// <summary>Enumerates bounded file inventories inside an owned checkout.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private void EnumerateFiles(HarborWorkspaceOperationRequest request, string checkout, HarborWorkspaceOperationResult result, CancellationToken token)
        {
            string root = ResolveRelativePath(request.RelativePath, checkout, true);
            if (!Directory.Exists(root)) throw Failure("workspace_not_directory", "The selected path is not a directory.");
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            List<string> paths = new List<string>();
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    EnsureNoLinks(entry, requireExists: true);
                    string name = Path.GetFileName(entry);
                    if (String.Equals(name, ".git", PathComparison)) continue;
                    if (Directory.Exists(entry))
                    {
                        if (request.Recursive) pending.Push(entry);
                        continue;
                    }
                    paths.Add(Path.GetRelativePath(checkout, entry).Replace(Path.DirectorySeparatorChar, '/'));
                    if (paths.Count > request.MaxEntries) throw Failure("workspace_entry_limit_exceeded", "The file listing exceeds its entry limit.");
                }
            }
            paths.Sort(StringComparer.Ordinal);
            result.Paths = paths;
        }
    }
}
