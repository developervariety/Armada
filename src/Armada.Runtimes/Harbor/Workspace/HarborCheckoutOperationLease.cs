namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>Cross-process exclusive lease shared by checkout mutations and bound child admission.</summary>
    internal static class HarborCheckoutOperationLease
    {
        private static readonly object _Sync = new object();

        /// <summary>Acquires the cross-process lease for a bound checkout.</summary>
        /// <param name="stateDirectory">Private runner journal root.</param>
        /// <param name="checkoutId">Opaque checkout identity to serialize.</param>
        /// <returns>An open exclusive lease handle. Dispose it to release the checkout.</returns>
        public static FileStream Acquire(string stateDirectory, string checkoutId)
        {
            if (String.IsNullOrWhiteSpace(checkoutId)) throw Failure("checkout_id_invalid", "A checkout identifier is required.");
            string directory = Path.Combine(Path.GetFullPath(stateDirectory), "workspace-checkout-locks");
            HarborPrivateStorage.EnsurePrivateDirectory(directory);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkoutId))).ToLowerInvariant();
            string path = Path.Combine(directory, key + ".lock");
            lock (_Sync)
            {
                try
                {
                    PrepareLockFile(path);
                    return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException exception)
                {
                    throw Failure("checkout_busy", "Another operation owns this checkout.", exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw Failure("checkout_lock_unavailable", "The checkout operation lease cannot be opened.", exception);
                }
            }
        }

        private static void PrepareLockFile(string path)
        {
            if (File.Exists(path))
            {
                HarborPrivateStorage.ValidatePrivateFile(path);
                return;
            }
            using (FileStream creation = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            HarborPrivateStorage.EnsurePrivateFile(path);
        }

        private static HarborJobJournalException Failure(string reason, string message, Exception? inner = null) =>
            new HarborJobJournalException(reason, message, inner);
    }
}
