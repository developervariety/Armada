namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Creates short-lived self-deploy files with owner-only permissions.
    /// </summary>
    internal static class SelfDeployPrivateFile
    {
        private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead
            | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        public static void CreateDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new SelfDeployPrivateStorageException("private_storage_path_missing");
            if (OperatingSystem.IsWindows())
            {
                // Do not infer ACL privacy from a path name. Windows ACL inspection and
                // owner-only creation must be implemented before this provider is enabled on
                // Windows; fail closed for both new and existing locations until then.
                throw new SelfDeployPrivateStorageException("private_storage_acl_unverified");
            }
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                VerifyDirectory(fullPath);
                return;
            }

            // Directory.CreateDirectory applies the requested mode only to the leaf; missing ancestors
            // would get the default public mode. Create each missing level with the private mode so a
            // nested private path never leaves a public parent that a later private check rejects.
            Stack<string> missing = new Stack<string>();
            string? cursor = fullPath;
            while (!String.IsNullOrEmpty(cursor) && !Directory.Exists(cursor))
            {
                missing.Push(cursor);
                cursor = Path.GetDirectoryName(cursor);
            }
            while (missing.Count > 0)
            {
                string level = missing.Pop();
                Directory.CreateDirectory(level, PrivateDirectoryMode);
                VerifyDirectory(level);
            }
        }

        public static async Task WriteTextAsync(string path, string content, CancellationToken token)
        {
            FileStreamOptions options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : PrivateFileMode
            };
            using (FileStream stream = new FileStream(path, options))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
            }
        }

        public static void RestrictFile(string path)
        {
            if (!File.Exists(path)) return;
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new SelfDeployPrivateStorageException("private_storage_symlink_refused");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, PrivateFileMode);
        }

        private static void VerifyDirectory(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new SelfDeployPrivateStorageException("private_storage_symlink_refused");
            if (!OperatingSystem.IsWindows())
            {
                UnixFileMode mode = File.GetUnixFileMode(path);
                UnixFileMode publicBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
                    | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((mode & publicBits) != UnixFileMode.None)
                    throw new SelfDeployPrivateStorageException("private_storage_permissions_unverified");
            }
        }
    }

}
