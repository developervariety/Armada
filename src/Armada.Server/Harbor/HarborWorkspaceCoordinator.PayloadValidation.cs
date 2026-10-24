namespace Armada.Server.Harbor
{
    using System;
    using Armada.Core.Harbor;

    public sealed partial class HarborWorkspaceCoordinator
    {
        private static bool ValidGit(HarborGitOperationRequest request)
        {
            return !(request.UseRepository && request.UseWorkingDirectory)
                && Enum.IsDefined(typeof(HarborGitOperationEnum), request.Operation)
                && request.MaxCount >= 1 && request.MaxCount <= 1000
                && request.MaxSamples >= 1 && request.MaxSamples <= 100
                && request.Line >= 1 && request.Line <= 100000000
                && request.MaxLines >= 1 && request.MaxLines <= 2000
                && request.TimeoutMilliseconds >= 1 && request.TimeoutMilliseconds <= 120000
                && request.OutputLimitBytes >= 1 && request.OutputLimitBytes <= 4 * 1024 * 1024;
        }

        private static bool ValidVerification(HarborVerificationRequest request)
        {
            return !String.IsNullOrWhiteSpace(request.CommandKey) && request.CommandKey.Length <= 200
                && Fingerprint(request.ExpectedCommandSha256)
                && !String.IsNullOrWhiteSpace(request.CheckId) && request.CheckId.Length <= 200
                && request.TimeoutSeconds >= 1 && request.TimeoutSeconds <= 3600
                && request.OutputLimitBytes >= 1 && request.OutputLimitBytes <= 16 * 1024 * 1024
                && request.AcceptedExitCodes != null && request.AcceptedExitCodes.Count >= 1 && request.AcceptedExitCodes.Count <= 16
                && (request.Rerun == null || ValidVerificationRerun(request.Rerun));
        }

        private static bool ValidVerificationRerun(HarborVerificationRerunRequest request)
        {
            if (!SafeIdentifier(request.PriorOutputId) || !SafeIdentifier(request.PriorCheckId)
                || request.FailingTestNames == null || request.FailingTestNames.Count < 1
                || request.FailingTestNames.Count > 200 || (request.FailedTestLinePattern?.Length ?? 0) > 2048)
                return false;
            foreach (string name in request.FailingTestNames)
                if (String.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.Contains('\r') || name.Contains('\n')) return false;
            if (request.RerunCommandKey == null) return request.RerunCommandSha256 == null;
            return SafeIdentifier(request.RerunCommandKey) && Fingerprint(request.RerunCommandSha256);
        }

        private static bool ValidBase64(string value)
        {
            if (value.Length > 3 * 1024 * 1024) return false;
            byte[] bytes = new byte[2 * 1024 * 1024];
            return Convert.TryFromBase64String(value, bytes, out int written) && written <= bytes.Length;
        }

        private static bool Fingerprint(string? value) => value != null && value.Length == 64
            && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A[0-9a-fA-F]{64}\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static bool FullCommit(string? value) => value != null
            && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
