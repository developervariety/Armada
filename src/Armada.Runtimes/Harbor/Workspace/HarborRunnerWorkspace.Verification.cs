namespace Armada.Runtimes.Harbor
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
    using Armada.Core.Services;

    /// <summary>Runs a bounded configured verification and stores its output.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task RunVerificationAsync(
            HarborWorkspaceOperationRequest request,
            HarborCheckoutIntent intent,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            CancellationToken token)
        {
            HarborVerificationRequest? verification = request.Verification;
            if (verification == null || String.IsNullOrWhiteSpace(verification.CommandKey)
                || String.IsNullOrWhiteSpace(verification.CheckId) || verification.CheckId.Length > 200
                || verification.CheckId.Contains("\r", StringComparison.Ordinal) || verification.CheckId.Contains("\n", StringComparison.Ordinal))
                throw Failure("workspace_verification_invalid", "The verification identity or command key is invalid.");
            if (!grant.VerificationCommands.TryGetValue(verification.CommandKey, out string? command))
                throw Failure("workspace_verification_not_configured", "The command key is not configured on this runner.");
            string actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant();
            if (!String.Equals(actualHash, verification.ExpectedCommandSha256, StringComparison.OrdinalIgnoreCase))
                throw Failure("workspace_verification_command_mismatch", "The configured verification command differs from the server-approved command.");
            if (verification.TimeoutSeconds < 1 || verification.TimeoutSeconds > MaximumVerificationSeconds
                || verification.OutputLimitBytes < 1 || verification.OutputLimitBytes > 16 * 1024 * 1024)
                throw Failure("workspace_verification_limits_invalid", "The verification time or output limit is outside the allowed range.");
            if (verification.AcceptedExitCodes == null || verification.AcceptedExitCodes.Count < 1 || verification.AcceptedExitCodes.Count > 16)
                throw Failure("workspace_verification_policy_invalid", "The verification exit policy is invalid.");
            string checkout = CheckoutPath(grant, intent.Binding, requireExists: true);
            await CheckExpectedHeadAsync(request, checkout, token).ConfigureAwait(false);
            string workingDirectory = String.IsNullOrWhiteSpace(verification.RelativeWorkingDirectory)
                ? checkout
                : ResolveRelativePath(verification.RelativeWorkingDirectory, checkout, true);
            if (!System.IO.Directory.Exists(workingDirectory))
                throw Failure("workspace_verification_directory_invalid", "The verification directory is not a checkout directory.");
            _VerificationOutputs.Reserve(request, verification.OutputLimitBytes);
            Dictionary<string, string> rerunEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (verification.Rerun != null)
            {
                IsolatedRerunPlan rerun = BuildRerunPlan(request, verification, grant, command, out rerunEnvironment);
                if (rerun.Command == null)
                    throw Failure("workspace_verification_rerun_unsupported", "The shared rerun planner cannot safely build this rerun.");
                command = rerun.Command;
            }
            HarborWorkspaceProcessResult execution = await RunVerificationProcessAsync(workingDirectory, command,
                TimeSpan.FromSeconds(verification.TimeoutSeconds), verification.OutputLimitBytes, rerunEnvironment, token).ConfigureAwait(false);
            HarborWorkspaceOperationResult stored = _VerificationOutputs.Complete(request, execution, intent.Binding);
            result.ExitCode = stored.ExitCode;
            result.TimedOut = stored.TimedOut;
            result.Cancelled = stored.Cancelled;
            result.KillConfirmed = stored.KillConfirmed;
            result.DurationMilliseconds = stored.DurationMilliseconds;
            result.StartedUtc = stored.StartedUtc;
            result.Truncated = stored.Truncated;
            result.VerificationOutputId = stored.VerificationOutputId;
            result.VerificationCheckId = stored.VerificationCheckId;
            result.StandardOutputLength = stored.StandardOutputLength;
            result.StandardOutputRetainedBytes = stored.StandardOutputRetainedBytes;
            result.StandardOutputOmittedBytes = stored.StandardOutputOmittedBytes;
            result.StandardOutputSha256 = stored.StandardOutputSha256;
            result.StandardErrorLength = stored.StandardErrorLength;
            result.StandardErrorRetainedBytes = stored.StandardErrorRetainedBytes;
            result.StandardErrorOmittedBytes = stored.StandardErrorOmittedBytes;
            result.StandardErrorSha256 = stored.StandardErrorSha256;
            result.Reason = stored.Reason;
            result.Binding = intent.Binding;
            result.HeadCommit = await ReadHeadAsync(checkout, token).ConfigureAwait(false);
            result.BranchName = await ReadBranchAsync(checkout, token).ConfigureAwait(false);
            result.CanonicalWorkingDirectory = workingDirectory;
            result.Accepted = true;
        }

    }
}
