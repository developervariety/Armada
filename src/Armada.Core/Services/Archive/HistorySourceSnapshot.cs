namespace Armada.Core.Services.Archive
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    // A snapshot of an aged lifecycle-owned file, not a fence for an arbitrary live database.
    // Linux retirement combines a private rename, inode binding and actual writer-reference checks.
    internal sealed class HistorySourceSnapshot
    {
        internal const string QuarantinePrefix = ".armada-archive-free-";
        internal string Path { get; }
        internal string Sha256 { get; }
        internal long Bytes { get; }
        private readonly byte[]? _Identity;
        private readonly DateTime _WrittenUtc;
        private readonly DateTime _CreatedUtc;

        private HistorySourceSnapshot(string path, string sha256, long bytes, byte[]? identity, DateTime writtenUtc, DateTime createdUtc)
        {
            Path = path;
            Sha256 = sha256;
            Bytes = bytes;
            _Identity = identity;
            _WrittenUtc = writtenUtc;
            _CreatedUtc = createdUtc;
        }

        internal static async Task<HistorySourceSnapshot> CaptureAsync(string path, CancellationToken token)
        {
            for (DirectoryInfo? ancestor = new FileInfo(path).Directory; ancestor != null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("history source has a linked ancestor");
            using (FileStream stream = Open(path))
            {
                byte[]? identity = OperatingSystem.IsLinux() ? ReadIdentity(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), String.Empty, 0x1000) : null;
                DateTime written = File.GetLastWriteTimeUtc(path);
                DateTime created = File.GetCreationTimeUtc(path);
                long bytes = stream.Length;
                string sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
                if (stream.Length != bytes || File.GetLastWriteTimeUtc(path) != written || File.GetCreationTimeUtc(path) != created)
                    throw new IOException("history source changed while its snapshot was read");
                if (identity != null && (!Same(identity, ReadIdentity(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), String.Empty, 0x1000), true)
                    || !Same(identity, ReadIdentity(-100, path, 0x100), true)))
                    throw new IOException("history source identity changed while its snapshot was read");
                return new HistorySourceSnapshot(path, sha, bytes, identity, written, created);
            }
        }

        internal async Task<bool> StillMatchesAsync(CancellationToken token)
        {
            HistorySourceSnapshot current = await CaptureAsync(Path, token).ConfigureAwait(false);
            return current.Bytes == Bytes && current.Sha256 == Sha256 && current._WrittenUtc == _WrittenUtc && current._CreatedUtc == _CreatedUtc
                && (_Identity == null || current._Identity != null && Same(_Identity, current._Identity, true));
        }

        internal async Task<string?> FreeAsync(string archiveKey, CancellationToken token)
        {
            // FileShare is not an enforced POSIX writer fence. Do not silently fall back to path-delete.
            if (!OperatingSystem.IsLinux() || _Identity == null) return "source quiescence and inode checks are unavailable on this platform; kept locally";
            string lower = System.IO.Path.GetFileName(Path).ToLowerInvariant();
            if (new[] { ".db", ".sqlite", ".sqlite3", ".vscdb", "-wal", "-shm" }.Any(suffix => lower.EndsWith(suffix, StringComparison.Ordinal)))
                return "mutable database source has no closed-owner fence; kept locally";
            string? quarantine = null;
            try
            {
                for (DirectoryInfo? ancestor = new FileInfo(Path).Directory; ancestor != null; ancestor = ancestor.Parent)
                    if (File.Exists(System.IO.Path.Combine(ancestor.FullName, ".armada-keep"))) return "source has an ancestor keep marker";
                using (FileStream held = Open(Path))
                {
                    int descriptor = held.SafeFileHandle.DangerousGetHandle().ToInt32();
                    if (!Same(_Identity, ReadIdentity(descriptor, String.Empty, 0x1000), true)
                        || !Same(_Identity, ReadIdentity(-100, Path, 0x100), true)) return "source identity changed after archive verification";
                    string parent = System.IO.Path.GetDirectoryName(Path)!;
                    quarantine = System.IO.Path.Combine(parent, QuarantinePrefix + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(quarantine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    byte[] privateDirectory = new byte[256];
                    if (statx(-100, quarantine, 0x100, 0x7ff, privateDirectory) != 0
                        || (BitConverter.ToUInt16(privateDirectory, 28) & 0xf1ff) != 0x41c0
                        || !_Identity.AsSpan(136, 8).SequenceEqual(privateDirectory.AsSpan(136, 8)))
                        return "retirement quarantine is not private and on the source filesystem; kept locally";
                    File.WriteAllText(System.IO.Path.Combine(quarantine, ".armada-keep"), "Archive retirement recovery; do not clean automatically.\n");
                    File.WriteAllText(System.IO.Path.Combine(quarantine, "recovery.json"), JsonSerializer.Serialize(new
                    {
                        originalPath = Path, archiveKey, bytes = Bytes, sha256 = Sha256,
                        deviceMajor = BitConverter.ToUInt32(_Identity, 136), deviceMinor = BitConverter.ToUInt32(_Identity, 140),
                        inode = BitConverter.ToUInt64(_Identity, 32), state = "retirement_pending"
                    }));
                    string candidate = System.IO.Path.Combine(quarantine, "content");
                    File.Move(Path, candidate, false);
                    byte[] moved = ReadIdentity(descriptor, String.Empty, 0x1000);
                    if (!Same(_Identity, moved, false) || !Same(moved, ReadIdentity(-100, candidate, 0x100), true))
                        return "source was replaced during retirement; retained in " + quarantine;
                    string? references = WritableReferenceFailure(moved, token);
                    if (references != null) return references + "; retained in " + quarantine;
                    held.Position = 0;
                    string currentSha = Convert.ToHexString(await SHA256.HashDataAsync(held, token).ConfigureAwait(false)).ToLowerInvariant();
                    token.ThrowIfCancellationRequested();
                    if (currentSha != Sha256 || !Same(moved, ReadIdentity(descriptor, String.Empty, 0x1000), true)
                        || !Same(moved, ReadIdentity(-100, candidate, 0x100), true))
                        return "source content changed during retirement; retained in " + quarantine;
                    // The supported owner is closed/idle. New ordinary pathname writers now target the
                    // original path, never this private candidate. This is not an adversarial-writer fence.
                    File.Delete(candidate);
                }
                File.Delete(System.IO.Path.Combine(quarantine, "recovery.json"));
                File.Delete(System.IO.Path.Combine(quarantine, ".armada-keep"));
                Directory.Delete(quarantine, false);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is EntryPointNotFoundException)
            {
                return ex.GetType().Name + ": " + ex.Message + (quarantine == null ? "; kept locally" : "; recovery directory " + quarantine);
            }
        }

        internal static bool IsRecoveryPath(string path)
        {
            return path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                .Any(segment => segment.StartsWith(QuarantinePrefix, StringComparison.Ordinal));
        }

        private static FileStream Open(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        private static byte[] ReadIdentity(int descriptor, string path, int flags)
        {
            // Linux UAPI struct statx has fixed-width fields in a 256-byte buffer. These offsets
            // do not use the architecture-dependent struct stat layout.
            byte[] identity = new byte[256];
            if (statx(descriptor, path, flags, 0x7ff, identity) != 0) throw new IOException("cannot bind source inode (errno " + Marshal.GetLastPInvokeError() + ")");
            if ((BitConverter.ToUInt32(identity, 0) & 0x3ff) != 0x3ff || (BitConverter.ToUInt16(identity, 28) & 0xf000) != 0x8000
                || BitConverter.ToUInt32(identity, 16) != 1 || (BitConverter.ToUInt16(identity, 28) & 0x12) != 0)
                throw new IOException("source is not an owner-write-only fully identified single-link regular file");
            return identity;
        }

        private static bool Same(byte[] expected, byte[] actual, bool includeChangeTime)
        {
            return expected.AsSpan(16, 24).SequenceEqual(actual.AsSpan(16, 24))
                && expected.AsSpan(40, 8).SequenceEqual(actual.AsSpan(40, 8))
                && expected.AsSpan(112, 16).SequenceEqual(actual.AsSpan(112, 16))
                && expected.AsSpan(136, 8).SequenceEqual(actual.AsSpan(136, 8))
                && (!includeChangeTime || expected.AsSpan(96, 16).SequenceEqual(actual.AsSpan(96, 16)));
        }

        private static string? WritableReferenceFailure(byte[] identity, CancellationToken token)
        {
            uint owner = BitConverter.ToUInt32(identity, 20);
            foreach (string process in Directory.EnumerateDirectories("/proc"))
            {
                token.ThrowIfCancellationRequested();
                if (!Int32.TryParse(System.IO.Path.GetFileName(process), out int ignored)) continue;
                try
                {
                    string uid = File.ReadLines(System.IO.Path.Combine(process, "status")).First(line => line.StartsWith("Uid:", StringComparison.Ordinal));
                    if (!uid.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).Any(value => UInt32.Parse(value, CultureInfo.InvariantCulture) == owner)) continue;
                    string birth = ProcessBirth(process);
                    foreach (string descriptor in Directory.EnumerateFiles(System.IO.Path.Combine(process, "fd")))
                    {
                        token.ThrowIfCancellationRequested();
                        byte[] reference = new byte[256];
                        if (statx(-100, descriptor, 0, 0x7ff, reference) != 0)
                        {
                            int error = Marshal.GetLastPInvokeError();
                            if (error == 2) continue; // A closed descriptor has no remaining writer.
                            return "cannot inspect a relevant process file descriptor";
                        }
                        if (!SameInode(identity, reference)) continue;
                        string flags = File.ReadLines(System.IO.Path.Combine(process, "fdinfo", System.IO.Path.GetFileName(descriptor)))
                            .First(line => line.StartsWith("flags:", StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
                        if ((Convert.ToInt64(flags, 8) & 3) != 0) return "history source still has an open writable descriptor";
                    }
                    foreach (string mapping in File.ReadLines(System.IO.Path.Combine(process, "maps")))
                    {
                        string[] fields = mapping.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length < 5 || fields[1].Length < 2 || fields[1][1] != 'w') continue;
                        string[] device = fields[3].Split(':');
                        if (device.Length == 2 && UInt32.Parse(device[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) == BitConverter.ToUInt32(identity, 136)
                            && UInt32.Parse(device[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) == BitConverter.ToUInt32(identity, 140)
                            && UInt64.Parse(fields[4], CultureInfo.InvariantCulture) == BitConverter.ToUInt64(identity, 32))
                            return "history source still has a writable memory mapping";
                    }
                    if (ProcessBirth(process) != birth) return "relevant process identity changed during writer checks";
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is FormatException)
                {
                    if (Directory.Exists(process)) return "cannot prove writer quiescence for a relevant process";
                }
            }
            return null;
        }

        private static string ProcessBirth(string directory)
        {
            string stat = File.ReadAllText(System.IO.Path.Combine(directory, "stat"));
            return stat.Substring(stat.LastIndexOf(')') + 2).Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
        }

        private static bool SameInode(byte[] left, byte[] right)
        {
            return BitConverter.ToUInt64(left, 32) == BitConverter.ToUInt64(right, 32)
                && left.AsSpan(136, 8).SequenceEqual(right.AsSpan(136, 8));
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] identity);
    }
}
