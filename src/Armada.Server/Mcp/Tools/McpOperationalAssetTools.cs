namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers a read-only integrity audit for operator-managed workflow assets.
    /// </summary>
    public static class McpOperationalAssetTools
    {
        /// <summary>
        /// Register operational-asset audit tools.
        /// </summary>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            WorkflowProfileService workflowProfiles,
            DeploymentEnvironmentService environments,
            RunbookService runbooks)
        {
            register(
                "armada_audit_operational_assets",
                "Read-only integrity audit for playbooks, runbooks, workflow profiles, environments, personas, pipelines, and default links. Operator-only; never delivered to captains.",
                new { type = "object", properties = new { } },
                async args => await AuditAsync(database, workflowProfiles, environments, runbooks).ConfigureAwait(false));
        }

        private static async Task<object> AuditAsync(
            DatabaseDriver database,
            WorkflowProfileService workflowProfiles,
            DeploymentEnvironmentService environments,
            RunbookService runbooks)
        {
            List<Playbook> playbooks = await database.Playbooks.EnumerateAsync().ConfigureAwait(false);
            List<Persona> personas = await database.Personas.EnumerateAsync().ConfigureAwait(false);
            List<PromptTemplate> templates = await database.PromptTemplates.EnumerateAsync().ConfigureAwait(false);
            List<Pipeline> pipelines = await database.Pipelines.EnumerateAsync().ConfigureAwait(false);
            List<Fleet> fleets = await database.Fleets.EnumerateAsync().ConfigureAwait(false);
            List<Vessel> vessels = await database.Vessels.EnumerateAsync().ConfigureAwait(false);
            List<Captain> captains = await database.Captains.EnumerateAsync().ConfigureAwait(false);
            List<WorkflowProfile> profiles = await database.WorkflowProfiles.EnumerateAllAsync(new WorkflowProfileQuery
            {
                PageNumber = 1,
                PageSize = 1000
            }).ConfigureAwait(false);
            AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
            EnumerationResult<DeploymentEnvironment> environmentPage = await environments.EnumerateAsync(auth, new DeploymentEnvironmentQuery
            {
                PageNumber = 1,
                PageSize = 500
            }).ConfigureAwait(false);
            EnumerationResult<Runbook> runbookPage = await runbooks.EnumerateAsync(auth, new RunbookQuery
            {
                PageNumber = 1,
                PageSize = 500
            }).ConfigureAwait(false);

            Dictionary<string, Playbook> playbooksById = playbooks.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Persona> personasByName = personas.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, PromptTemplate> templatesByName = templates.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Pipeline> pipelinesById = pipelines.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, WorkflowProfile> profilesById = profiles.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DeploymentEnvironment> environmentsById = environmentPage.Objects.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            List<AssetIssue> issues = new List<AssetIssue>();

            AddDefaultPlaybookIssues(issues, "fleet", fleets.Select(item => (item.Name, item.GetDefaultPlaybooks())), playbooksById);
            AddDefaultPlaybookIssues(issues, "vessel", vessels.Select(item => (item.Name, item.GetDefaultPlaybooks())), playbooksById);
            AddDefaultPlaybookIssues(issues, "persona", personas.Select(item => (item.Name, item.GetDefaultPlaybooks())), playbooksById);
            AddDefaultPlaybookIssues(issues, "captain", captains.Select(item => (item.Name, item.GetDefaultPlaybooks())), playbooksById);

            foreach (Persona persona in personas.Where(item => item.Active))
            {
                if (!templatesByName.TryGetValue(persona.PromptTemplateName, out PromptTemplate? template) || !template.Active)
                    issues.Add(new AssetIssue("error", "persona_prompt", persona.Name, "Prompt template is missing or inactive: " + persona.PromptTemplateName));
            }

            foreach (Pipeline pipeline in pipelines.Where(item => item.Active))
            {
                foreach (PipelineStage stage in pipeline.Stages)
                {
                    if (!personasByName.TryGetValue(stage.PersonaName, out Persona? persona) || !persona.Active)
                        issues.Add(new AssetIssue("error", "pipeline_persona", pipeline.Name, "Stage persona is missing or inactive: " + stage.PersonaName));
                }
            }

            foreach (Fleet fleet in fleets.Where(item => !String.IsNullOrWhiteSpace(item.DefaultPipelineId)))
            {
                if (!pipelinesById.TryGetValue(fleet.DefaultPipelineId!, out Pipeline? pipeline) || !pipeline.Active)
                    issues.Add(new AssetIssue("error", "fleet_pipeline", fleet.Name, "Default pipeline is missing or inactive: " + fleet.DefaultPipelineId));
            }
            foreach (Vessel vessel in vessels.Where(item => !String.IsNullOrWhiteSpace(item.DefaultPipelineId)))
            {
                if (!pipelinesById.TryGetValue(vessel.DefaultPipelineId!, out Pipeline? pipeline) || !pipeline.Active)
                    issues.Add(new AssetIssue("error", "vessel_pipeline", vessel.Name, "Default pipeline is missing or inactive: " + vessel.DefaultPipelineId));
            }

            foreach (WorkflowProfile profile in profiles)
            {
                WorkflowProfileValidationResult validation = await workflowProfiles.ValidateAsync(profile).ConfigureAwait(false);
                foreach (string error in validation.Errors)
                    issues.Add(new AssetIssue("error", "workflow_profile", profile.Name + " (" + profile.Id + ")", error));
                foreach (string warning in validation.Warnings)
                    issues.Add(new AssetIssue("warning", "workflow_profile", profile.Name + " (" + profile.Id + ")", warning));

                string commands = String.Join(" ", WorkflowProfileService.BuildCommandPreviews(profile).Select(item => item.Command));
                if (commands.Contains("%CD%", StringComparison.OrdinalIgnoreCase)
                    || commands.Contains(".\\", StringComparison.Ordinal)
                    || commands.Contains("gradlew.bat", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new AssetIssue("warning", "workflow_platform", profile.Name + " (" + profile.Id + ")", "Profile contains Windows command syntax; verify the execution host."));
                }
            }

            foreach (Vessel vessel in vessels.Where(item => item.Active))
            {
                WorkflowProfileResolutionPreviewResult? preview = await workflowProfiles.PreviewForVesselAsync(auth, vessel).ConfigureAwait(false);
                if (preview == null)
                    issues.Add(new AssetIssue("warning", "workflow_coverage", vessel.Name, "No active workflow profile resolves for this vessel."));
            }

            foreach (Runbook runbook in runbookPage.Objects)
            {
                if (!String.IsNullOrWhiteSpace(runbook.WorkflowProfileId) && !profilesById.ContainsKey(runbook.WorkflowProfileId))
                    issues.Add(new AssetIssue("error", "runbook_workflow", runbook.FileName, "Workflow profile does not exist: " + runbook.WorkflowProfileId));
                if (!String.IsNullOrWhiteSpace(runbook.EnvironmentId) && !environmentsById.ContainsKey(runbook.EnvironmentId))
                    issues.Add(new AssetIssue("error", "runbook_environment", runbook.FileName, "Environment does not exist: " + runbook.EnvironmentId));
                if (runbook.Steps.Count == 0)
                    issues.Add(new AssetIssue("warning", "runbook_steps", runbook.FileName, "Runbook has no steps."));
            }

            foreach (Playbook playbook in playbooks.Where(item => item.Active && item.FileName.StartsWith("sp-", StringComparison.OrdinalIgnoreCase)))
            {
                if (ContainsAny(playbook.Content, "subagent", "worktree", "separate session", "Skill tool"))
                    issues.Add(new AssetIssue("warning", "captain_capability", playbook.FileName, "Playbook names capabilities that Armada captains do not receive."));
            }

            return new
            {
                Counts = new
                {
                    Playbooks = playbooks.Count,
                    ActivePlaybooks = playbooks.Count(item => item.Active),
                    Runbooks = runbookPage.TotalRecords,
                    Personas = personas.Count,
                    Pipelines = pipelines.Count,
                    PromptTemplates = templates.Count,
                    WorkflowProfiles = profiles.Count,
                    Environments = environmentPage.TotalRecords,
                    Fleets = fleets.Count,
                    Vessels = vessels.Count,
                    Captains = captains.Count
                },
                Healthy = issues.All(item => item.Severity != "error"),
                ErrorCount = issues.Count(item => item.Severity == "error"),
                WarningCount = issues.Count(item => item.Severity == "warning"),
                Issues = issues
            };
        }

        private static void AddDefaultPlaybookIssues(
            List<AssetIssue> issues,
            string ownerType,
            IEnumerable<(string Name, List<SelectedPlaybook> Playbooks)> owners,
            IReadOnlyDictionary<string, Playbook> playbooksById)
        {
            foreach ((string name, List<SelectedPlaybook> defaults) in owners)
            {
                foreach (SelectedPlaybook selected in defaults)
                {
                    if (!playbooksById.TryGetValue(selected.PlaybookId, out Playbook? playbook))
                        issues.Add(new AssetIssue("error", "default_playbook", ownerType + ":" + name, "Default playbook does not exist: " + selected.PlaybookId));
                    else if (!playbook.Active)
                        issues.Add(new AssetIssue("warning", "default_playbook", ownerType + ":" + name, "Default playbook is inactive: " + playbook.FileName));
                }
            }
        }

        private static bool ContainsAny(string content, params string[] values)
        {
            return values.Any(value => content.Contains(value, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class AssetIssue
        {
            public AssetIssue(string severity, string category, string owner, string message)
            {
                Severity = severity;
                Category = category;
                Owner = owner;
                Message = message;
            }

            public string Severity { get; }
            public string Category { get; }
            public string Owner { get; }
            public string Message { get; }
        }
    }
}
