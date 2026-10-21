namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MissionService
    {
        #region Private-Methods

        /// <summary>
        /// The gate-input entries declared on the workflow profile that resolves for the mission's vessel, read the
        /// way Check arming resolves it. Empty when no profile resolves, it declares none, or it cannot be read.
        /// </summary>
        private async Task<List<string>> ReadGateInputPathsAsync(Mission mission, Vessel? vessel, CancellationToken token)
        {
            try
            {
                Vessel? target = vessel;
                if (target == null && !String.IsNullOrWhiteSpace(mission.VesselId))
                    target = await _Database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
                if (target == null) return new List<string>();

                AuthContext auth = AuthContext.Authenticated(
                    mission.TenantId ?? target.TenantId ?? "default",
                    mission.UserId ?? "default",
                    true,
                    true,
                    "system");
                WorkflowProfileService profiles = new WorkflowProfileService(_Database, _Logging);
                WorkflowProfile? profile = await profiles.ResolveForVesselAsync(auth, target, null, token).ConfigureAwait(false);
                return WorkflowGateInputs.Normalize(profile?.GateInputPaths);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read gate inputs for mission " + mission.Id + ": " + ex.Message);
                return new List<string>();
            }
        }

        /// <summary>
        /// The Gate Input Files section of an Architect brief, or an empty string for any other persona or when the
        /// vessel's profile declares no gate inputs.
        /// </summary>
        private async Task<string> BuildGateInputsSectionAsync(Mission mission, Vessel vessel, CancellationToken token)
        {
            if (!PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Architect)) return String.Empty;
            List<string> paths = await ReadGateInputPathsAsync(mission, vessel, token).ConfigureAwait(false);
            return WorkflowGateInputs.BuildArchitectSection(paths);
        }

        /// <summary>
        /// Record a warning event when an Architect plan names none of the files some declared gate inputs cover.
        /// The plan still fans out: the event tells the operator which gated files no mission was given, and does
        /// not block the voyage.
        /// </summary>
        private async Task RecordArchitectPlanGateInputsAsync(Mission architect, List<ParsedArchitectMission> parsed, CancellationToken token)
        {
            List<string> paths = await ReadGateInputPathsAsync(architect, null, token).ConfigureAwait(false);
            if (paths.Count == 0) return;

            List<string?> planTexts = new List<string?>();
            foreach (ParsedArchitectMission planned in parsed)
            {
                planTexts.Add(planned.Title);
                planTexts.Add(planned.Description);
            }
            List<string> unplanned = WorkflowGateInputs.FindUnplanned(paths, planTexts);
            if (unplanned.Count == 0) return;

            string message = "Architect " + architect.Id + " plan of " + parsed.Count
                + " missions names none of these gate inputs the required checks read: "
                + String.Join(", ", unplanned)
                + ". If the change touches what they describe, a mission must own them.";
            _Logging.Warn(_Header + message);
            try
            {
                ArmadaEvent evt = new ArmadaEvent(WorkflowGateInputs.PlanMissingEventType, message);
                evt.TenantId = architect.TenantId;
                evt.UserId = architect.UserId;
                evt.EntityType = "mission";
                evt.EntityId = architect.Id;
                evt.MissionId = architect.Id;
                evt.VesselId = architect.VesselId;
                evt.VoyageId = architect.VoyageId;
                evt.Payload = JsonSerializer.Serialize(new { missionCount = parsed.Count, unplannedGateInputs = unplanned });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _Logging.Warn(_Header + "could not record architect plan gate-input event for " + architect.Id + ": " + ex.Message);
            }
        }

        #endregion
    }
}
