namespace Armada.Core.Services
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<bool> CheckIntegrationBarrierMembersAsync(
            Mission mission, Vessel vessel, MissionIntegrationBarrier barrier,
            string targetSha, CancellationToken token)
        {
            foreach (MissionIntegrationBarrierMember member in barrier.Members)
            {
                Mission? terminal = await _Database.Missions.ReadAsync(member.CurrentTerminalMissionId, token).ConfigureAwait(false);
                if (terminal == null)
                {
                    await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, member.CurrentTerminalMissionId,
                        "Accepted branch " + member.BranchKey + " has no current terminal mission row.", token).ConfigureAwait(false);
                    return false;
                }
                if (!HasBarrierOwner(terminal, barrier)
                    || (terminal.Id == member.AcceptedTerminalMissionId
                        ? !await IsAcceptedBranchTerminalAsync(terminal, member, barrier, token).ConfigureAwait(false)
                        : !await IsAcceptedBranchRescueAsync(terminal, member, barrier, token, false).ConfigureAwait(false)))
                {
                    await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, terminal.Id,
                        "Accepted branch " + member.BranchKey + " current terminal is outside the accepted owner or branch lineage.", token).ConfigureAwait(false);
                    return false;
                }

                if (terminal.Status == MissionStatusEnum.Failed || terminal.Status == MissionStatusEnum.Cancelled
                    || terminal.Status == MissionStatusEnum.LandingFailed)
                {
                    Mission? replacement = await FindAcceptedBranchRescueAsync(terminal, member, barrier, token).ConfigureAwait(false);
                    if (replacement != null)
                        return await RecordIntegrationBarrierRescueAsync(mission, barrier, member, replacement, token).ConfigureAwait(false);

                    await CancelIntegrationBarrierMissionAsync(mission, member, terminal, token).ConfigureAwait(false);
                    return false;
                }

                if (terminal.Status != MissionStatusEnum.Complete)
                {
                    await HoldForAssignmentAsync(mission, IntegrationBarrierWaiting, terminal.Id,
                        "Accepted branch " + member.BranchKey + " has not landed. Integration waits for every branch to reach Complete.", token).ConfigureAwait(false);
                    return false;
                }
                if (String.IsNullOrWhiteSpace(terminal.CommitHash))
                {
                    await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, terminal.Id,
                        "Accepted branch " + member.BranchKey + " has no recorded source commit. Its landing cannot be proved.", token).ConfigureAwait(false);
                    return false;
                }

                bool? sourceLanded = await TryIntegrationAncestryAsync(mission, vessel, terminal.CommitHash!, targetSha, terminal.Id, token).ConfigureAwait(false);
                if (sourceLanded != true)
                {
                    await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, terminal.Id,
                        "Accepted branch " + member.BranchKey + " commit " + terminal.CommitHash + " is not proven on current target " + targetSha + ".", token).ConfigureAwait(false);
                    return false;
                }

                Mission? root = await _Database.Missions.ReadAsync(member.RootMissionId, token).ConfigureAwait(false);
                if (root == null || !HasBarrierOwner(root, barrier)
                    || !String.Equals(root.VoyageId, barrier.OriginVoyageId, StringComparison.Ordinal))
                {
                    await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, member.RootMissionId,
                        "Accepted branch " + member.BranchKey + " root is missing or outside the accepted owner and origin voyage.", token).ConfigureAwait(false);
                    return false;
                }
                if (!String.IsNullOrWhiteSpace(root.StartFromRef))
                {
                    bool? originIsPresent = await TryIntegrationAncestryAsync(mission, vessel, root.StartFromRef!, targetSha, member.RootMissionId, token).ConfigureAwait(false);
                    if (originIsPresent != true)
                    {
                        await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, member.RootMissionId,
                            "Accepted branch " + member.BranchKey + " original start commit is not proven on current target " + targetSha + ".", token).ConfigureAwait(false);
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
