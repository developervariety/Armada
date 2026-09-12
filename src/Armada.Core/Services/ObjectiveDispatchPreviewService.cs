namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Builds a read-only preview of the effective target, pipeline, verification, provisioning,
    /// captain, start-ref, and brief configuration for an objective dispatch.
    /// </summary>
    public sealed class ObjectiveDispatchPreviewService : IObjectiveDispatchPreviewService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly WorkflowProfileService _WorkflowProfiles;
        private readonly VesselReadinessService _VesselReadiness;
        private readonly IGitService _Git;
        private readonly ArmadaSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the objective dispatch preview service.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="workflowProfiles">Workflow profile resolver.</param>
        /// <param name="vesselReadiness">Vessel and workflow readiness service.</param>
        /// <param name="git">Git service used only for revision resolution.</param>
        /// <param name="settings">Armada settings.</param>
        public ObjectiveDispatchPreviewService(
            DatabaseDriver database,
            WorkflowProfileService workflowProfiles,
            VesselReadinessService vesselReadiness,
            IGitService git,
            ArmadaSettings settings)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _WorkflowProfiles = workflowProfiles ?? throw new ArgumentNullException(nameof(workflowProfiles));
            _VesselReadiness = vesselReadiness ?? throw new ArgumentNullException(nameof(vesselReadiness));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Preview one objective without creating a voyage, mission, branch, dock, Check, or lease.
        /// Optional dispatch values let operator paths preview their effective request while the
        /// autonomous path uses the objective's own values.
        /// </summary>
        /// <param name="auth">Authenticated caller context.</param>
        /// <param name="objective">Objective to preview.</param>
        /// <param name="requestedVesselId">Optional operator-selected target vessel.</param>
        /// <param name="requestedPipelineId">Optional operator-selected pipeline ID or name.</param>
        /// <param name="captainAssignments">Optional operator-selected persona assignments.</param>
        /// <param name="missionDescriptions">Optional operator mission descriptions with effective mode and start-ref values.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Complete dispatch preview.</returns>
        public async Task<ObjectiveDispatchPreview> PreviewAsync(
            AuthContext auth,
            Objective objective,
            string? requestedVesselId = null,
            string? requestedPipelineId = null,
            IReadOnlyList<CaptainAssignmentOverride>? captainAssignments = null,
            IReadOnlyList<MissionDescription>? missionDescriptions = null,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (objective == null) throw new ArgumentNullException(nameof(objective));

            ObjectiveDispatchPreview result = new ObjectiveDispatchPreview
            {
                ObjectiveId = objective.Id,
                DeliverableMode = MissionModes.FromObjectiveKind(objective.Kind) ?? MissionModeEnum.Implementation.ToString(),
                StartFromRef = NormalizeEmpty(objective.StartFromRef),
                RenderedBrief = ObjectiveBriefRenderer.Render(objective)
            };

            EvaluateBrief(objective, result);
            List<Objective> objectiveSnapshot = await ReadObjectivesAsync(auth, token).ConfigureAwait(false);
            ObjectiveDependencyAnalysis dependencies = ObjectiveDependencyAnalyzer.Analyze(objective, objectiveSnapshot);
            result.DependencyAnalysis = dependencies;
            result.BlockingChains = dependencies.BlockingChains
                .Select(chain => chain.ObjectiveIds.ToList())
                .ToList();
            if (dependencies.HasCycle)
            {
                AddIssue(result, "objective_dependency_cycle", "admission", ReadinessSeverityEnum.Error,
                    "The objective dependency graph contains a cycle: " + String.Join(" -> ", dependencies.CyclePath) + ".",
                    objective.Id);
            }
            else if (dependencies.BlockingNodes.Any(node => node.IsMissing))
            {
                AddIssue(result, "objective_dependency_missing", "admission", ReadinessSeverityEnum.Error,
                    "One or more objective dependencies do not exist or are not accessible.", objective.Id);
            }
            else if (!dependencies.IsDependencyReady)
            {
                AddIssue(result, "objective_dependencies_incomplete", "admission", ReadinessSeverityEnum.Error,
                    "One or more objective dependency chains are not complete.", objective.Id);
            }

            string? vesselId = ResolveTargetVesselId(objective, requestedVesselId, result);
            result.VesselId = vesselId;
            if (vesselId == null)
            {
                FinalizeResult(result);
                return result;
            }

            Vessel? vessel = await ReadVesselAsync(auth, vesselId, token).ConfigureAwait(false);
            if (vessel == null)
            {
                AddIssue(result, "target_vessel_not_found", "target", ReadinessSeverityEnum.Error,
                    "The target vessel does not exist or is not accessible.", vesselId);
                FinalizeResult(result);
                return result;
            }

            await EvaluateRepositoryContextAsync(vessel, result, token).ConfigureAwait(false);
            await EvaluateStartRefAsync(vessel, result, token).ConfigureAwait(false);
            List<string> effectiveMissionModes = await EvaluateMissionDescriptionsAsync(
                vessel, objective, missionDescriptions, result, token).ConfigureAwait(false);
            await EvaluatePreparationAnchorsAsync(auth, objective, result, token).ConfigureAwait(false);
            await EvaluateSiblingProvisioningAsync(auth, vessel, result, token).ConfigureAwait(false);

            string? effectivePipelineRequest = NormalizeEmpty(requestedPipelineId)
                ?? NormalizeEmpty(objective.SuggestedPipelineId);
            Pipeline? pipeline = await ResolvePipelineReadOnlyAsync(
                vessel,
                effectivePipelineRequest,
                effectiveMissionModes.Count > 0
                    ? effectiveMissionModes.All(IsReadOnlyMissionMode)
                    : objective.Kind == ObjectiveKindEnum.Research,
                result,
                token).ConfigureAwait(false);
            if (pipeline != null)
            {
                result.PipelineId = pipeline.Id;
                result.PipelineName = pipeline.Name;
            }
            else if (!result.Issues.Any(issue => issue.Area == "pipeline" && issue.Severity == ReadinessSeverityEnum.Error))
            {
                result.PipelineName = "WorkerOnly";
            }

            List<Captain> captains = await ReadCaptainsAsync(auth, token).ConfigureAwait(false);
            EvaluateCaptainCoverage(pipeline, captains, captainAssignments, missionDescriptions, result);
            await EvaluateChecksAsync(auth, vessel, result, token).ConfigureAwait(false);

            FinalizeResult(result);
            return result;
        }

        #endregion

        #region Private-Methods

        private static void EvaluateBrief(Objective objective, ObjectiveDispatchPreview result)
        {
            if (String.IsNullOrWhiteSpace(objective.Description))
                AddIssue(result, "brief_scope_missing", "brief", ReadinessSeverityEnum.Error,
                    "The objective description must state the dispatch scope.", null);

            if (objective.AcceptanceCriteria == null || !objective.AcceptanceCriteria.Any(item => !String.IsNullOrWhiteSpace(item)))
                AddIssue(result, "brief_acceptance_missing", "brief", ReadinessSeverityEnum.Error,
                    "The objective must state at least one acceptance criterion.", null);

            ObjectivePreparation preparation = objective.Preparation ?? new ObjectivePreparation();
            List<ObjectivePreparationClaim> claims = preparation.Claims ?? new List<ObjectivePreparationClaim>();
            bool hasMethod = !String.IsNullOrWhiteSpace(objective.RefinementSummary)
                || claims.Any(claim => claim != null
                    && claim.State == ObjectivePreparationClaimStateEnum.Verified
                    && !String.IsNullOrWhiteSpace(claim.Text)
                    && (claim.Kind == ObjectivePreparationClaimKindEnum.DispatchEntryPoint
                        || claim.Kind == ObjectivePreparationClaimKindEnum.ReuseType
                        || claim.Kind == ObjectivePreparationClaimKindEnum.ResponseRule));
            if (!hasMethod)
                AddIssue(result, "brief_method_missing", "brief", ReadinessSeverityEnum.Error,
                    "The objective must state an implementation method in its refinement summary or verified preparation.", null);

            foreach (ObjectivePreparationClaim claim in claims.Where(item => item != null
                && item.State == ObjectivePreparationClaimStateEnum.NeedsRecheck))
            {
                AddIssue(result, "preparation_claim_needs_recheck", "brief", ReadinessSeverityEnum.Error,
                    "A preparation claim must be verified again before dispatch: " + claim.Text, claim.Id);
            }
        }

        private static string? ResolveTargetVesselId(
            Objective objective,
            string? requestedVesselId,
            ObjectiveDispatchPreview result)
        {
            string? requested = NormalizeEmpty(requestedVesselId);
            List<string> objectiveTargets = (objective.VesselIds ?? new List<string>())
                .Where(item => !String.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requested != null)
            {
                if (objectiveTargets.Count != 1)
                {
                    AddIssue(result, "target_vessel_count", "target", ReadinessSeverityEnum.Error,
                        "Objective dispatch requires exactly one objective target vessel; the objective has " + objectiveTargets.Count + ".", null);
                }
                else if (!objectiveTargets.Contains(requested, StringComparer.OrdinalIgnoreCase))
                {
                    AddIssue(result, "objective_vessel_mismatch", "target", ReadinessSeverityEnum.Error,
                        "The requested vessel is not one of the objective target vessels.", requested);
                }
                return requested;
            }

            if (objectiveTargets.Count != 1)
            {
                AddIssue(result, "target_vessel_count", "target", ReadinessSeverityEnum.Error,
                    "Objective dispatch requires exactly one target vessel; the objective has " + objectiveTargets.Count + ".", null);
                return null;
            }

            return objectiveTargets[0];
        }

        private async Task EvaluateRepositoryContextAsync(
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            string repositoryPath = RepositoryPath(vessel);
            bool localExists = await _Git.IsRepositoryAsync(repositoryPath, token).ConfigureAwait(false);
            bool canClone = !String.IsNullOrWhiteSpace(vessel.RepoUrl);
            if (!localExists && !canClone)
            {
                AddIssue(result, "target_repository_unavailable", "provisioning", ReadinessSeverityEnum.Error,
                    "The target vessel has no usable local repository and no repository URL.", repositoryPath);
            }
            else if (!localExists)
            {
                AddIssue(result, "target_repository_clone_required", "provisioning", ReadinessSeverityEnum.Warning,
                    "Dock provisioning must clone the target repository before work starts.", vessel.RepoUrl);
            }
        }

        private async Task EvaluateStartRefAsync(Vessel vessel, ObjectiveDispatchPreview result, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(result.StartFromRef)) return;
            string repositoryPath = RepositoryPath(vessel);
            string? commit = await _Git.GetRevisionShaAsync(repositoryPath, result.StartFromRef, token).ConfigureAwait(false);
            result.ResolvedStartCommit = NormalizeEmpty(commit);
            if (String.IsNullOrWhiteSpace(commit))
            {
                AddIssue(result, "start_from_ref_missing", "provisioning", ReadinessSeverityEnum.Error,
                    "The objective start ref does not resolve in the target repository.", result.StartFromRef);
            }
        }

        private async Task<List<string>> EvaluateMissionDescriptionsAsync(
            Vessel vessel,
            Objective objective,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            List<string> modes = new List<string>();
            if (missionDescriptions == null || missionDescriptions.Count == 0) return modes;

            string? objectiveMode = MissionModes.FromObjectiveKind(objective.Kind);
            for (int index = 0; index < missionDescriptions.Count; index++)
            {
                MissionDescription? mission = missionDescriptions[index];
                if (mission == null) continue;
                string? suppliedMode = NormalizeEmpty(mission.Mode);
                if (suppliedMode != null && !MissionModes.IsKnown(suppliedMode))
                {
                    AddIssue(result, "invalid_mission_mode", "brief", ReadinessSeverityEnum.Error,
                        "Mission " + (index + 1) + " has an unknown mode: " + suppliedMode + ".", suppliedMode);
                }
                string effectiveMode = suppliedMode != null && MissionModes.IsKnown(suppliedMode)
                    ? MissionModes.Parse(suppliedMode).ToString()
                    : objectiveMode ?? MissionModeEnum.Implementation.ToString();
                modes.Add(effectiveMode);

                string? startRef = NormalizeEmpty(mission.StartFromRef) ?? NormalizeEmpty(objective.StartFromRef);
                if (!String.IsNullOrWhiteSpace(mission.DependsOnMissionId)
                    || !String.IsNullOrWhiteSpace(mission.DependsOnMissionAlias))
                    startRef = null;
                string? commit = startRef == null
                    ? null
                    : NormalizeEmpty(await _Git.GetRevisionShaAsync(RepositoryPath(vessel), startRef, token).ConfigureAwait(false));
                result.MissionStartRefs.Add(new ObjectiveDispatchMissionRef
                {
                    MissionIndex = index,
                    Title = mission.Title ?? String.Empty,
                    Mode = effectiveMode,
                    StartFromRef = startRef,
                    ResolvedCommit = commit
                });
                if (startRef != null && commit == null)
                {
                    AddIssue(result, "mission_start_from_ref_missing", "provisioning", ReadinessSeverityEnum.Error,
                        "The start ref for mission " + (index + 1) + " does not resolve in the target repository.", startRef);
                }
            }
            result.EffectiveMissionModes = modes.ToList();
            if (modes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                result.DeliverableMode = modes[0];
            else if (modes.Count > 1)
                result.DeliverableMode = "Mixed";
            return modes;
        }

        private async Task EvaluatePreparationAnchorsAsync(
            AuthContext auth,
            Objective objective,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            ObjectivePreparation preparation = objective.Preparation ?? new ObjectivePreparation();
            await EvaluateAnchorAsync(auth, preparation.Source, "source", result, token).ConfigureAwait(false);
            await EvaluateAnchorAsync(auth, preparation.Target, "target", result, token).ConfigureAwait(false);

            if (preparation.Target != null
                && !String.IsNullOrWhiteSpace(preparation.Target.VesselId)
                && !String.Equals(preparation.Target.VesselId, result.VesselId, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(result, "preparation_target_vessel_mismatch", "target", ReadinessSeverityEnum.Error,
                    "The prepared target vessel does not match the dispatch target vessel.", preparation.Target.VesselId);
            }
        }

        private async Task EvaluateAnchorAsync(
            AuthContext auth,
            ObjectivePreparationAnchor? anchor,
            string label,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (anchor == null) return;
            if (String.IsNullOrWhiteSpace(anchor.VesselId))
            {
                AddIssue(result, "preparation_" + label + "_vessel_missing", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " anchor must name a vessel.", null);
                return;
            }
            if (String.IsNullOrWhiteSpace(anchor.Ref) || String.IsNullOrWhiteSpace(anchor.ResolvedCommit))
            {
                AddIssue(result, "preparation_" + label + "_revision_incomplete", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " anchor must include a ref and resolved commit.", anchor.VesselId);
                return;
            }

            Vessel? anchorVessel = await ReadVesselAsync(auth, anchor.VesselId, token).ConfigureAwait(false);
            if (anchorVessel == null)
            {
                AddIssue(result, "preparation_" + label + "_vessel_not_found", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " vessel does not exist or is not accessible.", anchor.VesselId);
                return;
            }

            string? current = await _Git.GetRevisionShaAsync(RepositoryPath(anchorVessel), anchor.Ref, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(current))
            {
                AddIssue(result, "preparation_" + label + "_ref_missing", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " ref does not resolve.", anchor.Ref);
            }
            else if (!RevisionMatches(current, anchor.ResolvedCommit))
            {
                AddIssue(result, "preparation_" + label + "_revision_changed", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " ref no longer resolves to the recorded commit.", anchor.Ref);
            }
        }

        private async Task EvaluateSiblingProvisioningAsync(
            AuthContext auth,
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
            {
                if (sibling == null) continue;
                if (String.IsNullOrWhiteSpace(sibling.RelativePath))
                {
                    AddIssue(result, "sibling_relative_path_missing", "provisioning", ReadinessSeverityEnum.Error,
                        "A declared sibling repository has no relative checkout path.", null);
                    continue;
                }

                Vessel? siblingVessel = null;
                if (!String.IsNullOrWhiteSpace(sibling.VesselRef))
                {
                    siblingVessel = await ReadVesselAsync(auth, sibling.VesselRef, token).ConfigureAwait(false)
                        ?? await ReadVesselByNameAsync(auth, sibling.VesselRef, token).ConfigureAwait(false);
                }
                bool registeredSourceReady = siblingVessel != null
                    && await _Git.IsRepositoryAsync(RepositoryPath(siblingVessel), token).ConfigureAwait(false);
                bool hasSource = registeredSourceReady
                    || !String.IsNullOrWhiteSpace(siblingVessel?.RepoUrl)
                    || !String.IsNullOrWhiteSpace(sibling.RepoUrl);
                if (!hasSource)
                {
                    AddIssue(result, "sibling_source_unavailable", "provisioning",
                        sibling.BuildParticipant ? ReadinessSeverityEnum.Error : ReadinessSeverityEnum.Warning,
                        "Armada cannot resolve the source for sibling repository " + sibling.RelativePath + ".", sibling.VesselRef);
                }

                if (sibling.ExtractionArtifactPaths == null || sibling.ExtractionArtifactPaths.Count == 0) continue;
                if (siblingVessel == null || String.IsNullOrWhiteSpace(siblingVessel.WorkingDirectory))
                {
                    AddIssue(result, "sibling_artifact_source_unavailable", "provisioning", ReadinessSeverityEnum.Error,
                        "The sibling extraction artifacts require a registered vessel with a working directory.", sibling.RelativePath);
                    continue;
                }

                foreach (string artifactPath in sibling.ExtractionArtifactPaths.Where(item => !String.IsNullOrWhiteSpace(item)))
                {
                    string sourcePath = Path.Combine(siblingVessel.WorkingDirectory, artifactPath);
                    if (!Directory.Exists(sourcePath))
                    {
                        AddIssue(result, "sibling_artifact_missing", "provisioning", ReadinessSeverityEnum.Error,
                            "A required sibling extraction artifact path does not exist.", sourcePath);
                    }
                }
            }
        }

        private async Task<Pipeline?> ResolvePipelineReadOnlyAsync(
            Vessel vessel,
            string? explicitPipeline,
            bool readOnly,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(explicitPipeline))
            {
                Pipeline? requested = await _Database.Pipelines.ReadAsync(explicitPipeline, token).ConfigureAwait(false)
                    ?? await _Database.Pipelines.ReadByNameAsync(explicitPipeline, token).ConfigureAwait(false);
                if (requested == null)
                    AddIssue(result, "pipeline_not_found", "pipeline", ReadinessSeverityEnum.Error,
                        "The requested pipeline does not exist.", explicitPipeline);
                else
                    ValidatePipeline(requested, result);
                return requested;
            }

            if (readOnly) return null;

            string? inheritedPipelineId = NormalizeEmpty(vessel.DefaultPipelineId);
            if (inheritedPipelineId == null && !String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                Fleet? fleet = await _Database.Fleets.ReadAsync(vessel.FleetId, token).ConfigureAwait(false);
                inheritedPipelineId = NormalizeEmpty(fleet?.DefaultPipelineId);
            }
            if (inheritedPipelineId == null) return null;

            Pipeline? inherited = await _Database.Pipelines.ReadAsync(inheritedPipelineId, token).ConfigureAwait(false);
            if (inherited == null)
            {
                AddIssue(result, "default_pipeline_not_found", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective default pipeline does not exist.", inheritedPipelineId);
                return null;
            }
            ValidatePipeline(inherited, result);
            return inherited;
        }

        private static void ValidatePipeline(Pipeline pipeline, ObjectiveDispatchPreview result)
        {
            if (!pipeline.Active)
                AddIssue(result, "pipeline_inactive", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective pipeline is not active.", pipeline.Id);
            if (pipeline.Stages == null || pipeline.Stages.Count == 0)
                AddIssue(result, "pipeline_has_no_stages", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective pipeline has no stages.", pipeline.Id);
        }

        private void EvaluateCaptainCoverage(
            Pipeline? pipeline,
            List<Captain> captains,
            IReadOnlyList<CaptainAssignmentOverride>? captainAssignments,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            ObjectiveDispatchPreview result)
        {
            List<PipelineStage> stages = pipeline?.Stages?.ToList()
                ?? new List<PipelineStage> { new PipelineStage(1, "Worker") };
            IReadOnlyCollection<string> specialistPersonas = _Settings.ModelTier.SpecialistPersonas;
            foreach (PipelineStage stage in stages
                .Where(item => item != null)
                .OrderBy(item => item.Order))
            {
                List<string?> resolvedPreferences = ResolveStagePreferredModels(
                    stage, missionDescriptions, specialistPersonas);
                foreach (string? preferredModel in resolvedPreferences.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    CaptainAssignmentOverride? assignment = captainAssignments?.FirstOrDefault(item => item != null
                        && PersonaCatalog.Matches(item.Persona, stage.PersonaName));
                    CaptainTierEnum? fallbackTier = assignment?.FallbackTier;
                    List<Captain> configured = captains
                        .Where(IsConfiguredUsableCaptain)
                        .Where(captain => MissionService.CaptainSatisfiesPreferredRouting(
                            captain, stage.PersonaName, preferredModel, _Settings.ModelTier))
                        .Where(captain => fallbackTier == null || CaptainTierSelector.EffectiveTier(captain) >= fallbackTier.Value)
                        .ToList();

                    if (assignment != null && !String.IsNullOrWhiteSpace(assignment.CaptainId))
                    {
                        Captain? selected = captains.FirstOrDefault(captain => String.Equals(
                            captain.Id, assignment.CaptainId, StringComparison.Ordinal));
                        if (selected == null || !IsConfiguredUsableCaptain(selected)
                            || !MissionService.CaptainSatisfiesPreferredRouting(
                                selected, stage.PersonaName, preferredModel, _Settings.ModelTier))
                        {
                            AddIssue(result, "assigned_captain_ineligible", "captain", ReadinessSeverityEnum.Error,
                                "The assigned captain cannot run the " + stage.PersonaName + " role.", assignment.CaptainId);
                        }
                        else if (!configured.Any(captain => String.Equals(captain.Id, selected.Id, StringComparison.Ordinal)))
                        {
                            configured.Add(selected);
                        }
                    }

                    ObjectiveDispatchRole role = new ObjectiveDispatchRole
                    {
                        Persona = stage.PersonaName,
                        PreferredModel = preferredModel,
                        EligibleConfiguredCaptainIds = configured.Select(captain => captain.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
                        IdleEligibleCount = configured.Count(captain => captain.State == CaptainStateEnum.Idle)
                    };
                    result.RequiredRoles.Add(role);

                    if (configured.Count == 0)
                    {
                        AddIssue(result, "required_role_has_no_captain", "captain", ReadinessSeverityEnum.Error,
                            "No configured captain can run the required " + stage.PersonaName + " role.", preferredModel);
                    }
                    else if (role.IdleEligibleCount == 0)
                    {
                        AddIssue(result, "required_role_has_no_idle_captain", "captain", ReadinessSeverityEnum.Warning,
                            "The required " + stage.PersonaName + " role has configured coverage but no idle capacity.", preferredModel);
                    }
                }
            }
        }

        private static List<string?> ResolveStagePreferredModels(
            PipelineStage stage,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            IReadOnlyCollection<string> specialistPersonas)
        {
            List<string?> resolved = new List<string?>();
            if (missionDescriptions == null || missionDescriptions.Count == 0)
            {
                resolved.Add(PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel, null, stage.PersonaName, specialistPersonas));
                return resolved;
            }

            foreach (MissionDescription missionDescription in missionDescriptions)
            {
                if (missionDescription == null) continue;
                resolved.Add(PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel,
                    missionDescription.PreferredModel,
                    stage.PersonaName,
                    specialistPersonas));
            }

            if (resolved.Count == 0)
            {
                resolved.Add(PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel, null, stage.PersonaName, specialistPersonas));
            }

            return resolved;
        }

        private async Task EvaluateChecksAsync(
            AuthContext auth,
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            VoyageCheckArmingSettings arming = _Settings.VoyageCheckArming;
            if (!arming.Enabled)
            {
                AddIssue(result, "check_arming_disabled", "verification", ReadinessSeverityEnum.Error,
                    "Voyage Check arming is disabled, so dispatch cannot produce the required verification records.", null);
                return;
            }

            WorkflowProfile? profile = await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, null, token).ConfigureAwait(false);
            if (profile == null)
            {
                AddIssue(result, "workflow_profile_missing", "verification", ReadinessSeverityEnum.Error,
                    "No active workflow profile resolves for the target vessel.", vessel.Id);
                AddRequiredCheck(result, CheckRunTypeEnum.Build, null, false, arming.ArmBuild);
                AddRequiredCheck(result, CheckRunTypeEnum.UnitTest, null, false, arming.ArmUnitTest);
                return;
            }

            await EvaluateRequiredCheckAsync(auth, vessel, profile, CheckRunTypeEnum.Build, arming.ArmBuild, result, token).ConfigureAwait(false);
            await EvaluateRequiredCheckAsync(auth, vessel, profile, CheckRunTypeEnum.UnitTest, arming.ArmUnitTest, result, token).ConfigureAwait(false);
        }

        private async Task EvaluateRequiredCheckAsync(
            AuthContext auth,
            Vessel vessel,
            WorkflowProfile profile,
            CheckRunTypeEnum type,
            bool required,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (!required) return;
            VesselReadinessResult readiness = await _VesselReadiness.EvaluateAsync(
                auth,
                vessel,
                profile.Id,
                type,
                includeWorkflowRequirements: true,
                token: token).ConfigureAwait(false);
            bool inputsReady = !readiness.Issues.Any(issue => String.Equals(
                issue.Code, "required_input_missing", StringComparison.OrdinalIgnoreCase));
            foreach (VesselReadinessIssue issue in readiness.Issues.Where(issue =>
                String.Equals(issue.Code, "required_input_missing", StringComparison.OrdinalIgnoreCase)
                || String.Equals(issue.Code, "workflow_profile_invalid", StringComparison.OrdinalIgnoreCase)
                || String.Equals(issue.Code, "command_dependency_missing", StringComparison.OrdinalIgnoreCase)))
            {
                AddIssue(result, issue.Code, "verification", ReadinessSeverityEnum.Error, issue.Message, issue.RelatedValue);
            }
            AddRequiredCheck(result, type, _WorkflowProfiles.ResolveCommand(profile, type), inputsReady, true);
        }

        private static void AddRequiredCheck(
            ObjectiveDispatchPreview result,
            CheckRunTypeEnum type,
            string? command,
            bool inputsReady,
            bool required)
        {
            if (!required) return;
            result.RequiredChecks.Add(new ObjectiveDispatchCheck
            {
                Type = type,
                Command = NormalizeEmpty(command),
                RequiredInputsReady = inputsReady
            });
            if (String.IsNullOrWhiteSpace(command))
            {
                AddIssue(result, "check_command_missing", "verification", ReadinessSeverityEnum.Error,
                    "No command is configured for required Check " + type + ".", type.ToString());
            }
        }

        private async Task<Vessel?> ReadVesselAsync(AuthContext auth, string id, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Vessels.ReadAsync(id, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return null;
            if (auth.IsTenantAdmin) return await _Database.Vessels.ReadAsync(auth.TenantId, id, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return null;
            return await _Database.Vessels.ReadAsync(auth.TenantId, auth.UserId, id, token).ConfigureAwait(false);
        }

        private async Task<Vessel?> ReadVesselByNameAsync(AuthContext auth, string name, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Vessels.ReadByNameAsync(name, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return null;
            Vessel? vessel = await _Database.Vessels.ReadByNameAsync(auth.TenantId, name, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin) return vessel;
            if (String.IsNullOrWhiteSpace(auth.UserId)) return null;
            return vessel != null && String.Equals(vessel.UserId, auth.UserId, StringComparison.Ordinal)
                ? vessel
                : null;
        }

        private async Task<List<Captain>> ReadCaptainsAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return new List<Captain>();
            if (auth.IsTenantAdmin) return await _Database.Captains.EnumerateAsync(auth.TenantId, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return new List<Captain>();
            return await _Database.Captains.EnumerateAsync(auth.TenantId, auth.UserId, token).ConfigureAwait(false);
        }

        private async Task<List<Objective>> ReadObjectivesAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return new List<Objective>();
            if (auth.IsTenantAdmin) return await _Database.Objectives.EnumerateAsync(auth.TenantId, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return new List<Objective>();
            return await _Database.Objectives.EnumerateAsync(auth.TenantId, auth.UserId, token).ConfigureAwait(false);
        }

        private static bool IsConfiguredUsableCaptain(Captain captain)
        {
            return captain.State != CaptainStateEnum.Benched
                && captain.State != CaptainStateEnum.Quarantined
                && captain.State != CaptainStateEnum.Stalled
                && captain.State != CaptainStateEnum.Stopping;
        }

        private string RepositoryPath(Vessel vessel)
        {
            return vessel.LocalPath ?? Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");
        }

        private static bool RevisionMatches(string left, string right)
        {
            return left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
                || right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReadOnlyMissionMode(string mode)
        {
            MissionModeEnum parsed = MissionModes.Parse(mode);
            return parsed == MissionModeEnum.Audit || parsed == MissionModeEnum.Research;
        }

        private static string? NormalizeEmpty(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static void AddIssue(
            ObjectiveDispatchPreview result,
            string code,
            string area,
            ReadinessSeverityEnum severity,
            string message,
            string? relatedValue)
        {
            result.Issues.Add(new ObjectiveDispatchPreviewIssue
            {
                Code = code,
                Area = area,
                Severity = severity,
                Message = message,
                RelatedValue = relatedValue
            });
        }

        private static void FinalizeResult(ObjectiveDispatchPreview result)
        {
            result.ErrorCount = result.Issues.Count(issue => issue.Severity == ReadinessSeverityEnum.Error);
            result.WarningCount = result.Issues.Count(issue => issue.Severity == ReadinessSeverityEnum.Warning);
            result.IsReady = result.ErrorCount == 0;
        }

        #endregion
    }
}
