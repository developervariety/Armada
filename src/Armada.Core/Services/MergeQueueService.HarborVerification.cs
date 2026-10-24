namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        private async Task RunHarborQueueVerificationAsync(MergeEntry entry, Dock integration, CancellationToken token)
        {
            string command = entry.TestCommand ?? _Settings.MergeQueueTestCommand ?? String.Empty;
            if (String.IsNullOrWhiteSpace(command)) return;
            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            string? head = await _Git.GetHeadCommitHashAsync(checkout, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) throw new InvalidOperationException("harbor_verification_checkout_head_unavailable");
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            string checkId = "mq-test-" + HarborQueueAttemptId(entry.Id, job.RetryCount).Substring("mq_".Length, 32);
            string commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant();
            HarborWorkspaceOperationRequest operation = HarborCheckoutService.CreateRequest(integration, HarborWorkspaceOperationEnum.RunVerification);
            operation.Verification = new HarborVerificationRequest
            {
                CommandKey = "merge-queue-test",
                ExpectedCommandSha256 = commandHash,
                CheckId = checkId,
                TimeoutSeconds = Math.Max(1, Math.Min(1800, _Settings.MergeQueueTestTimeoutSeconds)),
                OutputLimitBytes = _TestOutputLimitBytes
            };
            HarborCheckoutService checkouts = _HarborCheckouts
                ?? throw new InvalidOperationException("harbor_checkout_provider_unavailable");
            HarborWorkspaceOperationResult completed;
            HarborVerificationOutputText output;
            using (new HarborCheckoutOperationScope("mq-test:" + checkId + ":" + head, head!))
            {
                completed = await checkouts.ExecuteAsync(integration, operation, token).ConfigureAwait(false);
                output = await HarborVerificationOutputReader.ReadAsync(checkouts, integration, completed, checkId, head!, token).ConfigureAwait(false);
            }
            string text = output.StandardOutput + output.StandardError;
            if (completed.TimedOut || completed.Cancelled || !completed.KillConfirmed || completed.ExitCode != 0)
            {
                string reason = "Harbor merge queue verification failed (exit " + (completed.ExitCode?.ToString() ?? "unknown") + ")";
                ApplyClassification(entry, new MergeFailureContext
                {
                    TestExitCode = completed.ExitCode ?? -1,
                    TestOutput = text,
                    ConflictedFiles = new System.Collections.Generic.List<string>()
                });
                entry.TestExitCode = completed.ExitCode;
                entry.TestOutput = TruncateOutput(reason + Environment.NewLine + text);
                await TransitionEntryToFailureAsync(entry, entry.TestOutput, token).ConfigureAwait(false);
                await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
            }
            else
            {
                entry.TestExitCode = completed.ExitCode;
                entry.TestOutput = TruncateOutput(text);
                entry.LastUpdateUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            }
        }
    }
}
