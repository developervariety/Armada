namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;

    /// <summary>Private bounded storage for operation replay records.</summary>
    internal sealed partial class HarborWorkspaceOperationJournal
    {
        private static readonly object _LeaseSync = new object();
        private static readonly JsonSerializerOptions _Options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };

        private FileStream AcquireLease(string key)
        {
            string path = RecordPath(key) + ".lock";
            lock (_LeaseSync)
            {
                try
                {
                    PrepareLeaseFile(path);
                    return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException exception) { throw Failure("workspace_operation_busy", "Another attempt owns this operation key.", exception); }
                catch (UnauthorizedAccessException exception) { throw Failure("workspace_operation_lock_unavailable", "The operation replay lock cannot be opened.", exception); }
            }
        }

        private HarborWorkspaceOperationRecord Read(string path)
        {
            try
            {
                HarborPrivateStorage.ValidatePrivateFile(path);
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumRecordBytes) throw Failure("workspace_operation_record_invalid", "The operation record exceeds its size limit.");
                    byte[] bytes = ReadRecordBytes(stream);
                    HarborWorkspaceOperationRecord? record = JsonSerializer.Deserialize<HarborWorkspaceOperationRecord>(bytes, _Options);
                    if (record == null || String.IsNullOrWhiteSpace(record.OperationKey) || String.IsNullOrWhiteSpace(record.SemanticHash) || record.SemanticHash.Length != 64
                        || record.Completed != (record.Result != null))
                        throw Failure("workspace_operation_record_invalid", "The operation record is incomplete.");
                    return record;
                }
            }
            catch (HarborJobJournalException) { throw; }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is JsonException || exception is ArgumentException)
            { throw Failure("workspace_operation_record_invalid", "The operation record cannot be trusted.", exception); }
        }

        private static void PrepareLeaseFile(string path)
        {
            if (File.Exists(path))
            {
                HarborPrivateStorage.ValidatePrivateFile(path);
                return;
            }
            using (FileStream creation = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            HarborPrivateStorage.EnsurePrivateFile(path);
        }

        private byte[] ReadRecordBytes(FileStream stream)
        {
            byte[] bytes = new byte[checked((int)stream.Length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) throw Failure("workspace_operation_record_invalid", "The operation record is truncated.");
                offset += count;
            }
            return bytes;
        }

        private void Write(string path, HarborWorkspaceOperationRecord record)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, _Options));
            if (bytes.Length > MaximumRecordBytes) throw Failure("workspace_operation_record_too_large", "The operation record exceeds its size limit.");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    HarborPrivateStorage.EnsurePrivateFile(temporary);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
