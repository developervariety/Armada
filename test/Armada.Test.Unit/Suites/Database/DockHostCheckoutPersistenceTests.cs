namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    /// <summary>Persistence tests for a Harbor-owned dock checkout identity.</summary>
    public sealed partial class DockHostCheckoutPersistenceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Dock Harbor Checkout Persistence";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await Binding_RoundTripsAcrossDriverReopen().ConfigureAwait(false);
            await LifecycleIntent_UsesExactCompareAndSetAndSurvivesGenericUpdate().ConfigureAwait(false);
            await TerminalReclaim_CanStartPostRemovalBranchCleanupByCas().ConfigureAwait(false);
            await LegacyNullBinding_PreservesLocalPath().ConfigureAwait(false);
            await MalformedNonNullBinding_RefusesDockReadDespiteLocalPath().ConfigureAwait(false);
        }

        private static HarborCheckoutLifecycleIntent CopyLifecycleForTest(HarborCheckoutLifecycleIntent source,
            HarborCheckoutLifecyclePhaseEnum phase)
        {
            return new HarborCheckoutLifecycleIntent
            {
                IntentId = source.IntentId,
                BindingRevision = source.BindingRevision,
                DockId = source.DockId,
                MissionId = source.MissionId,
                BranchName = source.BranchName,
                Purpose = source.Purpose,
                InitialHead = source.InitialHead,
                ExpectedHead = source.ExpectedHead,
                SnapshotCommitSha = source.SnapshotCommitSha,
                SnapshotRefName = source.SnapshotRefName,
                AnchoredHead = source.AnchoredHead,
                DockAnchorRef = source.DockAnchorRef,
                MissionAnchorRef = source.MissionAnchorRef,
                BranchAnchorRef = source.BranchAnchorRef,
                PreserveSnapshot = source.PreserveSnapshot,
                PreserveReferences = source.PreserveReferences,
                PreserveBranch = source.PreserveBranch,
                DeleteLocalBranch = source.DeleteLocalBranch,
                DeleteRemoteBranch = source.DeleteRemoteBranch,
                Phase = phase,
                RemovalConfirmed = source.RemovalConfirmed,
                CheckoutAlreadyRemoved = source.CheckoutAlreadyRemoved
            };
        }

        private static HarborCheckoutBinding CompleteBinding(string tenantId, string userId) => new HarborCheckoutBinding
        {
            RunnerId = "runner_owned_checkout",
            EnrollmentGeneration = 7,
            TenantId = tenantId,
            UserId = userId,
            RepositoryGrantId = "grant_owned_checkout",
            RepositoryFingerprint = new String('a', 64),
            CheckoutId = "hco_0123456789abcdef0123456789abcdef",
            BaseCommit = new String('b', 40),
            BranchName = "harbor/mission-branch",
            Revision = "rev_opaque_456"
        };
    }
}
