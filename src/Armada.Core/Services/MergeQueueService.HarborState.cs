namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        private async Task<bool> AdvanceHarborLandingStateMachineOneStepAsync(MergeEntry entry,
            HarborQueueContext context, CancellationToken token)
        {
            if (context.Vessel.GeneratedArtifacts != null)
            {
                await TransitionEntryToFailureAsync(entry, "generated_artifacts_harbor_unsupported", token,
                    fireRecovery: false).ConfigureAwait(false);
                return false;
            }
            if (entry.Status == MergeStatusEnum.Queued || entry.Status == MergeStatusEnum.Rebasing)
            {
                await PersistStatusAsync(entry, MergeStatusEnum.Rebasing, token).ConfigureAwait(false);
                try
                {
                    await PrepareHarborIntegrationAsync(entry, context, token).ConfigureAwait(false);
                }
                catch (InvalidOperationException exception) when (exception.Message.StartsWith("target branch drift:", StringComparison.Ordinal))
                {
                    bool rebuilt = await RebuildStaleHarborCandidateAsync(entry, context, exception.Message, token).ConfigureAwait(false);
                    if (!rebuilt) return false;
                    return true;
                }
                await PersistStatusAsync(entry, MergeStatusEnum.Merging, token).ConfigureAwait(false);
                return true;
            }

            Dock? integration = await ReadHarborIntegrationAsync(entry, token).ConfigureAwait(false);
            if (integration == null)
            {
                await PersistStatusAsync(entry, MergeStatusEnum.Rebasing, token).ConfigureAwait(false);
                return true;
            }

            if (entry.Status == MergeStatusEnum.Merging)
            {
                await MergeHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
                if (entry.Status == MergeStatusEnum.Failed || entry.Status == MergeStatusEnum.Cancelled) return false;
                await PersistStatusAsync(entry, MergeStatusEnum.Testing, token, startedTests: true).ConfigureAwait(false);
                return true;
            }

            if (entry.Status == MergeStatusEnum.Testing)
            {
                await TestHarborIntegrationAsync(entry, integration, context, token).ConfigureAwait(false);
                if (entry.Status == MergeStatusEnum.Failed) return false;
                await PersistStatusAsync(entry, MergeStatusEnum.Passed, token).ConfigureAwait(false);
                return true;
            }

            if (entry.Status == MergeStatusEnum.Passed)
            {
                MergeStatusEnum next = _PullRequestServiceFactory != null && !String.IsNullOrEmpty(entry.AuditCriticalTrigger)
                    ? MergeStatusEnum.CreatingPR : MergeStatusEnum.Pushing;
                await PersistStatusAsync(entry, next, token).ConfigureAwait(false);
                return true;
            }

            if (entry.Status == MergeStatusEnum.Pushing)
            {
                string? stale = await DescribeHarborUnpublishableCandidateAsync(entry, integration, context, token).ConfigureAwait(false);
                if (stale != null) return await RebuildStaleHarborCandidateAsync(entry, context, stale, token).ConfigureAwait(false);
                await LandHarborEntryAsync(entry, integration, context, token).ConfigureAwait(false);
                if (entry.Status == MergeStatusEnum.Landed)
                    await CleanupHarborBranchesAsync(entry, integration, context.Vessel, token).ConfigureAwait(false);
                await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
                return true;
            }

            if (entry.Status == MergeStatusEnum.CreatingPR)
            {
                return await OpenHarborPullRequestAsync(entry, integration, token).ConfigureAwait(false);
            }

            return false;
        }
    }
}
