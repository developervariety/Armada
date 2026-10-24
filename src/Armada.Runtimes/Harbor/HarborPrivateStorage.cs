namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
using System.Runtime.Versioning;

    /// <summary>Applies and validates private permissions for Harbor settings and journal files.</summary>
    public static partial class HarborPrivateStorage
    {
        /// <summary>Create a directory and restrict its data access to the current user and trusted system administrators.</summary>
        /// <param name="path">Directory path.</param>
        public static void EnsurePrivateDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A directory path is required.", nameof(path));
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows()) SecureWindowsDirectory(path);
            else SecureUnixDirectory(path);
        }

        /// <summary>Restrict an existing file to the current user and trusted system administrators.</summary>
        /// <param name="path">File path.</param>
        public static void EnsurePrivateFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("The private file does not exist.", path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A private file cannot be a link.");
            if (OperatingSystem.IsWindows()) SecureWindowsFile(path);
            else SecureUnixFile(path);
        }

        /// <summary>Validate that a file has private permissions without changing its access rules.</summary>
        /// <param name="path">File path.</param>
        public static void ValidatePrivateFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("The private file does not exist.", path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A private file cannot be a link.");
            if (OperatingSystem.IsWindows()) ValidateWindowsFile(path);
            else ValidateUnixFile(path);
        }

        [UnsupportedOSPlatform("windows")]
        private static void SecureUnixDirectory(string path)
        {
            ValidateUnixOwner(path);
            if (OperatingSystem.IsMacOS()) RemoveDarwinAcl(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ValidateUnixDirectory(path);
        }

        [UnsupportedOSPlatform("windows")]
        private static void SecureUnixFile(string path)
        {
            ValidateUnixOwner(path);
            if (OperatingSystem.IsMacOS()) RemoveDarwinAcl(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            ValidateUnixFile(path);
        }

        [UnsupportedOSPlatform("windows")]
        private static void ValidateUnixDirectory(string path)
        {
            ValidateUnixOwner(path);
            if (OperatingSystem.IsMacOS()) ValidateDarwinAcl(path);
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & shared) != 0) throw new UnauthorizedAccessException("A private directory grants access to other users.");
        }

        [UnsupportedOSPlatform("windows")]
        private static void ValidateUnixFile(string path)
        {
            ValidateUnixOwner(path);
            if (OperatingSystem.IsMacOS()) ValidateDarwinAcl(path);
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & shared) != 0) throw new UnauthorizedAccessException("A private file grants access to other users.");
        }
    }
}
