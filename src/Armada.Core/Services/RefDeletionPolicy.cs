namespace Armada.Core.Services
{
    /// <summary>Limits platform cleanup to owned branch and retention namespaces.</summary>
    public static class RefDeletionPolicy
    {
        /// <summary>Refuse user branches and parked recovery refs before a git mutation.</summary>
        public static void RequireManaged(string reference)
        {
            string branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? reference.Substring("refs/heads/".Length) : reference;
            if (branch.StartsWith("armada/", StringComparison.Ordinal)
                || branch.StartsWith("armada-landing/", StringComparison.Ordinal)
                || reference.StartsWith("refs/armada-preserved/", StringComparison.Ordinal)
                || reference.StartsWith("refs/armada/docks/", StringComparison.Ordinal)
                || reference.StartsWith("refs/armada/missions/", StringComparison.Ordinal)) return;
            throw new InvalidOperationException("ref_delete_unmanaged: platform cleanup refuses " + reference);
        }
    }
}
