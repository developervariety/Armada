namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Harbor;

    /// <summary>Stores bounded verification streams under the runner's pinned verification reservation.</summary>
    internal sealed partial class HarborVerificationOutputJournal
    {
        private const int MaximumOutputBytesPerStream = 16 * 1024 * 1024;
        private const int MaximumRenderedMarkerBytes = 512;
        private readonly long _ReservationBytes;
        private readonly string _Root;
        private readonly string _ReservationRoot;

        /// <summary>Opens the output journal under the runner's configured verification reservation.</summary>
        /// <param name="options">Validated runner storage settings.</param>
        public HarborVerificationOutputJournal(HarborSupervisorOptions options)
        {
            _ReservationBytes = options.MaxVerificationSpoolBytes;
            _Root = Path.Combine(Path.GetFullPath(options.StateDirectory), "verification-output");
            _ReservationRoot = Path.Combine(_Root, "reservations");
            HarborPrivateStorage.EnsurePrivateDirectory(_Root);
            HarborPrivateStorage.EnsurePrivateDirectory(_ReservationRoot);
        }

        /// <summary>Reserves bounded output capacity before starting a verification process.</summary>
        /// <param name="request">Owner-bound operation with a stable operation key.</param>
        /// <param name="bytesPerStream">Maximum retained bytes for each output stream.</param>
        /// <returns>Opaque output identifier for the reserved streams.</returns>
        public string Reserve(HarborWorkspaceOperationRequest request, int bytesPerStream)
        {
            if (_ReservationBytes <= 0) throw Failure("workspace_verification_storage_not_configured", "The runner has no pinned verification output reservation.");
            if (bytesPerStream < 1 || bytesPerStream > MaximumOutputBytesPerStream)
                throw Failure("workspace_verification_limits_invalid", "The verification output limit is outside the allowed range.");
            string outputId = OutputId(request.OperationKey!);
            using (FileStream lease = AcquireLease())
            {
                long used = ReadRetainedBytes();
                long reserved = ReadReservedBytes();
                long requested = checked(((long)bytesPerStream + MaximumRenderedMarkerBytes) * 2);
                if (used > _ReservationBytes - reserved || requested > _ReservationBytes - used - reserved)
                    throw Failure("workspace_verification_storage_full", "The pinned verification output reservation is full.");
                string path = ReservationPath(outputId);
                if (File.Exists(path)) throw Failure("workspace_verification_output_uncertain", "A prior verification output reservation is incomplete.");
                WriteReservation(path, new HarborVerificationReservationRecord
                {
                    OutputId = outputId, OperationKey = request.OperationKey!, CheckoutId = request.Binding!.CheckoutId,
                    ReservedBytes = requested, Binding = request.Binding
                });
            }
            return outputId;
        }

        /// <summary>Refuses a new checkout mutation while another verification may still write.</summary>
        /// <param name="checkoutId">Checkout to inspect.</param>
        /// <param name="operationKey">Current verification key, when checking its own reservation.</param>
        public void EnsureNoUncertainWriter(string checkoutId, string? operationKey)
        {
            using (FileStream lease = AcquireLease())
            {
                foreach (string path in Directory.GetFiles(_ReservationRoot, "hvo_*.reserve", SearchOption.TopDirectoryOnly))
                {
                    HarborVerificationReservationRecord reservation = ReadReservation(path);
                    if (reservation.CheckoutId == checkoutId && reservation.OperationKey != operationKey)
                        throw Failure("workspace_verification_writer_uncertain", "A prior verification may still own this checkout.");
                }
            }
        }

        private static HarborJobJournalException Failure(string reason, string message, Exception? inner = null)
            => new HarborJobJournalException(reason, message, inner);
    }
}
