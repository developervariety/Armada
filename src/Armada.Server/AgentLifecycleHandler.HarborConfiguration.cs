namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;

    public partial class AgentLifecycleHandler
    {
        private async Task ApplyOwnedRuntimeConfigurationAsync(HarborProcessLaunch launch, Captain captain,
            Mission mission, Dock dock, CancellationToken token = default)
        {
            if (!_Settings.SeedDockRuntimeMcpConfig) return;
            if (String.IsNullOrWhiteSpace(_Settings.Harbor.CaptainMcpBaseUrl))
                throw new HarborLaunchException("harbor_captain_mcp_endpoint_missing");
            HarborCheckoutService owner = _HarborCheckouts ?? throw new HarborLaunchException("harbor_workspace_unavailable");
            HarborCheckoutBinding binding = dock.HostCheckout ?? throw new HarborLaunchException("harbor_checkout_binding_missing");
            McpCredentialReference credential = await ResolveMissionMcpCredentialAsync(mission, token).ConfigureAwait(false);
            string relativeDirectory = ".armada/runtime/" + captain.Id;
            string hostDirectory = launch.WorkingDirectory.TrimEnd('/', '\\') + "/" + relativeDirectory;
            CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.PlanForHarbor(captain.Runtime,
                _Settings.Harbor.CaptainMcpBaseUrl, hostDirectory, credential);
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("runtime-config:" + binding.Revision, binding.BaseCommit))
            {
                await owner.WriteTextAsync(HarborCheckoutAddress.ForDock(dock.Id), relativeDirectory + "/.scope",
                    mission.Id, true, token).ConfigureAwait(false);
                foreach (IsolationConfigFile file in plan.FilesToWrite)
                    await owner.WriteTextAsync(Armada.Core.Harbor.HarborCheckoutAddress.ForDock(dock.Id),
                        relativeDirectory + "/" + file.RelativePath.Replace('\\', '/'), file.Contents, true, token).ConfigureAwait(false);
                await owner.AddGitExcludeAsync(HarborCheckoutAddress.ForDock(dock.Id), ".armada/runtime/", token).ConfigureAwait(false);
            }
            launch.ExtraArguments.AddRange(plan.ExtraArguments);
            launch.MissionConfiguration = new HarborMissionRuntimeConfiguration
            {
                Token = credential.Token,
                Directory = hostDirectory,
                ScopedHome = captain.Runtime == AgentRuntimeEnum.Gemini || captain.Runtime == AgentRuntimeEnum.Cursor,
                OpenCodeConfig = captain.Runtime == AgentRuntimeEnum.OpenCode
            };
        }
    }
}
