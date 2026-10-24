namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>Plans a rerun from a complete retained test failure set.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private IsolatedRerunPlan BuildRerunPlan(
            HarborWorkspaceOperationRequest request,
            HarborVerificationRequest verification,
            HarborRepositoryGrant grant,
            string baseCommand,
            out Dictionary<string, string> rerunEnvironment)
        {
            HarborVerificationRerunRequest rerun = verification.Rerun!;
            rerunEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (String.IsNullOrWhiteSpace(rerun.PriorOutputId) || String.IsNullOrWhiteSpace(rerun.PriorCheckId)
                || rerun.FailingTestNames == null || rerun.FailingTestNames.Count < 1
                || rerun.FailingTestNames.Count > FailedTestNameExtractor.MaxNames
                || (rerun.FailedTestLinePattern?.Length ?? 0) > 2048
                || (rerun.RerunCommandKey == null) != (rerun.RerunCommandSha256 == null))
                throw Failure("workspace_verification_rerun_invalid", "The prior output reference or bounded failure selection is invalid.");
            foreach (string name in rerun.FailingTestNames)
                if (String.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                    throw Failure("workspace_verification_rerun_invalid", "A selected failure name is outside its allowed form.");
            string failedOutput = _VerificationOutputs.ReadForRerun(request, rerun.PriorOutputId, rerun.PriorCheckId, out int priorExitCode);
            Regex? pattern = null;
            if (!String.IsNullOrWhiteSpace(rerun.FailedTestLinePattern)
                && !FailedTestLinePattern.TryCreate(rerun.FailedTestLinePattern, FailedTestLinePattern.MatchTimeout, out pattern, out _))
                throw Failure("workspace_verification_rerun_pattern_invalid", "The failure-name pattern is invalid.");
            FailedTestNameExtractor.FailedTestNameSet observed = FailedTestNameExtractor.Extract(failedOutput, pattern);
            if (observed.Overflow || observed.PatternTimedOut || observed.Names.Count != rerun.FailingTestNames.Count)
                throw Failure("workspace_verification_rerun_selection_mismatch", "The selected failures do not match a complete retained failure set.");
            for (int index = 0; index < observed.Names.Count; index++)
                if (!String.Equals(observed.Names[index], rerun.FailingTestNames[index], StringComparison.Ordinal))
                    throw Failure("workspace_verification_rerun_selection_mismatch", "The selected failures do not match the retained failure set.");
            DefinitionOfDoneFailureClassEnum failureClass = new DefinitionOfDoneFailureClassifier().Classify("unit-test", priorExitCode, failedOutput);
            WorkflowProfile? profile = null;
            if (!String.IsNullOrWhiteSpace(rerun.RerunCommandKey))
            {
                if (String.IsNullOrWhiteSpace(rerun.RerunCommandSha256) || !grant.VerificationCommands.TryGetValue(rerun.RerunCommandKey, out string? rerunCommand))
                    throw Failure("workspace_verification_rerun_command_not_configured", "The profile rerun command key is not configured on this runner.");
                string rerunHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rerunCommand))).ToLowerInvariant();
                if (!String.Equals(rerunHash, rerun.RerunCommandSha256, StringComparison.OrdinalIgnoreCase))
                    throw Failure("workspace_verification_rerun_command_mismatch", "The configured profile rerun command differs from the server-approved command.");
                profile = new WorkflowProfile { Id = "harbor-rerun", Name = "Harbor isolated rerun", IsolatedTestRerunCommand = rerunCommand };
            }
            IsolatedRerunPlan plan = IsolatedRerunPlan.ForFailedRun(profile, baseCommand, failureClass,
                observed.Names, false);
            if (plan.Command == null || plan.FromProfile != (profile != null))
                throw Failure("workspace_verification_rerun_unsupported", "The shared rerun planner cannot safely build this rerun.");
            rerunEnvironment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal);
            return plan;
        }
    }
}
