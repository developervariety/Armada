namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>Validates a checkout-bound child launch against its local durable owner record.</summary>
    internal static class HarborCheckoutLaunchAdmission
    {
        public static void Validate(HarborSupervisorStart start)
        {
            HarborLaunchRequest request = start.Request;
            HarborCheckoutBinding? binding = request.CheckoutBinding;
            HarborRepositoryGrant? grant = start.RepositoryGrant;
            if (binding == null)
            {
                if (grant != null || request.DockId != null)
                    throw Failure("checkout_launch_binding_incomplete", "A mapped launch cannot carry partial checkout ownership.");
                return;
            }
            if (grant == null || !binding.IsComplete || String.IsNullOrWhiteSpace(request.DockId))
                throw Failure("checkout_launch_binding_incomplete", "A checkout launch requires one complete configured grant.");

            grant.Validate();
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(grant.RepositoryUrl.Trim()))).ToLowerInvariant();
            if (!String.Equals(binding.RepositoryGrantId, grant.Id, StringComparison.Ordinal)
                || !String.Equals(binding.RepositoryFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
                || !String.Equals(binding.TenantId, grant.TenantId, StringComparison.Ordinal)
                || !String.Equals(binding.UserId, grant.UserId, StringComparison.Ordinal))
                throw Failure("checkout_launch_binding_mismatch", "The launch binding does not match the selected repository grant.");

            HarborCheckoutJournal journal = new HarborCheckoutJournal(start.Options.StateDirectory);
            HarborCheckoutIntent intent = journal.Read(binding.CheckoutId);
            if (!SameBinding(intent.Binding, binding) || !String.Equals(intent.State, "Ready", StringComparison.Ordinal)
                || !String.Equals(intent.DockId, request.DockId, StringComparison.Ordinal)
                || !String.Equals(intent.VesselId, grant.VesselId, StringComparison.Ordinal))
                throw Failure("checkout_launch_not_ready", "The selected checkout is not durably ready for this dock and owner.");

            string checkout = Path.GetFullPath(Path.Combine(grant.CheckoutRoot, binding.CheckoutId));
            string working = Path.GetFullPath(request.WorkingDirectory);
            if (!PathContainment.IsWithin(checkout, working))
                throw Failure("checkout_launch_path_mismatch", "The launch working directory is outside the canonical owned checkout.");
            EnsureNoLinks(working);
            HarborCheckoutGitIdentity.Validate(checkout, grant.RepositoryPath);
        }

        private static bool SameBinding(HarborCheckoutBinding left, HarborCheckoutBinding right) =>
            left.RunnerId == right.RunnerId && left.EnrollmentGeneration == right.EnrollmentGeneration
            && left.TenantId == right.TenantId && left.UserId == right.UserId
            && left.RepositoryGrantId == right.RepositoryGrantId && left.RepositoryFingerprint == right.RepositoryFingerprint
            && left.CheckoutId == right.CheckoutId && left.BaseCommit == right.BaseCommit
            && left.BranchName == right.BranchName && left.Revision == right.Revision;

        private static void EnsureNoLinks(string path)
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (String.IsNullOrEmpty(root)) throw Failure("checkout_launch_path_invalid", "The checkout path is invalid.");
            string current = root;
            foreach (string segment in full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) && !File.Exists(current)) throw Failure("checkout_launch_path_missing", "The checkout path is incomplete.");
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw Failure("checkout_launch_path_link_refused", "The checkout path contains a symbolic link or reparse point.");
            }
        }

        private static HarborJobJournalException Failure(string reason, string message) => new HarborJobJournalException(reason, message);
    }
}
