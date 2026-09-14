namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Durable restart record in private storage. Every change is a compare-and-swap under an exclusive
    /// record lock, written to a flushed temporary file and atomically renamed over the record.
    /// </summary>
    public sealed class SelfDeployRestartRecordStore
    {
        /// <summary>
        /// Environment variable carrying the operation id into a supervised launch.
        /// </summary>
        public const string OperationIdVariable = "ARMADA_SELF_DEPLOY_OPERATION_ID";

        /// <summary>
        /// Maximum accepted record size in bytes.
        /// </summary>
        public const int MaximumRecordBytes = 1024 * 1024;

        private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        private static readonly TimeSpan RecordLockTimeout = TimeSpan.FromSeconds(10);
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

        private readonly string _Directory;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="directory">Private self-deploy state directory.</param>
        public SelfDeployRestartRecordStore(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentNullException(nameof(directory));
            _Directory = Path.GetFullPath(directory);
        }

        /// <summary>
        /// Record file path.
        /// </summary>
        public string RecordPath => Path.Combine(_Directory, "restart-record.json");

        /// <summary>
        /// Default state directory for a data directory.
        /// </summary>
        /// <param name="dataDirectory">Armada data directory.</param>
        /// <returns>Self-deploy state directory.</returns>
        public static string DirectoryFor(string dataDirectory)
        {
            return Path.Combine(dataDirectory, "self-deploy");
        }

        /// <summary>
        /// Read the record without taking the record lock.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Read result; a present but invalid record carries a failure reason.</returns>
        public async Task<SelfDeployRestartRecordReadResult> ReadAsync(CancellationToken token = default)
        {
            if (!File.Exists(RecordPath)) return new SelfDeployRestartRecordReadResult { Exists = false };
            try
            {
                FileInfo info = new FileInfo(RecordPath);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    return Unreadable("restart_record_symlink_refused");
                if (info.Length > MaximumRecordBytes) return Unreadable("restart_record_too_large");
                byte[] bytes = await File.ReadAllBytesAsync(RecordPath, token).ConfigureAwait(false);
                SelfDeployRestartRecord? record = JsonSerializer.Deserialize<SelfDeployRestartRecord>(bytes, JsonOptions);
                if (record == null) return Unreadable("restart_record_empty");
                if (record.FormatVersion != 1) return Unreadable("restart_record_format_unsupported");
                if (String.IsNullOrWhiteSpace(record.OperationId)) return Unreadable("restart_record_operation_missing");
                return new SelfDeployRestartRecordReadResult { Exists = true, Record = record };
            }
            catch (JsonException)
            {
                return Unreadable("restart_record_unreadable");
            }
            catch (IOException)
            {
                return Unreadable("restart_record_io_failed");
            }
            catch (UnauthorizedAccessException)
            {
                return Unreadable("restart_record_access_denied");
            }
        }

        /// <summary>
        /// Create a new record unless a non-terminal or unreadable record exists.
        /// </summary>
        /// <param name="record">New record.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Transition result; not applied when another restart is in progress or state is unknown.</returns>
        public async Task<SelfDeployRestartTransitionResult> CreateAsync(SelfDeployRestartRecord record, CancellationToken token = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            EnsureDirectory();
            using (FileStream recordLock = await AcquireRecordLockAsync(token).ConfigureAwait(false))
            {
                SelfDeployRestartRecordReadResult current = await ReadAsync(token).ConfigureAwait(false);
                if (current.Exists && !current.IsReadable)
                    return NotApplied(null, current.FailureReason);
                if (current.IsReadable && !SelfDeployRestartRecord.IsTerminal(current.Record!.State))
                    return NotApplied(current.Record, "restart_in_progress");
                await WriteUnlockedAsync(record, token).ConfigureAwait(false);
                return new SelfDeployRestartTransitionResult { Applied = true, Record = record };
            }
        }

        /// <summary>
        /// Apply a change only when the record belongs to the operation and is in one of the expected states.
        /// </summary>
        /// <param name="operationId">Expected operation id.</param>
        /// <param name="expectedStates">States from which the change is allowed.</param>
        /// <param name="mutate">Change applied to the current record before it is written.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Transition result.</returns>
        public async Task<SelfDeployRestartTransitionResult> TryTransitionAsync(
            string operationId,
            IReadOnlyCollection<SelfDeployRestartStateEnum> expectedStates,
            Action<SelfDeployRestartRecord> mutate,
            CancellationToken token = default)
        {
            if (expectedStates == null) throw new ArgumentNullException(nameof(expectedStates));
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));
            EnsureDirectory();
            using (FileStream recordLock = await AcquireRecordLockAsync(token).ConfigureAwait(false))
            {
                SelfDeployRestartRecordReadResult current = await ReadAsync(token).ConfigureAwait(false);
                if (!current.Exists) return NotApplied(null, "restart_record_missing");
                if (!current.IsReadable) return NotApplied(null, current.FailureReason);
                SelfDeployRestartRecord record = current.Record!;
                if (!String.Equals(record.OperationId, operationId, StringComparison.Ordinal))
                    return NotApplied(record, "operation_mismatch");
                bool expected = false;
                foreach (SelfDeployRestartStateEnum state in expectedStates)
                {
                    if (state == record.State) expected = true;
                }
                if (!expected) return NotApplied(record, "unexpected_state_" + record.State);
                mutate(record);
                await WriteUnlockedAsync(record, token).ConfigureAwait(false);
                return new SelfDeployRestartTransitionResult { Applied = true, Record = record };
            }
        }

        /// <summary>
        /// Read the record and run an action while holding the record lock, so no record can be created or
        /// changed until the action finishes.
        /// </summary>
        /// <typeparam name="T">Action result type.</typeparam>
        /// <param name="action">Action given the current read result.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Action result.</returns>
        public async Task<T> ReadUnderRecordLockAsync<T>(
            Func<SelfDeployRestartRecordReadResult, Task<T>> action,
            CancellationToken token = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            EnsureDirectory();
            using (FileStream recordLock = await AcquireRecordLockAsync(token).ConfigureAwait(false))
            {
                SelfDeployRestartRecordReadResult current = await ReadAsync(token).ConfigureAwait(false);
                return await action(current).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Try to take the long-lived supervisor lock without waiting.
        /// </summary>
        /// <returns>Held lock, or null when another supervisor or recovery run holds it.</returns>
        public IDisposable? TryAcquireSupervisorLock()
        {
            EnsureDirectory();
            return TryOpenLock(Path.Combine(_Directory, "supervisor.lock"));
        }

        private void EnsureDirectory()
        {
            try
            {
                SelfDeployPrivateFile.CreateDirectory(_Directory);
            }
            catch (SelfDeployPrivateStorageException ex)
            {
                throw new SelfDeployCutoverException(ex.FailureReason, ex);
            }
        }

        private async Task<FileStream> AcquireRecordLockAsync(CancellationToken token)
        {
            string lockPath = Path.Combine(_Directory, "record.lock");
            DateTime deadline = DateTime.UtcNow + RecordLockTimeout;
            while (true)
            {
                FileStream? stream = TryOpenLock(lockPath);
                if (stream != null) return stream;
                if (DateTime.UtcNow >= deadline) throw new SelfDeployCutoverException("restart_record_lock_timeout");
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }

        private static FileStream? TryOpenLock(string lockPath)
        {
            FileStreamOptions options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : PrivateFileMode
            };
            try
            {
                return new FileStream(lockPath, options);
            }
            catch (IOException)
            {
                return null;
            }
        }

        private async Task WriteUnlockedAsync(SelfDeployRestartRecord record, CancellationToken token)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            if (bytes.Length > MaximumRecordBytes) throw new SelfDeployCutoverException("restart_record_too_large");
            string temporaryPath = Path.Combine(_Directory, "restart-record." + Guid.NewGuid().ToString("N") + ".tmp");
            FileStreamOptions options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : PrivateFileMode
            };
            try
            {
                using (FileStream stream = new FileStream(temporaryPath, options))
                {
                    await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                    stream.Flush(true);
                }
                File.Move(temporaryPath, RecordPath, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OperationCanceledException)
            {
                string cleanupFailure = String.Empty;
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception cleanupEx) when (cleanupEx is IOException || cleanupEx is UnauthorizedAccessException)
                {
                    cleanupFailure = "_temporary_cleanup_failed";
                }
                if (ex is OperationCanceledException) throw;
                throw new SelfDeployCutoverException("restart_record_write_failed" + cleanupFailure, ex);
            }
        }

        private static SelfDeployRestartRecordReadResult Unreadable(string reason)
        {
            return new SelfDeployRestartRecordReadResult { Exists = true, FailureReason = reason };
        }

        private static SelfDeployRestartTransitionResult NotApplied(SelfDeployRestartRecord? record, string reason)
        {
            return new SelfDeployRestartTransitionResult { Applied = false, Record = record, FailureReason = reason };
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
