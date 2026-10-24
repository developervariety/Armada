namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Text.Json;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    internal sealed partial class HarborVerificationOutputJournal
    {
        private string MetadataPath(string id) => Path.Combine(_Root, id + ".json");
        private string StreamPath(string id, bool standardError) => Path.Combine(_Root, id + (standardError ? ".stderr" : ".stdout"));
        private string ReservationPath(string id) => Path.Combine(_ReservationRoot, id + ".reserve");
        private static string OutputId(string operationKey) => "hvo_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(operationKey))).ToLowerInvariant();

        private FileStream AcquireLease()
        {
            string path = Path.Combine(_Root, ".spool.lock");
            try
            {
                if (!File.Exists(path))
                {
                    using (FileStream creation = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                    HarborPrivateStorage.EnsurePrivateFile(path);
                }
                else HarborPrivateStorage.ValidatePrivateFile(path);
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) { throw Failure("workspace_verification_spool_busy", "Another verification output operation is active.", exception); }
        }

        private long ReadRetainedBytes()
        {
            long total = 0;
            foreach (string path in Directory.GetFiles(_Root, "hvo_*.json", SearchOption.TopDirectoryOnly))
            {
                HarborVerificationOutputRecord record = ReadRecord(Path.GetFileNameWithoutExtension(path));
                total = checked(total + record.StandardOutputRetainedBytes + record.StandardErrorRetainedBytes);
            }
            return total;
        }

        private long ReadReservedBytes()
        {
            long total = 0;
            foreach (string path in Directory.GetFiles(_ReservationRoot, "hvo_*.reserve", SearchOption.TopDirectoryOnly))
            {
                HarborPrivateStorage.ValidatePrivateFile(path);
                HarborVerificationReservationRecord reservation = ReadReservation(path);
                if (reservation.ReservedBytes < 1) throw Failure("workspace_verification_reservation_corrupt", "A verification output reservation is malformed.");
                total = checked(total + reservation.ReservedBytes);
            }
            return total;
        }

        private static void WriteReservation(string path, HarborVerificationReservationRecord reservation)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(reservation);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                HarborPrivateStorage.EnsurePrivateFile(path);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static HarborVerificationReservationRecord ReadReservation(string path)
        {
            HarborPrivateStorage.ValidatePrivateFile(path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 16 * 1024) throw Failure("workspace_verification_reservation_corrupt", "A verification output reservation exceeds its size limit.");
                byte[] bytes = new byte[checked((int)stream.Length)];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw Failure("workspace_verification_reservation_corrupt", "A verification output reservation is truncated.");
                    offset += count;
                }
                HarborVerificationReservationRecord? record = JsonSerializer.Deserialize<HarborVerificationReservationRecord>(bytes);
                if (record == null || String.IsNullOrWhiteSpace(record.OutputId) || String.IsNullOrWhiteSpace(record.OperationKey)
                    || String.IsNullOrWhiteSpace(record.CheckoutId) || record.Binding == null
                    || record.Binding.CheckoutId != record.CheckoutId)
                    throw Failure("workspace_verification_reservation_corrupt", "A verification output reservation is malformed.");
                return record;
            }
        }

        private static bool SameBinding(HarborCheckoutBinding left, HarborCheckoutBinding? right)
        {
            return right != null && left.RunnerId == right.RunnerId && left.EnrollmentGeneration == right.EnrollmentGeneration
                && left.TenantId == right.TenantId && left.UserId == right.UserId && left.RepositoryGrantId == right.RepositoryGrantId
                && left.RepositoryFingerprint == right.RepositoryFingerprint && left.CheckoutId == right.CheckoutId
                && left.BaseCommit == right.BaseCommit && left.BranchName == right.BranchName && left.Revision == right.Revision;
        }
    }
}
