namespace Armada.Core.Services
{
    using System;
    using System.IO;

    /// <summary>
    /// Owner-only private storage through Unix permission bits: directories 0700, files 0600.
    /// </summary>
    internal sealed class UnixSelfDeployPrivateStorage : ISelfDeployPrivateStorageBackend
    {
        private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        private const UnixFileMode PublicBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        /// <inheritdoc />
        public string Name => "unix-mode";

        /// <inheritdoc />
        public void CreatePrivateDirectory(string path)
        {
            if (OperatingSystem.IsWindows()) throw new SelfDeployPrivateStorageException("private_storage_backend_platform_mismatch");
            Directory.CreateDirectory(path, PrivateDirectoryMode);
        }

        /// <inheritdoc />
        public void VerifyPrivateDirectory(string path)
        {
            if (OperatingSystem.IsWindows()) throw new SelfDeployPrivateStorageException("private_storage_backend_platform_mismatch");
            if ((File.GetUnixFileMode(path) & PublicBits) != UnixFileMode.None)
                throw new SelfDeployPrivateStorageException("private_storage_permissions_unverified");
        }

        /// <inheritdoc />
        public FileStream CreatePrivateFile(string path)
        {
            if (OperatingSystem.IsWindows()) throw new SelfDeployPrivateStorageException("private_storage_backend_platform_mismatch");
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan,
                UnixCreateMode = PrivateFileMode
            });
        }

        /// <inheritdoc />
        public void RestrictFile(string path)
        {
            if (OperatingSystem.IsWindows()) throw new SelfDeployPrivateStorageException("private_storage_backend_platform_mismatch");
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }
}
