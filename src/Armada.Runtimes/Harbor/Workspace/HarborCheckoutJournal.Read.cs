namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Models;

    /// <summary>Stores private durable intents for runner-owned Git checkouts.</summary>
    internal sealed partial class HarborCheckoutJournal
    {
        private List<HarborCheckoutIntent> EnumerateUnsafe()
        {
            List<HarborCheckoutIntent> records = new List<HarborCheckoutIntent>();
            foreach (string directory in Directory.EnumerateDirectories(_Root))
            {
                string name = Path.GetFileName(directory);
                if (name.StartsWith(".pending-", StringComparison.Ordinal))
                {
                    DeleteUnpublishedStaging(directory);
                    continue;
                }
                ValidateCheckoutId(name);
                records.Add(ReadUnsafe(name));
                if (records.Count > MaximumRecords) throw Failure("checkout_limit_invalid", "The checkout journal exceeds its record limit.");
            }
            return records;
        }

        private HarborCheckoutIntent ReadUnsafe(string checkoutId)
        {
            string directory = DirectoryFor(checkoutId);
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw Failure("checkout_record_missing", "The checkout record is unavailable.");
            string path = RecordPath(checkoutId);
            try
            {
                HarborPrivateStorage.ValidatePrivateFile(path);
                byte[] content = ReadBounded(path);
                if (content.Length < 1) throw Failure("checkout_record_invalid", "The checkout record is empty.");
                HarborCheckoutIntent? intent = JsonSerializer.Deserialize<HarborCheckoutIntent>(content, _JsonOptions);
                if (intent == null) throw Failure("checkout_record_invalid", "The checkout record is empty.");
                ValidateIntent(intent);
                if (!String.Equals(intent.Binding.CheckoutId, checkoutId, StringComparison.Ordinal))
                    throw Failure("checkout_record_invalid", "The checkout record identity does not match its directory.");
                return intent;
            }
            catch (HarborJobJournalException) { throw; }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is JsonException || exception is ArgumentException || exception is System.Security.SecurityException)
            {
                throw new HarborJobJournalException("checkout_record_invalid", "The checkout record cannot be trusted.", exception);
            }
        }

        private static byte[] ReadBounded(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[MaximumRecordBytes + 1];
                int total = 0;
                while (total < buffer.Length)
                {
                    int read = stream.Read(buffer, total, buffer.Length - total);
                    if (read == 0) break;
                    total += read;
                }
                if (total > MaximumRecordBytes) throw Failure("checkout_record_invalid", "The checkout record exceeds its size limit.");
                byte[] result = new byte[total];
                Buffer.BlockCopy(buffer, 0, result, 0, total);
                return result;
            }
        }

    }
}
