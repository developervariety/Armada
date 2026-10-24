namespace Armada.Runtimes.Harbor
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Dispatches the typed workspace operations supported by this runner.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task ExecuteOperationAsync(
            HarborWorkspaceOperationRequest request,
            HarborHandshakeAck ack,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            CancellationToken token)
        {
            switch (request.Operation)
            {
                case HarborWorkspaceOperationEnum.InspectRepository:
                    if (request.Binding != null) throw Failure("workspace_binding_unexpected", "Repository inspection does not accept a checkout binding.");
                    await InspectRepositoryAsync(request, grant, result, token).ConfigureAwait(false);
                    break;
                case HarborWorkspaceOperationEnum.ProvisionCheckout:
                    HarborCheckoutBinding provisionBinding = RequireBinding(request, ack, grant);
                    await ProvisionCheckoutAsync(request, provisionBinding, grant, result, token).ConfigureAwait(false);
                    break;
                case HarborWorkspaceOperationEnum.InspectCheckout:
                    HarborCheckoutBinding inspectBinding = RequireBinding(request, ack, grant);
                    HarborCheckoutIntent inspectIntent = ReadReadyIntent(request, inspectBinding, grant);
                    await InspectCheckoutAsync(inspectIntent, grant, result, token).ConfigureAwait(false);
                    break;
                case HarborWorkspaceOperationEnum.Stat:
                case HarborWorkspaceOperationEnum.ReadText:
                case HarborWorkspaceOperationEnum.ReadBytes:
                case HarborWorkspaceOperationEnum.WriteText:
                case HarborWorkspaceOperationEnum.WriteBytes:
                case HarborWorkspaceOperationEnum.EnumerateFiles:
                case HarborWorkspaceOperationEnum.DeleteFile:
                    HarborCheckoutBinding fileBinding = RequireBinding(request, ack, grant);
                    HarborCheckoutIntent fileIntent = ReadReadyIntent(request, fileBinding, grant);
                    await ExecuteFileOperationAsync(request, fileIntent, grant, result, token).ConfigureAwait(false);
                    break;
                case HarborWorkspaceOperationEnum.AddGitExclude:
                    HarborCheckoutBinding excludeBinding = RequireBinding(request, ack, grant);
                    HarborCheckoutIntent excludeIntent = ReadReadyIntent(request, excludeBinding, grant);
                    await AddGitExcludeAsync(request, excludeIntent, grant, result, token).ConfigureAwait(false);
                    break;
                case HarborWorkspaceOperationEnum.RemoveCheckout:
                    HarborCheckoutBinding removeBinding = RequireBinding(request, ack, grant);
                    HarborCheckoutIntent removeIntent = ReadIntent(request, removeBinding, grant);
                    await RemoveCheckoutAsync(request, removeIntent, grant, result, token).ConfigureAwait(false);
                    break;
                case HarborWorkspaceOperationEnum.RunVerification:
                    HarborCheckoutBinding verificationBinding = RequireBinding(request, ack, grant);
                    HarborCheckoutIntent verificationIntent = ReadReadyIntent(request, verificationBinding, grant);
                    await RunVerificationAsync(request, verificationIntent, grant, result, token).ConfigureAwait(false);
                    break;
                case HarborWorkspaceOperationEnum.ReadVerificationOutput:
                    HarborCheckoutBinding outputBinding = RequireBinding(request, ack, grant);
                    HarborCheckoutIntent outputIntent = ReadReadyIntent(request, outputBinding, grant);
                    await CheckExpectedHeadAsync(request, CheckoutPath(grant, outputBinding, true), token).ConfigureAwait(false);
                    CopyVerificationOutputResult(_VerificationOutputs.Read(request), result);
                    result.Binding = outputIntent.Binding;
                    result.HeadCommit = await ReadHeadAsync(CheckoutPath(grant, outputBinding, true), token).ConfigureAwait(false);
                    result.Accepted = true;
                    break;
                case HarborWorkspaceOperationEnum.Git:
                    HarborCheckoutBinding gitBinding = RequireBinding(request, ack, grant);
                    HarborCheckoutIntent gitIntent = ReadIntent(request, gitBinding, grant);
                    bool retainedRepositoryOperation = AllowsRetainedRepositoryOperation(request);
                    if (gitIntent.State != "Ready" && !(retainedRepositoryOperation && gitIntent.State == "Removed"))
                        throw Failure("workspace_checkout_not_ready", "The checkout intent does not permit this Git operation.");
                    await ExecuteGitOperationAsync(request, gitIntent, grant, result, token).ConfigureAwait(false);
                    break;
                default:
                    throw Failure("workspace_operation_unsupported", "The workspace operation is not supported.");
            }
        }
    }
}
