namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for deployment inspection and bounded actions.
    /// </summary>
    public static class McpDeploymentTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers deployment MCP tools.
        /// </summary>
        public static void Register(RegisterToolDelegate register, DeploymentService deploymentService)
        {
            register(
                "get_deployment",
                "Inspect one deployment including approval, verification, rollback, linked checks, and request-history evidence.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        deploymentId = new { type = "string", description = "Deployment ID (dpl_ prefix)" }
                    },
                    required = new[] { "deploymentId" }
                },
                async (args) =>
                {
                    DeploymentIdArgs request = JsonSerializer.Deserialize<DeploymentIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize DeploymentIdArgs.");
                    AuthContext auth = McpCallerContext.Require();
                    Deployment? deployment = await deploymentService.ReadAsync(auth, request.DeploymentId).ConfigureAwait(false);
                    if (deployment == null) return (object)new { Error = "Deployment not found" };
                    return (object)deployment;
                });

            register(
                "create_deployment",
                "Create a bounded deployment record and execute it immediately when approval is not required.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Optional vessel ID (vsl_ prefix)" },
                        workflowProfileId = new { type = "string", description = "Optional workflow profile override (wfp_ prefix)" },
                        environmentId = new { type = "string", description = "Optional environment ID (env_ prefix)" },
                        environmentName = new { type = "string", description = "Optional environment name" },
                        releaseId = new { type = "string", description = "Optional linked release ID (rel_ prefix)" },
                        missionId = new { type = "string", description = "Optional linked mission ID (msn_ prefix)" },
                        voyageId = new { type = "string", description = "Optional linked voyage ID (vyg_ prefix)" },
                        title = new { type = "string", description = "Optional deployment title" },
                        sourceRef = new { type = "string", description = "Optional source ref such as a branch, tag, or commit; resolved once to the full commit the deployment ships" },
                        artifactIdentity = new { type = "string", description = "Optional identity of the artifact the deployment ships, such as an image digest or package version" },
                        summary = new { type = "string", description = "Optional short summary" },
                        notes = new { type = "string", description = "Optional operator notes" },
                        autoExecute = new { type = "boolean", description = "Whether to execute immediately when approval is not required" }
                    }
                },
                async (args) =>
                {
                    DeploymentUpsertRequest request = JsonSerializer.Deserialize<DeploymentUpsertRequest>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize DeploymentUpsertRequest.");
                    AuthContext auth = McpCallerContext.Require();
                    return (object)await deploymentService.CreateAsync(auth, request).ConfigureAwait(false);
                });

            object updateDeploymentSchema = new
            {
                type = "object",
                properties = new
                {
                    deploymentId = new { type = "string", description = "Deployment ID (dpl_ prefix)" },
                    vesselId = new { type = "string", description = "Optional vessel ID (vsl_ prefix)" },
                    workflowProfileId = new { type = "string", description = "Optional workflow profile override (wfp_ prefix)" },
                    environmentId = new { type = "string", description = "Optional environment ID (env_ prefix)" },
                    environmentName = new { type = "string", description = "Optional environment name" },
                    releaseId = new { type = "string", description = "Optional linked release ID (rel_ prefix)" },
                    missionId = new { type = "string", description = "Optional linked mission ID (msn_ prefix)" },
                    voyageId = new { type = "string", description = "Optional linked voyage ID (vyg_ prefix)" },
                    title = new { type = "string", description = "Optional deployment title" },
                    sourceRef = new { type = "string", description = "Optional source ref; a changed ref is resolved again, refused once the deployment was approved or executed" },
                    artifactIdentity = new { type = "string", description = "Optional artifact identity; refused once the deployment was approved or executed" },
                    summary = new { type = "string", description = "Optional short summary" },
                    notes = new { type = "string", description = "Optional operator notes" },
                    autoExecute = new { type = "boolean", description = "Whether to execute immediately when approval is not required" }
                },
                required = new[] { "deploymentId" }
            };

            Func<JsonElement?, Task<object>> updateDeploymentHandler = async (args) =>
            {
                DeploymentUpdateArgs request = JsonSerializer.Deserialize<DeploymentUpdateArgs>(args!.Value, _JsonOptions)
                    ?? throw new InvalidOperationException("Could not deserialize DeploymentUpdateArgs.");
                AuthContext auth = McpCallerContext.Require();
                try
                {
                    return (object)await deploymentService.UpdateAsync(auth, request.DeploymentId, request.ToUpsertRequest()).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is ArgumentException)
                {
                    return (object)new { Error = ex.Message };
                }
            };

            // update_deployment is the family-consistent name (matches create_deployment / get_deployment).
            // armada_update_deployment is kept as a back-compat alias (2026-07-26 prefix normalization).
            register("update_deployment", "Update one deployment record's metadata (no status field; approve/verify/rollback stay dedicated tools).", updateDeploymentSchema, updateDeploymentHandler);
            register("armada_update_deployment", "Alias of update_deployment (back-compat).", updateDeploymentSchema, updateDeploymentHandler);

            register(
                "approve_deployment",
                "Approve a pending deployment and begin execution. Name the reviewed candidate: approval refuses when the deployment names a different commit or artifact, or when the environment's required Checks have not passed at the pinned commit.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        deploymentId = new { type = "string", description = "Deployment ID (dpl_ prefix)" },
                        comment = new { type = "string", description = "Optional approval comment" },
                        expectedSourceCommit = new { type = "string", description = "Optional commit the approver reviewed: the full commit or a prefix of at least 7 characters" },
                        expectedArtifactIdentity = new { type = "string", description = "Optional artifact identity the approver reviewed" }
                    },
                    required = new[] { "deploymentId" }
                },
                async (args) =>
                {
                    DeploymentApprovalArgs request = JsonSerializer.Deserialize<DeploymentApprovalArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize DeploymentApprovalArgs.");
                    AuthContext auth = McpCallerContext.Require();
                    return (object)await deploymentService.ApproveAsync(
                        auth,
                        request.DeploymentId,
                        request.Comment,
                        request.ExpectedSourceCommit,
                        request.ExpectedArtifactIdentity).ConfigureAwait(false);
                });

            register(
                "verify_deployment",
                "Run post-deploy verification for an existing deployment.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        deploymentId = new { type = "string", description = "Deployment ID (dpl_ prefix)" }
                    },
                    required = new[] { "deploymentId" }
                },
                async (args) =>
                {
                    DeploymentIdArgs request = JsonSerializer.Deserialize<DeploymentIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize DeploymentIdArgs.");
                    AuthContext auth = McpCallerContext.Require();
                    return (object)await deploymentService.VerifyAsync(auth, request.DeploymentId).ConfigureAwait(false);
                });

            register(
                "rollback_deployment",
                "Run the configured rollback flow for an existing deployment.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        deploymentId = new { type = "string", description = "Deployment ID (dpl_ prefix)" }
                    },
                    required = new[] { "deploymentId" }
                },
                async (args) =>
                {
                    DeploymentIdArgs request = JsonSerializer.Deserialize<DeploymentIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize DeploymentIdArgs.");
                    AuthContext auth = McpCallerContext.Require();
                    return (object)await deploymentService.RollbackAsync(auth, request.DeploymentId).ConfigureAwait(false);
                });
        }

        private sealed class DeploymentApprovalArgs
        {
            public string DeploymentId { get; set; } = "";

            public string? Comment { get; set; } = null;

            public string? ExpectedSourceCommit { get; set; } = null;

            public string? ExpectedArtifactIdentity { get; set; } = null;
        }

        private sealed class DeploymentUpdateArgs : DeploymentUpsertRequest
        {
            public string DeploymentId { get; set; } = "";

            public DeploymentUpsertRequest ToUpsertRequest()
            {
                return new DeploymentUpsertRequest
                {
                    VesselId = VesselId,
                    WorkflowProfileId = WorkflowProfileId,
                    EnvironmentId = EnvironmentId,
                    EnvironmentName = EnvironmentName,
                    ReleaseId = ReleaseId,
                    MissionId = MissionId,
                    VoyageId = VoyageId,
                    ObjectiveIds = ObjectiveIds,
                    Title = Title,
                    SourceRef = SourceRef,
                    ArtifactIdentity = ArtifactIdentity,
                    Summary = Summary,
                    Notes = Notes,
                    AutoExecute = AutoExecute
                };
            }
        }
    }
}
