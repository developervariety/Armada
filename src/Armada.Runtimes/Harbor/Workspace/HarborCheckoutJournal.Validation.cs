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
        private static void ValidateIntent(HarborCheckoutIntent intent)
        {
            if (intent.Binding == null || !intent.Binding.IsComplete || String.IsNullOrWhiteSpace(intent.DockId)
                || String.IsNullOrWhiteSpace(intent.VesselId) || String.IsNullOrWhiteSpace(intent.RequestId)
                || !String.Equals(intent.State, "Pending", StringComparison.Ordinal)
                && !String.Equals(intent.State, "Ready", StringComparison.Ordinal)
                && !String.Equals(intent.State, "Removing", StringComparison.Ordinal)
                && !String.Equals(intent.State, "Removed", StringComparison.Ordinal)
                && !String.Equals(intent.State, "Uncertain", StringComparison.Ordinal))
                throw Failure("checkout_record_invalid", "The checkout record failed validation.");
            ValidateCheckoutId(intent.Binding.CheckoutId);
        }

        private static void ValidateCheckoutId(string checkoutId)
        {
            if (checkoutId.Length != 36 || !checkoutId.StartsWith("hco_", StringComparison.Ordinal))
                throw Failure("checkout_id_invalid", "The checkout identifier is invalid.");
            for (int index = 4; index < checkoutId.Length; index++)
                if (!(checkoutId[index] >= '0' && checkoutId[index] <= '9') && !(checkoutId[index] >= 'a' && checkoutId[index] <= 'f'))
                    throw Failure("checkout_id_invalid", "The checkout identifier is invalid.");
        }

        private static bool SameBinding(HarborCheckoutIntent left, HarborCheckoutIntent right)
        {
            HarborCheckoutBinding first = left.Binding;
            HarborCheckoutBinding second = right.Binding;
            return first.RunnerId == second.RunnerId && first.EnrollmentGeneration == second.EnrollmentGeneration
                && first.TenantId == second.TenantId && first.UserId == second.UserId
                && first.RepositoryGrantId == second.RepositoryGrantId && first.RepositoryFingerprint == second.RepositoryFingerprint
                && first.CheckoutId == second.CheckoutId && first.BaseCommit == second.BaseCommit
                && first.BranchName == second.BranchName && first.Revision == second.Revision
                && left.DockId == right.DockId && left.VesselId == right.VesselId && left.RequestId == right.RequestId;
        }

        private static HarborJobJournalException Failure(string reason, string message) => new HarborJobJournalException(reason, message);
    }
}
