namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Text.Json;

    /// <summary>Persists the bounded storage reservation for a journal root.</summary>
    public sealed partial class HarborJobJournal
    {
        private sealed class StoragePolicyRecord
        {
            public StoragePolicyRecord() { }
            public long MaxSpoolBytes { get; set; }
            public int MaxRetainedJobs { get; set; }
        }

        private void EnsureStoragePolicyPinned()
        {
            string path = Path.Combine(_Root, ".storage-policy.json");
            if (!File.Exists(path))
            {
                StoragePolicyRecord initial = new StoragePolicyRecord
                {
                    MaxSpoolBytes = _Options.MaxSpoolBytes,
                    MaxRetainedJobs = _Options.MaxRetainedJobs
                };
                AtomicWrite(path, JsonSerializer.Serialize(initial, _JsonOptions));
                return;
            }

            StoragePolicyRecord? stored;
            try { stored = JsonSerializer.Deserialize<StoragePolicyRecord>(ReadBoundedTextFile(path, "storage_policy_too_large", "storage_policy_invalid"), _JsonOptions); }
            catch (JsonException exception) { throw Failure("storage_policy_invalid", "The pinned storage policy is malformed.", exception); }
            if (stored == null || stored.MaxSpoolBytes != _Options.MaxSpoolBytes || stored.MaxRetainedJobs != _Options.MaxRetainedJobs)
                throw Failure("storage_policy_mismatch", "The configured storage reservation differs from the journal's pinned policy.");
        }
    }
}
