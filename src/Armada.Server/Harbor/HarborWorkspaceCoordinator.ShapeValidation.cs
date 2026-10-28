namespace Armada.Server.Harbor
{
    using System;
    using Armada.Core.Harbor;

    public sealed partial class HarborWorkspaceCoordinator
    {
        private static bool RequestShapeIsValid(HarborWorkspaceOperationRequest request)
        {
            if (request.MaxBytes < 1 || request.MaxBytes > 2 * 1024 * 1024 || request.MaxEntries < 1 || request.MaxEntries > 10000
                || request.TimeoutSeconds < 1 || request.TimeoutSeconds > 3600) return false;
            if (!SafeIdentifier(request.RunnerId) || !SafeIdentifier(request.VesselId) || !SafeIdentifier(request.RepositoryGrantId)
                || !SafeIdentifier(request.DockId, allowEmpty: true) || !SafeIdentifier(request.OperationKey, allowEmpty: true)
                || !SafeIdentifier(request.RequestedRevision, allowEmpty: true)) return false;
            if (request.Text != null && (request.Text.Length > 2 * 1024 * 1024 || System.Text.Encoding.UTF8.GetByteCount(request.Text) > 2 * 1024 * 1024)) return false;
            if (request.BytesBase64 != null && !ValidBase64(request.BytesBase64)) return false;
            if (request.RelativePath != null && (request.RelativePath.Length > 4096 || request.RelativePath.IndexOf('\0') >= 0)) return false;
            if (request.ExpectedHead != null && !FullCommit(request.ExpectedHead)) return false;
            if (request.ExpectedRepositoryFingerprint != null && !Fingerprint(request.ExpectedRepositoryFingerprint)) return false;
            if (request.Git != null && request.Operation != HarborWorkspaceOperationEnum.Git) return false;
            if (request.Verification != null && request.Operation != HarborWorkspaceOperationEnum.RunVerification) return false;
            if (request.VerificationOutput != null && request.Operation != HarborWorkspaceOperationEnum.ReadVerificationOutput) return false;
            if ((request.Operation == HarborWorkspaceOperationEnum.Git) != (request.Git != null)) return false;
            if ((request.Operation == HarborWorkspaceOperationEnum.RunVerification) != (request.Verification != null)) return false;
            if (request.Text != null && request.Operation != HarborWorkspaceOperationEnum.WriteText) return false;
            if (request.InventoryCommand != null && request.Operation != HarborWorkspaceOperationEnum.ProbeCodexInventory) return false;
            if ((request.Operation == HarborWorkspaceOperationEnum.WriteText) != (request.Text != null)) return false;
            if (request.BytesBase64 != null && request.Operation != HarborWorkspaceOperationEnum.WriteBytes) return false;
            if ((request.Operation == HarborWorkspaceOperationEnum.WriteBytes) != (request.BytesBase64 != null)) return false;

            switch (request.Operation)
            {
                case HarborWorkspaceOperationEnum.InspectRepository:
                    return request.Binding == null && String.IsNullOrWhiteSpace(request.DockId)
                        && request.ExpectedHead == null && request.RelativePath == null && request.Text == null && request.BytesBase64 == null
                        && request.Git == null && request.Verification == null && request.OperationKey == null;
                case HarborWorkspaceOperationEnum.ProvisionCheckout:
                    return request.Binding != null && FullCommit(request.ExpectedHead)
                        && String.Equals(request.ExpectedHead, request.Binding.BaseCommit, StringComparison.OrdinalIgnoreCase)
                        && NoFileOrNestedPayload(request);
                case HarborWorkspaceOperationEnum.InspectCheckout:
                    return request.Binding != null && request.ExpectedHead == null && NoFileOrNestedPayload(request) && request.OperationKey == null
                        && request.RequestedRevision == null;
                case HarborWorkspaceOperationEnum.Git:
                    return request.Binding != null && ValidExpectedHead(request) && ValidGit(request.Git!)
                        && request.RelativePath == null && request.Text == null && request.BytesBase64 == null
                        && request.Verification == null && request.RequestedRevision == null;
                case HarborWorkspaceOperationEnum.Stat:
                case HarborWorkspaceOperationEnum.ReadText:
                case HarborWorkspaceOperationEnum.WriteText:
                case HarborWorkspaceOperationEnum.ReadBytes:
                case HarborWorkspaceOperationEnum.WriteBytes:
                case HarborWorkspaceOperationEnum.EnumerateFiles:
                case HarborWorkspaceOperationEnum.DeleteFile:
                case HarborWorkspaceOperationEnum.AddGitExclude:
                    return request.Binding != null && ValidExpectedHead(request) && !String.IsNullOrWhiteSpace(request.RelativePath)
                        && request.Git == null && request.Verification == null && request.RequestedRevision == null;
                case HarborWorkspaceOperationEnum.RemoveCheckout:
                    return request.Binding != null && FullCommit(request.ExpectedHead) && request.RelativePath == null
                        && request.Text == null && request.BytesBase64 == null && request.Git == null && request.Verification == null
                        && request.RequestedRevision == null;
                case HarborWorkspaceOperationEnum.RunVerification:
                    return request.Binding != null && FullCommit(request.ExpectedHead) && ValidVerification(request.Verification!)
                        && request.RelativePath == null && request.Text == null && request.BytesBase64 == null
                        && request.Git == null && request.RequestedRevision == null;
                case HarborWorkspaceOperationEnum.ProbeCodexInventory:
                    return request.Binding == null && String.IsNullOrWhiteSpace(request.DockId)
                        && request.ExpectedHead == null && request.RelativePath == null && request.Text == null
                        && request.BytesBase64 == null && request.Git == null && request.Verification == null
                        && request.VerificationOutput == null && request.OperationKey == null
                        && request.RequestedRevision == null
                        && CodexInventoryCommand.TryParse(request.InventoryCommand, out _);
                case HarborWorkspaceOperationEnum.ReadVerificationOutput:
                    return request.Binding != null && FullCommit(request.ExpectedHead)
                        && request.VerificationOutput != null
                        && SafeIdentifier(request.VerificationOutput.OutputId) && SafeIdentifier(request.VerificationOutput.CheckId)
                        && request.VerificationOutput.Offset >= 0 && request.VerificationOutput.MaxBytes >= 1
                        && request.VerificationOutput.MaxBytes <= 1024 * 1024
                        && request.RelativePath == null && request.Text == null && request.BytesBase64 == null
                        && request.Git == null && request.Verification == null && request.OperationKey == null
                        && request.RequestedRevision == null;
                default:
                    return false;
            }
        }

        private static bool ValidExpectedHead(HarborWorkspaceOperationRequest request)
        {
            return HarborWorkspaceOperationRules.IsMutation(request) ? FullCommit(request.ExpectedHead)
                : request.ExpectedHead == null || FullCommit(request.ExpectedHead);
        }

        private static bool NoFileOrNestedPayload(HarborWorkspaceOperationRequest request)
        {
            return request.RelativePath == null && request.Text == null && request.BytesBase64 == null
                && request.Git == null && request.Verification == null;
        }

        private static bool SafeIdentifier(string? value, bool allowEmpty = false)
        {
            if (String.IsNullOrWhiteSpace(value)) return allowEmpty && String.IsNullOrEmpty(value);
            return value.Length <= 900 && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
        }

    }
}
