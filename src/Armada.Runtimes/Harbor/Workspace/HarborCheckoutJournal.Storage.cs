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
        private void WriteNew(string path, HarborCheckoutIntent intent)
        {
            byte[] content = Serialize(intent);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                HarborPrivateStorage.EnsurePrivateFile(path);
                stream.Write(content, 0, content.Length);
                stream.Flush(true);
            }
        }

        private void WriteAtomic(string path, HarborCheckoutIntent intent)
        {
            string temporary = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp";
            try
            {
                WriteNew(temporary, intent);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static byte[] Serialize(HarborCheckoutIntent intent)
        {
            byte[] content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(intent, _JsonOptions));
            if (content.Length > MaximumRecordBytes) throw Failure("checkout_record_too_large", "The checkout record exceeds its size limit.");
            return content;
        }

        private void DeleteUnpublishedStaging(string directory)
        {
            if (!Directory.Exists(directory)) return;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw Failure("checkout_staging_invalid", "An unpublished checkout staging path is a link.");
            string[] files = Directory.GetFiles(directory);
            string[] directories = Directory.GetDirectories(directory);
            if (files.Length > 1 || directories.Length > 0)
                throw Failure("checkout_staging_invalid", "An unpublished checkout staging path contains unexpected entries.");
            foreach (string file in files)
            {
                if (!String.Equals(Path.GetFileName(file), "checkout.json", StringComparison.Ordinal))
                    throw Failure("checkout_staging_invalid", "An unpublished checkout staging path contains an unknown file.");
                HarborPrivateStorage.ValidatePrivateFile(file);
                File.Delete(file);
            }
            Directory.Delete(directory, false);
        }

        private string DirectoryFor(string checkoutId) => Path.Combine(_Root, checkoutId);
        private string RecordPath(string checkoutId) => Path.Combine(DirectoryFor(checkoutId), "checkout.json");

    }
}
