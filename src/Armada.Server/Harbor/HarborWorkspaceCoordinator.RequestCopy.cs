namespace Armada.Server.Harbor
{
    using Armada.Core.Harbor;

    public sealed partial class HarborWorkspaceCoordinator
    {
        private static HarborGitOperationRequest? CopyGitRequest(HarborGitOperationRequest? request)
        {
            if (request == null) return null;
            return new HarborGitOperationRequest
            {
                Operation = request.Operation, UseRepository = request.UseRepository, UseWorkingDirectory = request.UseWorkingDirectory, TargetCheckoutId = request.TargetCheckoutId,
                BranchName = request.BranchName, BaseBranch = request.BaseBranch, Detached = request.Detached,
                RemoteName = request.RemoteName, Title = request.Title, Body = request.Body, Reason = request.Reason,
                SourceRef = request.SourceRef, DestinationRef = request.DestinationRef, CommitSha = request.CommitSha,
                ExpectedOldSha = request.ExpectedOldSha, TargetBranch = request.TargetBranch,
                CommitMessage = request.CommitMessage, PullRequestUrl = request.PullRequestUrl, Revision = request.Revision,
                RelativePath = request.RelativePath, Term = request.Term, MaxCount = request.MaxCount,
                MaxSamples = request.MaxSamples, Line = request.Line, MaxLines = request.MaxLines,
                AncestorRef = request.AncestorRef, DescendantRef = request.DescendantRef,
                StartCommit = request.StartCommit, FromCommit = request.FromCommit, ToCommit = request.ToCommit,
                TimeoutMilliseconds = request.TimeoutMilliseconds, OutputLimitBytes = request.OutputLimitBytes,
                TargetRef = request.TargetRef, RefPrefix = request.RefPrefix, MissionId = request.MissionId
            };
        }

        private static HarborVerificationRequest? CopyVerification(HarborVerificationRequest? request)
        {
            if (request == null) return null;
            return new HarborVerificationRequest
            {
                CommandKey = request.CommandKey, ExpectedCommandSha256 = request.ExpectedCommandSha256,
                CheckId = request.CheckId, RelativeWorkingDirectory = request.RelativeWorkingDirectory,
                TimeoutSeconds = request.TimeoutSeconds,
                AcceptedExitCodes = request.AcceptedExitCodes == null
                    ? new System.Collections.Generic.List<int>()
                    : new System.Collections.Generic.List<int>(request.AcceptedExitCodes),
                OutputLimitBytes = request.OutputLimitBytes,
                Rerun = request.Rerun == null ? null : new HarborVerificationRerunRequest
                {
                    PriorOutputId = request.Rerun.PriorOutputId, PriorCheckId = request.Rerun.PriorCheckId,
                    FailingTestNames = request.Rerun.FailingTestNames == null
                        ? new System.Collections.Generic.List<string>()
                        : new System.Collections.Generic.List<string>(request.Rerun.FailingTestNames),
                    FailedTestLinePattern = request.Rerun.FailedTestLinePattern,
                    RerunCommandKey = request.Rerun.RerunCommandKey,
                    RerunCommandSha256 = request.Rerun.RerunCommandSha256
                }
            };
        }

        private static HarborVerificationOutputRequest? CopyVerificationOutput(HarborVerificationOutputRequest? request)
        {
            if (request == null) return null;
            return new HarborVerificationOutputRequest
            {
                OutputId = request.OutputId, CheckId = request.CheckId, StandardError = request.StandardError,
                Offset = request.Offset, MaxBytes = request.MaxBytes
            };
        }
    }
}
