namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// Temporary directory for self-deploy tests. Artifact directories are read-only by design, so disposal
    /// restores owner write permission before deleting.
    /// </summary>
    public sealed class SelfDeployTestDirectory : IDisposable
    {
        /// <summary>
        /// Create a unique temporary directory.
        /// </summary>
        public SelfDeployTestDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "armada-selfdeploy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        /// <summary>
        /// Root path.
        /// </summary>
        public string Root { get; }

        /// <summary>
        /// Make a read-only tree writable again.
        /// </summary>
        /// <param name="path">Directory to unlock.</param>
        public static void MakeWritable(string path)
        {
            if (OperatingSystem.IsWindows() || !Directory.Exists(path)) return;
            Stack<string> pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(child);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                MakeWritable(Root);
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Test assertions have already run; a leftover unique temporary directory changes no result.
                Console.Error.WriteLine("[SelfDeployTestDirectory] cleanup failed for " + Root + ": " + ex.Message);
            }
        }
    }
}
