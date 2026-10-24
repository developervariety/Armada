namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    public sealed partial class MissionServiceRequestedCaptainRoutingTests
    {
        private static bool HarborCheckoutRemovalIsTerminal(Dock dock)
        {
            return dock.HostCheckoutLifecycle != null
                && dock.HostCheckoutLifecycle.Phase == Armada.Core.Enums.HarborCheckoutLifecyclePhaseEnum.Terminal
                && dock.HostCheckoutLifecycle.RemovalConfirmed;
        }

        private sealed partial class RetryAfterPersistedDockChannel : IHarborWorkspaceOperations
        {
            private readonly bool _ThrowAfterFirstProvision;
            public RetryAfterPersistedDockChannel(bool throwAfterFirstProvision = true)
            {
                _ThrowAfterFirstProvision = throwAfterFirstProvision;
            }

            public int ProvisionCount { get; private set; }
            public HarborCheckoutBinding? LastBinding { get; private set; }
            public string? LastOperation { get; private set; }

            public Task<HarborWorkspaceOperationResult> ExecuteAsync(AuthContext auth,
                HarborWorkspaceOperationRequest request, CancellationToken token = default)
            {
                LastOperation = request.Operation.ToString();
                HarborWorkspaceOperationResult result = new HarborWorkspaceOperationResult
                {
                    Accepted = true,
                    RequestId = request.RequestId,
                    Operation = request.Operation,
                    Binding = request.Binding
                };
                if (IsFilesystemOperation(request.Operation))
                {
                    HandleFilesystemOperation(request, result);
                    return Task.FromResult(result);
                }
                if (request.Operation == HarborWorkspaceOperationEnum.InspectRepository)
                {
                    result.RepositoryFingerprint = request.ExpectedRepositoryFingerprint;
                    result.HeadCommit = new string('a', 40);
                    result.BranchName = request.RequestedRevision;
                }
                else if (request.Operation == HarborWorkspaceOperationEnum.ProvisionCheckout)
                {
                    ProvisionCount++;
                    LastBinding = request.Binding;
                    if (_ThrowAfterFirstProvision && ProvisionCount == 1) throw new InvalidOperationException("simulated_runner_disconnect_after_intent");
                    result.HeadCommit = request.Binding!.BaseCommit;
                    result.BranchName = request.Detached ? "(detached)" : request.Binding.BranchName;
                }
                else if (request.Operation == HarborWorkspaceOperationEnum.Git
                    && request.Git?.Operation == HarborGitOperationEnum.GetHeadCommitHash)
                {
                    result.Git = new HarborGitOperationResult { Text = new string('a', 40) };
                }
                else if (request.Operation == HarborWorkspaceOperationEnum.Git)
                {
                    if (request.Git?.Operation == HarborGitOperationEnum.SnapshotDockWork)
                        result.Git = new HarborGitOperationResult { Snapshot = DockWorkSnapshot.NoChanges() };
                    else
                        result.Git = new HarborGitOperationResult
                        {
                            Flag = request.Git?.Operation == HarborGitOperationEnum.IsPathTracked ? false : null
                        };
                }
                else if (request.Operation != HarborWorkspaceOperationEnum.RemoveCheckout)
                {
                    throw new InvalidOperationException("unexpected Harbor workspace operation " + request.Operation);
                }
                return Task.FromResult(result);
            }
        }
    }
}
