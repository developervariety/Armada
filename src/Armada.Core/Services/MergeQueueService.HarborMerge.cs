namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        private async Task MergeHarborIntegrationAsync(MergeEntry entry, Dock integration, CancellationToken token)
        {
            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            entry.TestOutput = null;
            string repository = checkout + "/repository";
            string? source = await _Git.GetRevisionCommitShaAsync(repository, "refs/heads/" + entry.BranchName, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(source))
                source = await _Git.GetRevisionCommitShaAsync(repository, "refs/remotes/origin/" + entry.BranchName, token).ConfigureAwait(false);
            string? head = await _Git.GetHeadCommitHashAsync(checkout, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(source) || String.IsNullOrWhiteSpace(head))
            {
                await TransitionEntryToFailureAsync(entry, "harbor_merge_ref_unavailable: " + entry.BranchName, token).ConfigureAwait(false);
                await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
                return;
            }
            if (await _Git.TryIsAncestorAsync(checkout, source!, "HEAD", token).ConfigureAwait(false) == true) return;
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            string operation = "mq-merge:" + HarborQueueAttemptId(entry.Id, job.RetryCount) + ":" + head;
            try
            {
                Vessel? vessel = await _Database.Vessels.ReadAsync(entry.VesselId, token).ConfigureAwait(false);
                if (vessel == null) _Logging.Warn(_Header + "Markdown resolution vessel metadata is missing for " + entry.Id);
                List<string> resolvedMarkdown = new List<string>();
                using (new HarborCheckoutOperationScope(operation, head!))
                    await _Git.MergeBranchLocalWithPolicyAsync(checkout, repository, entry.BranchName, null, null,
                        vessel?.AutoResolveAddOnlyMarkdown ?? false, resolvedMarkdown.Add, token).ConfigureAwait(false);
                string resolution = await LandingMarkdownResolution.RecordAsync(_Database, _Settings,
                    entry.MissionId, entry.VesselId, entry.Id, resolvedMarkdown, token, message => _Logging.Warn(_Header + message)).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(resolution))
                {
                    entry.TestOutput = CombineTestOutput(resolution, entry.TestOutput ?? String.Empty);
                    entry.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && token.IsCancellationRequested))
            {
                await TransitionEntryToFailureAsync(entry, "harbor_merge_failed: " + exception.Message, token).ConfigureAwait(false);
                await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
            }
        }

        private async Task TestHarborIntegrationAsync(MergeEntry entry, Dock integration,
            HarborQueueContext context, CancellationToken token)
        {
            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            LandingEvidence evidence = await _LandingEvidence.CollectAsync(entry.VesselId, entry.TenantId,
                checkout, entry.TargetBranch, token).ConfigureAwait(false);
            if (!evidence.Available)
            {
                await TransitionEntryToFailureAsync(entry, evidence.FormatRefusal(), token, fireRecovery: false).ConfigureAwait(false);
                await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
                return;
            }
            DockBoundaryScanResult boundary = await ScanDockBoundaryAsync(entry, evidence, token).ConfigureAwait(false);
            if (!boundary.Passed)
            {
                string reason = FormatDockBoundaryFailureReason(boundary, evidence.Vessel?.Name ?? entry.VesselId ?? "unknown", !String.IsNullOrEmpty(entry.MissionId));
                await TransitionEntryToFailureAsync(entry, reason, token, fireRecovery: false).ConfigureAwait(false);
                await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
                return;
            }
            if (_IntegrationValidator != null)
            {
                IntegrationValidationResult validation = await ValidateHarborQueueCandidateAsync(entry, integration, context, token).ConfigureAwait(false);
                if (!validation.AllowsPublication)
                {
                    bool recover = validation.Outcome == IntegrationValidationOutcomeEnum.Failed;
                    await TransitionEntryToFailureAsync(entry, TruncateOutput(validation.Reason), token, fireRecovery: recover).ConfigureAwait(false);
                    await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                _Logging.Warn(_Header + "no integration validator is wired; " + entry.Id + " is measured only by the configured verification command");
            }
            await RunHarborQueueVerificationAsync(entry, integration, token).ConfigureAwait(false);
        }

        private async Task<IntegrationValidationResult> ValidateHarborQueueCandidateAsync(MergeEntry entry,
            Dock integration, HarborQueueContext context, CancellationToken token)
        {
            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            string? integrationCommit = await _Git.GetHeadCommitHashAsync(checkout, token).ConfigureAwait(false);
            string? sourceCommit = await _Git.GetRevisionCommitShaAsync(checkout + "/repository",
                "refs/heads/" + entry.BranchName, token).ConfigureAwait(false);
            string? baseCommit = integration.HostCheckout?.BaseCommit;
            if (String.IsNullOrWhiteSpace(integrationCommit) || String.IsNullOrWhiteSpace(sourceCommit) || String.IsNullOrWhiteSpace(baseCommit))
                return new IntegrationValidationResult { Outcome = IntegrationValidationOutcomeEnum.Unavailable, Reason = "harbor_integration_evidence_unavailable" };
            IntegrationCandidate candidate = new IntegrationCandidate
            {
                MissionId = entry.MissionId,
                MergeEntryId = entry.Id,
                LandingPath = "MergeQueue",
                TargetBranch = entry.TargetBranch,
                SourceBranch = entry.BranchName,
                SourceCommit = sourceCommit,
                TargetBaseCommit = baseCommit,
                IntegrationCommit = integrationCommit,
                TenantId = entry.TenantId,
                UserId = entry.UserId
            };
            return await _IntegrationValidator!.ValidateAsync(context.Vessel, candidate, token).ConfigureAwait(false);
        }
    }
}
