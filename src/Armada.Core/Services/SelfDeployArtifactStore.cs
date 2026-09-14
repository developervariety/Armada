namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Content-addressed store of read-only server artifacts in private storage. An artifact directory is
    /// named by the digest of its files, never overwritten, and re-verified before every launch.
    /// </summary>
    public sealed class SelfDeployArtifactStore : ISelfDeployArtifactStore
    {
        /// <summary>
        /// Maximum files in one artifact.
        /// </summary>
        public const int MaximumFiles = 20000;

        /// <summary>
        /// Maximum total bytes in one artifact.
        /// </summary>
        public const long MaximumBytes = 2L * 1024 * 1024 * 1024;

        private const UnixFileMode ReadOnlyFileMode = UnixFileMode.UserRead;
        private const UnixFileMode ReadOnlyDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        private readonly string _Root;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="root">Private releases directory.</param>
        public SelfDeployArtifactStore(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentNullException(nameof(root));
            _Root = Path.GetFullPath(root);
        }

        /// <inheritdoc />
        public async Task<SelfDeployReleaseArtifact> CaptureAsync(
            string sourceDirectory,
            string entryAssembly,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(sourceDirectory)) throw new SelfDeployCutoverException("artifact_source_missing");
            if (String.IsNullOrWhiteSpace(entryAssembly)
                || !String.Equals(Path.GetFileName(entryAssembly), entryAssembly, StringComparison.Ordinal))
                throw new SelfDeployCutoverException("artifact_entry_invalid");
            string source = Path.GetFullPath(sourceDirectory);
            if (!Directory.Exists(source)) throw new SelfDeployCutoverException("artifact_source_missing");
            if (!File.Exists(Path.Combine(source, entryAssembly))) throw new SelfDeployCutoverException("artifact_entry_missing");

            try
            {
                SelfDeployPrivateFile.CreateDirectory(_Root);
            }
            catch (SelfDeployPrivateStorageException ex)
            {
                throw new SelfDeployCutoverException(ex.FailureReason, ex);
            }

            List<string> sourceFiles = EnumerateFiles(source);
            string sourceDigest = await ComputeDigestAsync(source, sourceFiles, token).ConfigureAwait(false);
            string target = Path.Combine(_Root, sourceDigest);
            if (Directory.Exists(target))
                return await ReuseExistingAsync(target, sourceDigest, entryAssembly, sourceFiles.Count, token).ConfigureAwait(false);

            string staging = Path.Combine(_Root, ".staging-" + Guid.NewGuid().ToString("N"));
            bool moved = false;
            try
            {
                SelfDeployPrivateFile.CreateDirectory(staging);
                foreach (string relative in sourceFiles)
                {
                    token.ThrowIfCancellationRequested();
                    string destination = Path.Combine(staging, relative);
                    string? parent = Path.GetDirectoryName(destination);
                    if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    File.Copy(Path.Combine(source, relative), destination, false);
                }

                List<string> stagedFiles = EnumerateFiles(staging);
                string stagedDigest = await ComputeDigestAsync(staging, stagedFiles, token).ConfigureAwait(false);
                if (!String.Equals(stagedDigest, sourceDigest, StringComparison.Ordinal))
                    throw new SelfDeployCutoverException("artifact_source_changed_during_capture");

                try
                {
                    Directory.Move(staging, target);
                    moved = true;
                }
                catch (IOException) when (Directory.Exists(target))
                {
                    return await ReuseExistingAsync(target, sourceDigest, entryAssembly, sourceFiles.Count, token).ConfigureAwait(false);
                }

                MakeReadOnly(target);
                return new SelfDeployReleaseArtifact
                {
                    Digest = sourceDigest,
                    Directory = target,
                    EntryAssembly = entryAssembly,
                    FileCount = sourceFiles.Count
                };
            }
            catch (SelfDeployPrivateStorageException ex)
            {
                throw new SelfDeployCutoverException(ex.FailureReason, ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new SelfDeployCutoverException("artifact_copy_failed", ex);
            }
            finally
            {
                if (!moved && Directory.Exists(staging))
                {
                    try
                    {
                        Directory.Delete(staging, true);
                    }
                    catch (Exception cleanupEx) when (cleanupEx is IOException || cleanupEx is UnauthorizedAccessException)
                    {
                        // A leftover staging directory has a unique name and is never read as an artifact;
                        // its presence does not change the failure already being reported.
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<string?> VerifyAsync(SelfDeployReleaseArtifact artifact, CancellationToken token = default)
        {
            if (artifact == null) return "artifact_missing";
            if (String.IsNullOrWhiteSpace(artifact.Digest) || String.IsNullOrWhiteSpace(artifact.Directory))
                return "artifact_missing";
            string expectedDirectory = Path.Combine(_Root, artifact.Digest);
            if (!String.Equals(Path.GetFullPath(artifact.Directory), expectedDirectory, StringComparison.Ordinal))
                return "artifact_outside_store";
            if (!Directory.Exists(expectedDirectory)) return "artifact_directory_missing";
            if (String.IsNullOrWhiteSpace(artifact.EntryAssembly)
                || !File.Exists(Path.Combine(expectedDirectory, artifact.EntryAssembly)))
                return "artifact_entry_missing";
            try
            {
                List<string> files = EnumerateFiles(expectedDirectory);
                string digest = await ComputeDigestAsync(expectedDirectory, files, token).ConfigureAwait(false);
                if (!String.Equals(digest, artifact.Digest, StringComparison.Ordinal)) return "artifact_digest_mismatch";
                return null;
            }
            catch (SelfDeployCutoverException ex)
            {
                return ex.FailureReason;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "artifact_read_failed";
            }
        }

        /// <inheritdoc />
        public Task<SelfDeployReleasePruneResult> PruneAsync(
            IReadOnlyCollection<string> protectedDigests,
            int retainPrevious,
            CancellationToken token = default)
        {
            if (protectedDigests == null) throw new ArgumentNullException(nameof(protectedDigests));
            SelfDeployReleasePruneResult result = new SelfDeployReleasePruneResult();
            if (!Directory.Exists(_Root)) return Task.FromResult(result);

            HashSet<string> keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (string digest in protectedDigests)
            {
                if (!String.IsNullOrWhiteSpace(digest)) keep.Add(digest);
            }

            List<DirectoryInfo> previous = new List<DirectoryInfo>();
            foreach (string entry in Directory.EnumerateDirectories(_Root))
            {
                DirectoryInfo directory = new DirectoryInfo(entry);
                if (!IsDigestName(directory.Name) || keep.Contains(directory.Name)) continue;
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // A symlink is never followed or deleted through; it is reported instead.
                    result.FailureReason = "release_symlink_skipped";
                    continue;
                }
                previous.Add(directory);
            }

            previous.Sort((left, right) =>
            {
                int byTime = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                return byTime != 0 ? byTime : String.CompareOrdinal(left.Name, right.Name);
            });

            int retain = Math.Max(0, retainPrevious);
            for (int i = 0; i < previous.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i < retain)
                {
                    result.RetainedPrevious.Add(previous[i].Name);
                    continue;
                }
                try
                {
                    MakeWritable(previous[i].FullName);
                    Directory.Delete(previous[i].FullName, true);
                    result.Removed.Add(previous[i].Name);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.FailureReason = "release_delete_failed";
                }
            }

            return Task.FromResult(result);
        }

        private static bool IsDigestName(string name)
        {
            if (name.Length != 64) return false;
            foreach (char character in name)
            {
                bool hex = (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        private static void MakeWritable(string root)
        {
            if (OperatingSystem.IsWindows()) return;
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
            }
        }

        private async Task<SelfDeployReleaseArtifact> ReuseExistingAsync(
            string target,
            string digest,
            string entryAssembly,
            int fileCount,
            CancellationToken token)
        {
            SelfDeployReleaseArtifact existing = new SelfDeployReleaseArtifact
            {
                Digest = digest,
                Directory = target,
                EntryAssembly = entryAssembly,
                FileCount = fileCount
            };
            string? failure = await VerifyAsync(existing, token).ConfigureAwait(false);
            if (failure != null) throw new SelfDeployCutoverException("artifact_existing_copy_invalid");
            return existing;
        }

        private static List<string> EnumerateFiles(string root)
        {
            List<string> files = new List<string>();
            long totalBytes = 0;
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new SelfDeployCutoverException("artifact_symlink_refused");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }
                    totalBytes += new FileInfo(entry).Length;
                    files.Add(Path.GetRelativePath(root, entry));
                    if (files.Count > MaximumFiles || totalBytes > MaximumBytes)
                        throw new SelfDeployCutoverException("artifact_too_large");
                }
            }
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        private static async Task<string> ComputeDigestAsync(string root, List<string> files, CancellationToken token)
        {
            using (IncrementalHash tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                foreach (string relative in files)
                {
                    token.ThrowIfCancellationRequested();
                    string normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
                    tree.AppendData(Encoding.UTF8.GetBytes(normalized));
                    tree.AppendData(new byte[] { 0 });
                    using (FileStream stream = new FileStream(Path.Combine(root, relative), FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        byte[] contentHash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                        tree.AppendData(BitConverter.GetBytes(stream.Length));
                        tree.AppendData(contentHash);
                    }
                }
                return Convert.ToHexString(tree.GetHashAndReset()).ToLowerInvariant();
            }
        }

        private static void MakeReadOnly(string root)
        {
            if (OperatingSystem.IsWindows()) return;
            List<string> directories = new List<string>();
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                directories.Add(directory);
                foreach (string file in Directory.EnumerateFiles(directory)) File.SetUnixFileMode(file, ReadOnlyFileMode);
                foreach (string child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            for (int i = directories.Count - 1; i >= 0; i--) File.SetUnixFileMode(directories[i], ReadOnlyDirectoryMode);
        }
    }
}
