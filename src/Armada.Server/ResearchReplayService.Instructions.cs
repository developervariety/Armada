namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class ResearchReplayService
    {
        private async Task<List<ResearchReplayInstruction>> CaptureInstructionsAsync(List<Mission> stages, CancellationToken token)
        {
            List<ResearchReplayInstruction> captured = new List<ResearchReplayInstruction>();
            string root = Path.GetFullPath(Path.Combine(_Settings.LogDirectory, "instructions"));
            if (!Directory.Exists(root)) return captured;
            DirectoryInfo directory = new DirectoryInfo(root);
            if (directory.LinkTarget != null || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Replay instruction capture refuses linked directories.");
            long totalBytes = 0;
            foreach (Mission mission in stages.OrderBy(m => m.Id, StringComparer.Ordinal))
            {
                if (Path.GetFileName(mission.Id) != mission.Id || mission.Id.Contains('*') || mission.Id.Contains('?'))
                    throw new InvalidOperationException("Invalid mission identity for retained instruction capture.");
                foreach (string path in Directory.EnumerateFiles(root, mission.Id + ".*.md").OrderBy(p => p, StringComparer.Ordinal))
                {
                    FileInfo file = new FileInfo(path);
                    if (file.Name.StartsWith("._", StringComparison.Ordinal) || file.LinkTarget != null
                        || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Replay instruction capture refuses linked files.");
                    if (file.Length > 2 * 1024 * 1024 || totalBytes + file.Length > 8 * 1024 * 1024)
                        throw new InvalidOperationException("Retained launch instructions exceed the replay capture bound.");
                    byte[] bytes = await ReadInstructionBytesAsync(path, totalBytes, token).ConfigureAwait(false);
                    totalBytes += bytes.Length;
                    if (bytes.Length > 2 * 1024 * 1024 || totalBytes > 8 * 1024 * 1024)
                        throw new InvalidOperationException("Retained launch instructions changed beyond the capture bound.");
                    captured.Add(new ResearchReplayInstruction { MissionId = mission.Id, FileName = file.Name,
                        Content = new UTF8Encoding(false, true).GetString(bytes), Utf8Bytes = bytes.Length,
                        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
                }
            }
            return captured;
        }

        private static async Task<byte[]> ReadInstructionBytesAsync(string path, long totalBytes, CancellationToken token)
        {
            using (FileStream opened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (MemoryStream bounded = new MemoryStream())
            {
                ValidateOpenedInstructionPath(opened, path);
                byte[] buffer = new byte[81920];
                int count;
                while ((count = await opened.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    if (bounded.Length + count > 2 * 1024 * 1024 || totalBytes + bounded.Length + count > 8 * 1024 * 1024)
                        throw new InvalidOperationException("Retained launch instructions grew beyond the capture bound.");
                    bounded.Write(buffer, 0, count);
                }
                return bounded.ToArray();
            }
        }

        private static void ValidateOpenedInstructionPath(FileStream opened, string expectedPath)
        {
            string? actual = null;
            if (OperatingSystem.IsLinux())
                actual = File.ResolveLinkTarget("/proc/self/fd/" + opened.SafeFileHandle.DangerousGetHandle().ToInt64(), true)?.FullName;
            else if (OperatingSystem.IsMacOS())
            {
                StringBuilder buffer = new StringBuilder(4096);
                if (GetMacFilePath(opened.SafeFileHandle.DangerousGetHandle().ToInt32(), 50, buffer) == 0) actual = buffer.ToString();
            }
            else if (OperatingSystem.IsWindows())
            {
                StringBuilder buffer = new StringBuilder(32768);
                uint count = GetWindowsFilePath(opened.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
                if (count > 0 && count < buffer.Capacity)
                {
                    actual = buffer.ToString();
                    if (actual.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) actual = @"\\" + actual.Substring(8);
                    else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual.Substring(4);
                }
            }
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (actual == null || !String.Equals(Path.GetFullPath(actual), Path.GetFullPath(expectedPath), comparison))
                throw new InvalidOperationException("The opened instruction file does not match its authorized retained path.");
        }

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int GetMacFilePath(int descriptor, int operation, StringBuilder path);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetWindowsFilePath(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            StringBuilder path, uint capacity, uint flags);
    }
}
