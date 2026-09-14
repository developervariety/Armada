namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Creates short-lived self-deploy and backup files with owner-only access through the platform backend:
    /// Unix permission bits, or verified owner-only ACLs on Windows.
    /// </summary>
    internal static class SelfDeployPrivateFile
    {
        private static ISelfDeployPrivateStorageBackend _Backend = SelectBackend(OperatingSystem.IsWindows());

        /// <summary>
        /// Backend in use. Replaceable so the selection and fail-closed contract can be exercised on any host.
        /// </summary>
        internal static ISelfDeployPrivateStorageBackend Backend
        {
            get => _Backend;
            set => _Backend = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Backend for a platform.
        /// </summary>
        /// <param name="windows">True for Windows.</param>
        /// <returns>Windows ACL backend or Unix permission backend.</returns>
        internal static ISelfDeployPrivateStorageBackend SelectBackend(bool windows)
        {
            return windows ? new WindowsSelfDeployPrivateStorage() : new UnixSelfDeployPrivateStorage();
        }

        public static void CreateDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new SelfDeployPrivateStorageException("private_storage_path_missing");
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                RefuseReparsePoint(fullPath);
                _Backend.VerifyPrivateDirectory(fullPath);
                return;
            }

            // Create each missing level with owner-only access so a nested private path never leaves a public
            // parent that a later private check rejects. Existing ancestors are left unchanged.
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
                _Backend.CreatePrivateDirectory(level);
                RefuseReparsePoint(level);
                _Backend.VerifyPrivateDirectory(level);
            }
        }

        public static async Task WriteTextAsync(string path, string content, CancellationToken token)
        {
            using (FileStream stream = _Backend.CreatePrivateFile(path))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
            }
        }

        public static void RestrictFile(string path)
        {
            if (!File.Exists(path)) return;
            RefuseReparsePoint(path);
            _Backend.RestrictFile(path);
        }

        private static void RefuseReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new SelfDeployPrivateStorageException("private_storage_symlink_refused");
        }
    }
}
