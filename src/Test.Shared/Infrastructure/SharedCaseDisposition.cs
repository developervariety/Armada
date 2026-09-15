namespace Test.Shared.Infrastructure
{
    using System;

    /// <summary>
    /// One recorded reason a shared case does not execute in the shared runner, with the legacy case that
    /// owns the behaviour when there is one.
    /// </summary>
    public sealed class SharedCaseDisposition
    {
        #region Public-Members

        /// <summary>
        /// Shared test identity, <c>SuiteId.CaseId</c>.
        /// </summary>
        public string TestId { get; }

        /// <summary>
        /// Why the case does not execute.
        /// </summary>
        public SharedCaseDispositionKindEnum Kind { get; }

        /// <summary>
        /// Repository-relative source file of the owning legacy case, or null when no legacy case owns it.
        /// </summary>
        public string? OwnerFile { get; }

        /// <summary>
        /// Registered name of the owning legacy case, or null when no legacy case owns it.
        /// </summary>
        public string? OwnerCase { get; }

        /// <summary>
        /// The contract difference that makes this disposition correct.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// The skip reason every runner reports for the case.
        /// </summary>
        public string SkipReason
        {
            get
            {
                if (Kind == SharedCaseDispositionKindEnum.DuplicateOfLegacyCase)
                    return "Duplicate of executed legacy case '" + OwnerCase + "' (" + OwnerFile + "): " + Reason;
                if (Kind == SharedCaseDispositionKindEnum.IntentionalForkDifference)
                    return "Intentional fork difference: " + Reason;
                return "Awaiting owner decision: " + Reason;
            }
        }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Record a case the fork does not implement, pending an owner decision.
        /// </summary>
        /// <param name="testId">Shared test identity, <c>SuiteId.CaseId</c>.</param>
        /// <param name="reason">The behaviour the fork does not implement.</param>
        /// <returns>The disposition.</returns>
        public static SharedCaseDisposition AwaitingOwner(string testId, string reason)
        {
            return new SharedCaseDisposition(testId, SharedCaseDispositionKindEnum.AwaitingOwnerDecision, null, null, reason);
        }

        /// <summary>
        /// Record a case that asserts behaviour the fork deliberately keeps different.
        /// </summary>
        /// <param name="testId">Shared test identity, <c>SuiteId.CaseId</c>.</param>
        /// <param name="reason">The fork behaviour and why the fork keeps it.</param>
        /// <returns>The disposition.</returns>
        public static SharedCaseDisposition ForkDifference(string testId, string reason)
        {
            return new SharedCaseDisposition(testId, SharedCaseDispositionKindEnum.IntentionalForkDifference, null, null, reason);
        }

        /// <summary>
        /// Record a case duplicated by an executed legacy case that owns the current contract.
        /// </summary>
        /// <param name="testId">Shared test identity, <c>SuiteId.CaseId</c>.</param>
        /// <param name="ownerFile">Repository-relative source file of the legacy case.</param>
        /// <param name="ownerCase">Registered name of the legacy case.</param>
        /// <param name="reason">The contract change the legacy case carries and this copy lacks.</param>
        /// <returns>The disposition.</returns>
        public static SharedCaseDisposition DuplicateOf(string testId, string ownerFile, string ownerCase, string reason)
        {
            if (String.IsNullOrWhiteSpace(ownerFile)) throw new ArgumentNullException(nameof(ownerFile));
            if (String.IsNullOrWhiteSpace(ownerCase)) throw new ArgumentNullException(nameof(ownerCase));
            return new SharedCaseDisposition(testId, SharedCaseDispositionKindEnum.DuplicateOfLegacyCase, ownerFile, ownerCase, reason);
        }

        private SharedCaseDisposition(string testId, SharedCaseDispositionKindEnum kind, string? ownerFile, string? ownerCase, string reason)
        {
            if (String.IsNullOrWhiteSpace(testId)) throw new ArgumentNullException(nameof(testId));
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));
            TestId = testId;
            Kind = kind;
            OwnerFile = ownerFile;
            OwnerCase = ownerCase;
            Reason = reason;
        }

        #endregion
    }
}
