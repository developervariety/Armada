namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST API routes for objective and intake-style scoping records.
    /// </summary>
    public class ObjectiveRoutes
    {
        private readonly ObjectiveService _Objectives;
        private readonly GitHubIntegrationService _GitHub;
        private readonly ObjectiveDispatchPreviewService _DispatchPreview;
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ObjectiveRoutes(
            ObjectiveService objectives,
            GitHubIntegrationService gitHub,
            ObjectiveDispatchPreviewService dispatchPreview)
        {
            _Objectives = objectives ?? throw new ArgumentNullException(nameof(objectives));
            _GitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
            _DispatchPreview = dispatchPreview ?? throw new ArgumentNullException(nameof(dispatchPreview));
        }

        /// <summary>
        /// Register routes.
        /// </summary>
        public void Register(
            Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/objectives", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                ObjectiveQuery query = BuildQueryFromRequest(req);
                return await _Objectives.EnumerateAsync(ctx, query).ConfigureAwait(false);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("List objectives")
                .WithDescription("Returns paginated objective and intake-style records tied to repositories, planning, releases, deployments, and incidents.")
                .WithParameter(OpenApiParameterMetadata.Query("pageNumber", "One-based page number", false, OpenApiSchemaMetadata.Integer()))
                .WithParameter(OpenApiParameterMetadata.Query("pageSize", "Page size", false, OpenApiSchemaMetadata.Integer()))
                .WithParameter(OpenApiParameterMetadata.Query("owner", "Optional owner filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("category", "Optional category filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("parentObjectiveId", "Optional parent objective filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("vesselId", "Optional vessel filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("fleetId", "Optional fleet filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("planningSessionId", "Optional planning-session filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("voyageId", "Optional voyage filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("missionId", "Optional mission filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("checkRunId", "Optional check-run filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("releaseId", "Optional release filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("deploymentId", "Optional deployment filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("incidentId", "Optional incident filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("tag", "Optional tag filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("status", "Optional status filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("backlogState", "Optional backlog-state filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("kind", "Optional kind filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("priority", "Optional priority filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("effort", "Optional effort filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("targetVersion", "Optional target-version filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("search", "Optional free-text search", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Objective>>("Paginated objectives"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/objectives/enumerate", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                ObjectiveQuery query = JsonSerializer.Deserialize<ObjectiveQuery>(req.Http.Request.DataAsString, _JsonOptions) ?? new ObjectiveQuery();
                ApplyQuerystringOverrides(req, query);
                return await _Objectives.EnumerateAsync(ctx, query).ConfigureAwait(false);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Enumerate objectives")
                .WithDescription("Paginated objective enumeration using a JSON body and optional querystring overrides.")
                .WithRequestBody(OpenApiJson.BodyFor<ObjectiveQuery>("Objective query", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Objective>>("Paginated objectives"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/objectives/reorder", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                ObjectiveReorderRequest request = JsonSerializer.Deserialize<ObjectiveReorderRequest>(req.Http.Request.DataAsString, _JsonOptions)
                    ?? new ObjectiveReorderRequest();

                try
                {
                    return await _Objectives.ReorderAsync(ctx, request).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Reorder objectives")
                .WithDescription("Applies one or more explicit backlog rank updates.")
                .WithRequestBody(OpenApiJson.BodyFor<ObjectiveReorderRequest>("Objective reorder request", true))
                .WithResponse(200, OpenApiJson.For<List<Objective>>("Updated objectives"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/objectives/{id}", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                Objective? objective = await _Objectives.ReadAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                if (objective == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Objective not found" };
                }

                return objective;
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Get an objective")
                .WithDescription("Returns one objective or intake-style record by ID.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Objective ID (obj_ prefix)"))
                .WithResponse(200, OpenApiJson.For<Objective>("Objective"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/objectives/{id}/dispatch-preview", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                Objective? objective = await _Objectives.ReadAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                if (objective == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Objective not found" };
                }

                try
                {
                    return await _DispatchPreview.PreviewAsync(
                        ctx,
                        objective,
                        NormalizeEmpty(QueryValueReader.Read(req, "vesselId")),
                        NormalizeEmpty(QueryValueReader.Read(req, "pipelineId")),
                        ParseCaptainAssignments(QueryValueReader.Read(req, "captainAssignments"))).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Invalid captainAssignments JSON: " + ex.Message };
                }
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Preview objective dispatch readiness")
                .WithDescription("Returns a read-only preview of the effective target, pipeline, captain coverage, verification, provisioning, dependencies, and brief for one objective dispatch.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Objective ID (obj_ prefix)"))
                .WithParameter(OpenApiParameterMetadata.Query("vesselId", "Optional target vessel override", false))
                .WithParameter(OpenApiParameterMetadata.Query("pipelineId", "Optional pipeline ID or name override", false))
                .WithParameter(OpenApiParameterMetadata.Query("captainAssignments", "Optional JSON array of captain routing overrides", false))
                .WithResponse(200, OpenApiJson.For<ObjectiveDispatchPreview>("Objective dispatch preview"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/objectives", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                return await CreateObjectiveAsync(req, ctx).ConfigureAwait(false);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Create an objective")
                .WithDescription("Creates an internal-first objective or intake record with linked repositories, planning, releases, deployments, and incidents.")
                .WithRequestBody(OpenApiJson.BodyFor<ObjectiveUpsertRequest>("Objective create request", true))
                .WithResponse(201, OpenApiJson.For<Objective>("Created objective"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/objectives/import/github", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                GitHubObjectiveImportRequest request = JsonSerializer.Deserialize<GitHubObjectiveImportRequest>(req.Http.Request.DataAsString, _JsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as GitHubObjectiveImportRequest.");

                try
                {
                    Objective objective = await _GitHub.ImportObjectiveAsync(ctx, request).ConfigureAwait(false);
                    req.Http.Response.StatusCode = String.IsNullOrWhiteSpace(request.ObjectiveId) ? 201 : 200;
                    return objective;
                }
                catch (InvalidOperationException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Import an objective from GitHub")
                .WithDescription("Imports or refreshes an objective from a GitHub issue or pull request using the vessel repository URL and resolved GitHub token.")
                .WithRequestBody(OpenApiJson.BodyFor<GitHubObjectiveImportRequest>("GitHub objective import request", true))
                .WithResponse(200, OpenApiJson.For<Objective>("Updated objective"))
                .WithResponse(201, OpenApiJson.For<Objective>("Created objective"))
                .WithSecurity("ApiKey"));

            app.Put("/api/v1/objectives/{id}", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                return await UpdateObjectiveAsync(req, ctx).ConfigureAwait(false);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Update an objective")
                .WithDescription("Updates scope, acceptance criteria, linked entities, and current status for an objective. The preparation field is a complete replacement; send every source, target, and claim value that must remain.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Objective ID (obj_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<ObjectiveUpsertRequest>("Objective update request", true))
                .WithResponse(200, OpenApiJson.For<Objective>("Updated objective"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/objectives/{id}", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                RecordWriteResult<Objective> result = await _Objectives.DeleteRecordAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 204);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Delete an objective")
                .WithDescription("Deletes one objective and its snapshot chain.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Objective ID (obj_ prefix)"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/backlog", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                ObjectiveQuery query = BuildQueryFromRequest(req);
                return await _Objectives.EnumerateAsync(ctx, query).ConfigureAwait(false);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("List backlog items")
                .WithDescription("Returns paginated backlog items using the user-facing backlog terminology.")
                .WithParameter(OpenApiParameterMetadata.Query("pageNumber", "One-based page number", false, OpenApiSchemaMetadata.Integer()))
                .WithParameter(OpenApiParameterMetadata.Query("pageSize", "Page size", false, OpenApiSchemaMetadata.Integer()))
                .WithParameter(OpenApiParameterMetadata.Query("owner", "Optional owner filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("category", "Optional category filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("parentObjectiveId", "Optional parent objective filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("vesselId", "Optional vessel filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("fleetId", "Optional fleet filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("planningSessionId", "Optional planning-session filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("voyageId", "Optional voyage filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("missionId", "Optional mission filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("checkRunId", "Optional check-run filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("releaseId", "Optional release filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("deploymentId", "Optional deployment filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("incidentId", "Optional incident filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("tag", "Optional tag filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("status", "Optional status filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("backlogState", "Optional backlog-state filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("kind", "Optional kind filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("priority", "Optional priority filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("effort", "Optional effort filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("targetVersion", "Optional target-version filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("search", "Optional free-text search", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Objective>>("Paginated backlog items"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/backlog/enumerate", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                ObjectiveQuery query = JsonSerializer.Deserialize<ObjectiveQuery>(req.Http.Request.DataAsString, _JsonOptions) ?? new ObjectiveQuery();
                ApplyQuerystringOverrides(req, query);
                return await _Objectives.EnumerateAsync(ctx, query).ConfigureAwait(false);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Enumerate backlog items")
                .WithDescription("Paginated backlog enumeration using a JSON body and optional querystring overrides.")
                .WithRequestBody(OpenApiJson.BodyFor<ObjectiveQuery>("Backlog query", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Objective>>("Paginated backlog items"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/backlog/reorder", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                ObjectiveReorderRequest request = JsonSerializer.Deserialize<ObjectiveReorderRequest>(req.Http.Request.DataAsString, _JsonOptions)
                    ?? new ObjectiveReorderRequest();

                try
                {
                    return await _Objectives.ReorderAsync(ctx, request).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Reorder backlog items")
                .WithDescription("Applies one or more explicit backlog rank updates using backlog terminology.")
                .WithRequestBody(OpenApiJson.BodyFor<ObjectiveReorderRequest>("Backlog reorder request", true))
                .WithResponse(200, OpenApiJson.For<List<Objective>>("Updated backlog items"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/backlog/{id}", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                Objective? objective = await _Objectives.ReadAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                if (objective == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Backlog item not found" };
                }

                return objective;
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Get a backlog item")
                .WithDescription("Returns one backlog item by ID.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Backlog item ID (obj_ prefix)"))
                .WithResponse(200, OpenApiJson.For<Objective>("Backlog item"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/backlog/{id}/dispatch-preview", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                Objective? objective = await _Objectives.ReadAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                if (objective == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Backlog item not found" };
                }

                try
                {
                    return await _DispatchPreview.PreviewAsync(
                        ctx,
                        objective,
                        NormalizeEmpty(QueryValueReader.Read(req, "vesselId")),
                        NormalizeEmpty(QueryValueReader.Read(req, "pipelineId")),
                        ParseCaptainAssignments(QueryValueReader.Read(req, "captainAssignments"))).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Invalid captainAssignments JSON: " + ex.Message };
                }
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Preview backlog item dispatch readiness")
                .WithDescription("Returns the same read-only objective dispatch preview through the backlog alias.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Backlog item ID (obj_ prefix)"))
                .WithParameter(OpenApiParameterMetadata.Query("vesselId", "Optional target vessel override", false))
                .WithParameter(OpenApiParameterMetadata.Query("pipelineId", "Optional pipeline ID or name override", false))
                .WithParameter(OpenApiParameterMetadata.Query("captainAssignments", "Optional JSON array of captain routing overrides", false))
                .WithResponse(200, OpenApiJson.For<ObjectiveDispatchPreview>("Backlog item dispatch preview"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/backlog", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                return await CreateObjectiveAsync(req, ctx).ConfigureAwait(false);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Create a backlog item")
                .WithDescription("Creates a backlog item using the user-facing backlog terminology.")
                .WithRequestBody(OpenApiJson.BodyFor<ObjectiveUpsertRequest>("Backlog create request", true))
                .WithResponse(201, OpenApiJson.For<Objective>("Created backlog item"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithSecurity("ApiKey"));

            app.Put("/api/v1/backlog/{id}", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                return await UpdateObjectiveAsync(req, ctx).ConfigureAwait(false);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Update a backlog item")
                .WithDescription("Updates backlog scope, rank, prioritization, and linked entities.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Backlog item ID (obj_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<ObjectiveUpsertRequest>("Backlog update request", true))
                .WithResponse(200, OpenApiJson.For<Objective>("Updated backlog item"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/backlog/{id}", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return RouteAuthRefusal.FromStatus(req);
                RecordWriteResult<Objective> result = await _Objectives.DeleteRecordAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 204);
            },
            api => api
                .WithTag("Objectives")
                .WithSummary("Delete a backlog item")
                .WithDescription("Deletes one backlog item and its snapshot chain.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Backlog item ID (obj_ prefix)"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }

        /// <summary>
        /// Create an objective through the shared write rule. A body that is not valid JSON for the request,
        /// such as an unknown enum value, is refused with 400.
        /// </summary>
        private async Task<object?> CreateObjectiveAsync(ApiRequest req, AuthContext ctx)
        {
            if (!RecordWriteResponse.TryReadBody(req, _JsonOptions, out ObjectiveUpsertRequest? request, out object? refusal)) return refusal;
            RecordWriteResult<Objective> result = await _Objectives.CreateRecordAsync(ctx, request).ConfigureAwait(false);
            return RecordWriteResponse.From(req, result, 201);
        }

        /// <summary>
        /// Update an objective through the shared write rule: 404 when the objective is not found, 400 for an
        /// invalid field or a linked record outside the caller's scope.
        /// </summary>
        private async Task<object?> UpdateObjectiveAsync(ApiRequest req, AuthContext ctx)
        {
            if (!RecordWriteResponse.TryReadBody(req, _JsonOptions, out ObjectiveUpsertRequest? request, out object? refusal)) return refusal;
            RecordWriteResult<Objective> result = await _Objectives.UpdateRecordAsync(ctx, req.Parameters["id"], request).ConfigureAwait(false);
            return RecordWriteResponse.From(req, result, 200);
        }

        private static ObjectiveQuery BuildQueryFromRequest(ApiRequest req)
        {
            ObjectiveQuery query = new ObjectiveQuery();
            ApplyQuerystringOverrides(req, query);
            return query;
        }

        private static void ApplyQuerystringOverrides(ApiRequest req, ObjectiveQuery query)
        {
            if (Int32.TryParse(QueryValueReader.Read(req, "pageNumber"), out int pageNumber))
                query.PageNumber = Math.Max(1, pageNumber);
            if (Int32.TryParse(QueryValueReader.Read(req, "pageSize"), out int pageSize))
                query.PageSize = Math.Clamp(pageSize, 1, 500);
            if (DateTime.TryParse(QueryValueReader.Read(req, "fromUtc"), out DateTime fromUtc))
                query.FromUtc = fromUtc.ToUniversalTime();
            if (DateTime.TryParse(QueryValueReader.Read(req, "toUtc"), out DateTime toUtc))
                query.ToUtc = toUtc.ToUniversalTime();
            if (Enum.TryParse(QueryValueReader.Read(req, "status"), true, out ObjectiveStatusEnum status))
                query.Status = status;
            if (Enum.TryParse(QueryValueReader.Read(req, "backlogState"), true, out ObjectiveBacklogStateEnum backlogState))
                query.BacklogState = backlogState;
            if (Enum.TryParse(QueryValueReader.Read(req, "kind"), true, out ObjectiveKindEnum kind))
                query.Kind = kind;
            if (Enum.TryParse(QueryValueReader.Read(req, "priority"), true, out ObjectivePriorityEnum priority))
                query.Priority = priority;
            if (Enum.TryParse(QueryValueReader.Read(req, "effort"), true, out ObjectiveEffortEnum effort))
                query.Effort = effort;

            query.Owner = NormalizeEmpty(QueryValueReader.Read(req, "owner")) ?? query.Owner;
            query.Category = NormalizeEmpty(QueryValueReader.Read(req, "category")) ?? query.Category;
            query.ParentObjectiveId = NormalizeEmpty(QueryValueReader.Read(req, "parentObjectiveId")) ?? query.ParentObjectiveId;
            query.VesselId = NormalizeEmpty(QueryValueReader.Read(req, "vesselId")) ?? query.VesselId;
            query.FleetId = NormalizeEmpty(QueryValueReader.Read(req, "fleetId")) ?? query.FleetId;
            query.PlanningSessionId = NormalizeEmpty(QueryValueReader.Read(req, "planningSessionId")) ?? query.PlanningSessionId;
            query.VoyageId = NormalizeEmpty(QueryValueReader.Read(req, "voyageId")) ?? query.VoyageId;
            query.MissionId = NormalizeEmpty(QueryValueReader.Read(req, "missionId")) ?? query.MissionId;
            query.CheckRunId = NormalizeEmpty(QueryValueReader.Read(req, "checkRunId")) ?? query.CheckRunId;
            query.ReleaseId = NormalizeEmpty(QueryValueReader.Read(req, "releaseId")) ?? query.ReleaseId;
            query.DeploymentId = NormalizeEmpty(QueryValueReader.Read(req, "deploymentId")) ?? query.DeploymentId;
            query.IncidentId = NormalizeEmpty(QueryValueReader.Read(req, "incidentId")) ?? query.IncidentId;
            query.Tag = NormalizeEmpty(QueryValueReader.Read(req, "tag")) ?? query.Tag;
            query.TargetVersion = NormalizeEmpty(QueryValueReader.Read(req, "targetVersion")) ?? query.TargetVersion;
            query.Search = NormalizeEmpty(QueryValueReader.Read(req, "search")) ?? query.Search;
        }

        private static async Task<AuthContext?> AuthorizeAsync(
            ApiRequest req,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
            if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
            {
                req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                return null;
            }

            return ctx;
        }

        private static string? NormalizeEmpty(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static List<CaptainAssignmentOverride>? ParseCaptainAssignments(string? value)
        {
            string? json = NormalizeEmpty(value);
            return json == null
                ? null
                : JsonSerializer.Deserialize<List<CaptainAssignmentOverride>>(json, _JsonOptions)
                    ?? new List<CaptainAssignmentOverride>();
        }
    }
}
