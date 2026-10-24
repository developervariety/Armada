namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;

    /// <summary>Rejects symbolic-link and reparse-point paths in all runner-owned workspace roots.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private static void EnsureNoLinks(string path, bool requireExists)
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (String.IsNullOrWhiteSpace(root)) throw Failure("workspace_path_invalid", "The workspace path has no filesystem root.");
            string current = root;
            string[] segments = fullPath.Substring(root.Length).Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (entry.LinkTarget != null)
                    throw Failure("workspace_symlink_refused", "A workspace path contains a symbolic link or reparse point.");
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw Failure("workspace_symlink_refused", "A workspace path contains a symbolic link or reparse point.");
            }
            if (requireExists && !File.Exists(fullPath) && !Directory.Exists(fullPath))
                throw Failure("workspace_path_missing", "The selected workspace path does not exist.");
        }
    }
}
