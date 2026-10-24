namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Validated, versioned Harbor checkout lifecycle storage and compare-and-set writes.</summary>
    internal static partial class HarborCheckoutLifecyclePersistence
    {
        private static bool ValidTransition(HarborCheckoutLifecycleIntent expected, HarborCheckoutLifecycleIntent next)
        {
            if (expected.Phase == HarborCheckoutLifecyclePhaseEnum.Terminal
                && expected.RemovalConfirmed
                && expected.Purpose == HarborCheckoutLifecyclePurposeEnum.Reclaim
                && next.CheckoutAlreadyRemoved
                && next.Purpose != HarborCheckoutLifecyclePurposeEnum.Reclaim
                && next.IntentId != expected.IntentId
                && next.BindingRevision == expected.BindingRevision
                && next.DockId == expected.DockId
                && next.MissionId == expected.MissionId
                && next.BranchName == expected.BranchName
                && next.InitialHead == expected.AnchoredHead
                && next.ExpectedHead == expected.AnchoredHead
                && next.Phase == HarborCheckoutLifecyclePhaseEnum.Prepared
                && !next.PreserveSnapshot && !next.PreserveReferences && !next.PreserveBranch
                && next.RemovalConfirmed == false
                && next.SnapshotCommitSha == expected.SnapshotCommitSha
                && next.SnapshotRefName == expected.SnapshotRefName
                && next.AnchoredHead == expected.AnchoredHead
                && next.DockAnchorRef == expected.DockAnchorRef
                && next.MissionAnchorRef == expected.MissionAnchorRef
                && next.BranchAnchorRef == expected.BranchAnchorRef)
                return true;
            return Valid(expected)
                && expected.IntentId == next.IntentId
                && expected.BindingRevision == next.BindingRevision
                && expected.DockId == next.DockId
                && expected.MissionId == next.MissionId
                && expected.BranchName == next.BranchName
                && expected.Purpose == next.Purpose
                && expected.InitialHead == next.InitialHead
                && expected.PreserveSnapshot == next.PreserveSnapshot
                && expected.PreserveReferences == next.PreserveReferences
                && expected.PreserveBranch == next.PreserveBranch
                && expected.DeleteLocalBranch == next.DeleteLocalBranch
                && expected.DeleteRemoteBranch == next.DeleteRemoteBranch
                && expected.CheckoutAlreadyRemoved == next.CheckoutAlreadyRemoved
                && next.Phase >= expected.Phase
                && next.Phase <= expected.Phase + 1
                && (expected.SnapshotCommitSha == null || expected.SnapshotCommitSha == next.SnapshotCommitSha)
                && (expected.SnapshotRefName == null || expected.SnapshotRefName == next.SnapshotRefName)
                && (expected.AnchoredHead == null || expected.AnchoredHead == next.AnchoredHead)
                && (expected.DockAnchorRef == null || expected.DockAnchorRef == next.DockAnchorRef)
                && (expected.MissionAnchorRef == null || expected.MissionAnchorRef == next.MissionAnchorRef)
                && (expected.BranchAnchorRef == null || expected.BranchAnchorRef == next.BranchAnchorRef)
                && (!expected.RemovalConfirmed || next.RemovalConfirmed)
                && (String.Equals(next.ExpectedHead, expected.ExpectedHead, StringComparison.OrdinalIgnoreCase)
                    || (expected.Phase == HarborCheckoutLifecyclePhaseEnum.Prepared
                        && next.Phase == HarborCheckoutLifecyclePhaseEnum.SnapshotComplete
                        && String.Equals(next.ExpectedHead, next.SnapshotCommitSha ?? expected.ExpectedHead, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(expected.ExpectedHead, expected.InitialHead, StringComparison.OrdinalIgnoreCase)));
        }

    }
}
