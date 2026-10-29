namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Runtimes.Mcp;
    using McpToolCallResult = Armada.Runtimes.Mcp.McpToolCallResult;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Workflow-profile MCP tools (get, create, update, delete, delete many, validate, resolve, preview) and the REST
    /// batch delete: happy paths, partial updates, typed errors, tenant-admin-only writes, and tenant isolation. Every
    /// case creates its own tenants, so results are exact on the shared fixture.
    /// </summary>
    public sealed class WorkflowProfileMcpSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.WorkflowProfileMcp";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("mcp_crud_resolve_preview", "Create, get, partially update, validate, resolve, preview, and delete a workflow profile over MCP", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-crud", true).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(fx, admin, "wfm-crud").ConfigureAwait(false);

                using (McpToolClient mcp = Mcp(fx, admin))
                {
                    await mcp.InitializeAsync().ConfigureAwait(false);

                    WorkflowProfile created = await OkAsync<WorkflowProfile>(mcp, "create_workflow_profile", new
                    {
                        name = "MCP Workflow",
                        description = "created over MCP",
                        scope = "Vessel",
                        vesselId = vessel.Id,
                        languageHints = new[] { "csharp" },
                        buildCommand = "dotnet build",
                        unitTestCommand = "dotnet test",
                        lintCommand = "dotnet format --verify-no-changes",
                        expectedArtifacts = new[] { "bin/" },
                        environments = new[] { new { environmentName = "staging", deployCommand = "deploy.sh staging" } },
                        requiredInputs = new[] { new { provider = "EnvironmentVariable", key = "DEPLOY_TOKEN", environmentName = "staging" } }
                    }).ConfigureAwait(false);
                    AssertStartsWith("wfp_", created.Id);
                    AssertEqual(admin.TenantId, created.TenantId, "created in the caller's tenant");
                    AssertEqual(admin.UserId, created.UserId, "owned by the caller");
                    AssertEqual(WorkflowProfileScopeEnum.Vessel, created.Scope);
                    AssertEqual(ScopeEnum.TenantWide, created.OwnershipScope, "tenant admin default ownership");
                    AssertEqual("staging", created.Environments.Single().EnvironmentName);
                    AssertEqual("DEPLOY_TOKEN", created.RequiredInputs.Single().Key);

                    WorkflowProfile read = await OkAsync<WorkflowProfile>(mcp, "get_workflow_profile", new { id = created.Id }).ConfigureAwait(false);
                    AssertEqual("dotnet test", read.UnitTestCommand);

                    // Only the unit test command changes; everything else keeps its stored value.
                    WorkflowProfile updated = await OkAsync<WorkflowProfile>(mcp, "update_workflow_profile", new { id = created.Id, unitTestCommand = "dotnet test --no-build" }).ConfigureAwait(false);
                    AssertEqual("dotnet test --no-build", updated.UnitTestCommand);
                    WorkflowProfile afterUpdate = await OkAsync<WorkflowProfile>(mcp, "get_workflow_profile", new { id = created.Id }).ConfigureAwait(false);
                    AssertEqual("dotnet test --no-build", afterUpdate.UnitTestCommand, "get shows the updated unit test command");
                    AssertEqual("MCP Workflow", afterUpdate.Name, "name untouched");
                    AssertEqual("created over MCP", afterUpdate.Description, "description untouched");
                    AssertEqual("dotnet build", afterUpdate.BuildCommand, "build command untouched");
                    AssertEqual("dotnet format --verify-no-changes", afterUpdate.LintCommand, "lint command untouched");
                    AssertEqual(WorkflowProfileScopeEnum.Vessel, afterUpdate.Scope, "scope untouched");
                    AssertEqual(vessel.Id, afterUpdate.VesselId, "vessel untouched");
                    AssertTrue(afterUpdate.Active, "active untouched");
                    AssertEqual("csharp", afterUpdate.LanguageHints.Single(), "language hints untouched");
                    AssertEqual("staging", afterUpdate.Environments.Single().EnvironmentName, "environments untouched");
                    AssertEqual("DEPLOY_TOKEN", afterUpdate.RequiredInputs.Single().Key, "required inputs untouched");
                    AssertEqual("bin/", afterUpdate.ExpectedArtifacts.Single(), "expected artifacts untouched");

                    // An empty string clears a command; a boolean set to false is applied rather than ignored.
                    WorkflowProfile cleared = await OkAsync<WorkflowProfile>(mcp, "update_workflow_profile", new { id = created.Id, lintCommand = "", active = false, description = "edited" }).ConfigureAwait(false);
                    AssertNull(cleared.LintCommand, "empty string clears the lint command");
                    AssertFalse(cleared.Active, "active false applied");
                    AssertEqual("edited", cleared.Description);
                    AssertEqual("dotnet build", cleared.BuildCommand, "build command still untouched");
                    await OkAsync<WorkflowProfile>(mcp, "update_workflow_profile", new { id = created.Id, active = true }).ConfigureAwait(false);

                    WorkflowProfileValidationResult valid = await OkAsync<WorkflowProfileValidationResult>(mcp, "validate_workflow_profile", new { name = "draft", buildCommand = "make" }).ConfigureAwait(false);
                    AssertTrue(valid.IsValid, "a definition with a command is valid");
                    AssertContains("Build", String.Join(",", valid.AvailableCheckTypes));
                    WorkflowProfileValidationResult invalid = await OkAsync<WorkflowProfileValidationResult>(mcp, "validate_workflow_profile", new { name = "draft" }).ConfigureAwait(false);
                    AssertFalse(invalid.IsValid, "a definition without commands is invalid");
                    AssertTrue(invalid.Errors.Count > 0, "validation errors are returned");

                    WorkflowProfile resolved = await OkAsync<WorkflowProfile>(mcp, "resolve_workflow_profile", new { vesselId = vessel.Id, workflowProfileId = created.Id }).ConfigureAwait(false);
                    AssertEqual(created.Id, resolved.Id, "explicit override resolves");
                    WorkflowProfile implicitResolved = await OkAsync<WorkflowProfile>(mcp, "resolve_workflow_profile", new { vesselId = vessel.Id }).ConfigureAwait(false);
                    AssertStartsWith("wfp_", implicitResolved.Id, "a profile resolves without an override");

                    WorkflowProfileResolutionPreviewResult preview = await OkAsync<WorkflowProfileResolutionPreviewResult>(mcp, "preview_workflow_profile", new { vesselId = vessel.Id, workflowProfileId = created.Id }).ConfigureAwait(false);
                    AssertEqual(created.Id, preview.ResolvedProfile?.Id, "preview resolves the override");
                    AssertTrue(preview.CommandPreviews.Any(p => p.Command == "dotnet test --no-build"), "preview shows the updated unit test command");

                    await OkAsync<object>(mcp, "delete_workflow_profile", new { id = created.Id }).ConfigureAwait(false);
                    await ErrorAsync(mcp, "get_workflow_profile", new { id = created.Id }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                }
            }));

            cases.Add(CaseAsync("mcp_minimal_create_defaults", "A create with only a name and one command gets the model defaults", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-defaults", true).ConfigureAwait(false);
                using (McpToolClient mcp = Mcp(fx, admin))
                {
                    await mcp.InitializeAsync().ConfigureAwait(false);
                    WorkflowProfile created = await OkAsync<WorkflowProfile>(mcp, "create_workflow_profile", new { name = "Minimal", buildCommand = "make" }).ConfigureAwait(false);
                    AssertEqual(WorkflowProfileScopeEnum.Global, created.Scope, "default scope");
                    AssertTrue(created.Active, "default active");
                    AssertFalse(created.IsDefault, "default isDefault");
                    AssertEqual(ScopeEnum.TenantWide, created.OwnershipScope, "default ownership for a tenant admin");
                    AssertNull(created.FleetId);
                    AssertNull(created.VesselId);
                    AssertNull(created.UnitTestCommand);
                    AssertEqual(0, created.Environments.Count);
                    AssertEqual(0, created.RequiredInputs.Count);

                    // Setting isDefault on a second Global profile clears it on the first, as REST does.
                    WorkflowProfile first = await OkAsync<WorkflowProfile>(mcp, "create_workflow_profile", new { name = "Default A", buildCommand = "make", isDefault = true }).ConfigureAwait(false);
                    WorkflowProfile second = await OkAsync<WorkflowProfile>(mcp, "create_workflow_profile", new { name = "Default B", buildCommand = "make", isDefault = true }).ConfigureAwait(false);
                    AssertTrue(second.IsDefault);
                    AssertFalse((await OkAsync<WorkflowProfile>(mcp, "get_workflow_profile", new { id = first.Id }).ConfigureAwait(false)).IsDefault, "previous default cleared");
                }
            }));

            cases.Add(CaseAsync("mcp_errors", "Missing ids are NotFound; invalid definitions are InvalidArgument and leave the stored profile unchanged", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-errors", true).ConfigureAwait(false);
                using (McpToolClient mcp = Mcp(fx, admin))
                {
                    await mcp.InitializeAsync().ConfigureAwait(false);
                    await ErrorAsync(mcp, "get_workflow_profile", new { id = "wfp_missing" }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(mcp, "update_workflow_profile", new { id = "wfp_missing", buildCommand = "make" }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(mcp, "delete_workflow_profile", new { id = "wfp_missing" }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(mcp, "resolve_workflow_profile", new { vesselId = "vsl_missing" }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(mcp, "preview_workflow_profile", new { vesselId = "vsl_missing" }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(mcp, "delete_workflow_profiles", new { ids = Array.Empty<string>() }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);
                    await ErrorAsync(mcp, "get_workflow_profile", new { id = "  " }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);

                    await ErrorAsync(mcp, "create_workflow_profile", new { name = "No commands" }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);
                    await ErrorAsync(mcp, "create_workflow_profile", new { name = "No vessel", scope = "Vessel", buildCommand = "make" }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);
                    // The schema enum is enforced before the handler runs, so a bad scope is refused as plain text.
                    McpToolCallResult badScope = await mcp.CallToolResultAsync("create_workflow_profile", JsonHelper.Serialize(new { name = "Bad scope", scope = "Galaxy", buildCommand = "make" })).ConfigureAwait(false);
                    AssertContains("must be one of the allowed values", badScope.Text, "an unknown scope is refused");
                    await ErrorAsync(mcp, "create_workflow_profile", new { name = "  ", buildCommand = "make" }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);
                    await ErrorAsync(mcp, "create_workflow_profile", new { name = "Missing fleet", fleetId = "flt_missing", buildCommand = "make" }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);

                    WorkflowProfile created = await OkAsync<WorkflowProfile>(mcp, "create_workflow_profile", new { name = "Stable", buildCommand = "make" }).ConfigureAwait(false);
                    await ErrorAsync(mcp, "update_workflow_profile", new { id = created.Id, buildCommand = "" }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);
                    await ErrorAsync(mcp, "update_workflow_profile", new { id = created.Id, name = "" }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);
                    WorkflowProfile unchanged = await OkAsync<WorkflowProfile>(mcp, "get_workflow_profile", new { id = created.Id }).ConfigureAwait(false);
                    AssertEqual("make", unchanged.BuildCommand, "a rejected update leaves the stored profile unchanged");
                    AssertEqual("Stable", unchanged.Name, "a rejected update leaves the name unchanged");

                    McpToolCallResult missingRequired = await mcp.CallToolResultAsync("update_workflow_profile", JsonHelper.Serialize(new { buildCommand = "make" })).ConfigureAwait(false);
                    AssertFalse(missingRequired.Text.Contains("\"id\":\"" + created.Id, StringComparison.Ordinal), "a call without id changes nothing");
                }
            }));

            cases.Add(CaseAsync("mcp_non_admin_writes_rejected", "A regular user reads workflow profiles but every write and validate is Forbidden, as over REST", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-auth-admin", true).ConfigureAwait(false);
                E2ETenantUser user = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-auth-user", false, admin.TenantId).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(fx, admin, "wfm-auth").ConfigureAwait(false);

                using (McpToolClient adminMcp = Mcp(fx, admin))
                using (McpToolClient userMcp = Mcp(fx, user))
                {
                    await adminMcp.InitializeAsync().ConfigureAwait(false);
                    await userMcp.InitializeAsync().ConfigureAwait(false);
                    WorkflowProfile created = await OkAsync<WorkflowProfile>(adminMcp, "create_workflow_profile", new { name = "Tenant wide", buildCommand = "make" }).ConfigureAwait(false);

                    AssertEqual(created.Id, (await OkAsync<WorkflowProfile>(userMcp, "get_workflow_profile", new { id = created.Id }).ConfigureAwait(false)).Id, "a regular user reads a tenant-wide profile");
                    // As over REST, a regular user resolves only against vessels they own; the admin's vessel is not visible.
                    await ErrorAsync(userMcp, "resolve_workflow_profile", new { vesselId = vessel.Id, workflowProfileId = created.Id }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);

                    await ErrorAsync(userMcp, "create_workflow_profile", new { name = "Mine", buildCommand = "make" }, McpToolErrorCodeEnum.Forbidden).ConfigureAwait(false);
                    await ErrorAsync(userMcp, "update_workflow_profile", new { id = created.Id, unitTestCommand = "hijack" }, McpToolErrorCodeEnum.Forbidden).ConfigureAwait(false);
                    await ErrorAsync(userMcp, "delete_workflow_profile", new { id = created.Id }, McpToolErrorCodeEnum.Forbidden).ConfigureAwait(false);
                    await ErrorAsync(userMcp, "delete_workflow_profiles", new { ids = new[] { created.Id } }, McpToolErrorCodeEnum.Forbidden).ConfigureAwait(false);
                    await ErrorAsync(userMcp, "validate_workflow_profile", new { name = "draft", buildCommand = "make" }, McpToolErrorCodeEnum.Forbidden).ConfigureAwait(false);

                    WorkflowProfile after = await OkAsync<WorkflowProfile>(adminMcp, "get_workflow_profile", new { id = created.Id }).ConfigureAwait(false);
                    AssertNull(after.UnitTestCommand, "the refused update changed nothing");
                }
            }));

            cases.Add(CaseAsync("mcp_tenant_isolation", "Another tenant's admin cannot read, change, delete, or reference tenant A's profiles and vessels", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser adminA = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-iso-a", true).ConfigureAwait(false);
                E2ETenantUser adminB = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-iso-b", true).ConfigureAwait(false);
                Vessel vesselA = await CreateVesselAsync(fx, adminA, "wfm-iso-a").ConfigureAwait(false);

                using (McpToolClient a = Mcp(fx, adminA))
                using (McpToolClient b = Mcp(fx, adminB))
                {
                    await a.InitializeAsync().ConfigureAwait(false);
                    await b.InitializeAsync().ConfigureAwait(false);
                    WorkflowProfile profileA = await OkAsync<WorkflowProfile>(a, "create_workflow_profile", new { name = "Tenant A", scope = "Vessel", vesselId = vesselA.Id, unitTestCommand = "secret-a" }).ConfigureAwait(false);

                    await ErrorAsync(b, "get_workflow_profile", new { id = profileA.Id }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(b, "update_workflow_profile", new { id = profileA.Id, unitTestCommand = "hijack" }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(b, "delete_workflow_profile", new { id = profileA.Id }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(b, "resolve_workflow_profile", new { vesselId = vesselA.Id }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(b, "preview_workflow_profile", new { vesselId = vesselA.Id, workflowProfileId = profileA.Id }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(b, "create_workflow_profile", new { name = "Foreign", scope = "Vessel", vesselId = vesselA.Id, buildCommand = "make" }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);
                    await ErrorAsync(b, "validate_workflow_profile", new { name = "Foreign", scope = "Vessel", vesselId = vesselA.Id, buildCommand = "make" }, McpToolErrorCodeEnum.InvalidArgument).ConfigureAwait(false);

                    DeleteMultipleResult skipped = await OkAsync<DeleteMultipleResult>(b, "delete_workflow_profiles", new { ids = new[] { profileA.Id } }).ConfigureAwait(false);
                    AssertEqual(0, skipped.Deleted, "another tenant's profile is not deleted");
                    AssertEqual("Not found", skipped.Skipped.Single().Reason);

                    WorkflowProfile intact = await OkAsync<WorkflowProfile>(a, "get_workflow_profile", new { id = profileA.Id }).ConfigureAwait(false);
                    AssertEqual("secret-a", intact.UnitTestCommand, "tenant A's profile is unchanged");

                    DeleteMultipleResult deleted = await OkAsync<DeleteMultipleResult>(a, "delete_workflow_profiles", new { ids = new[] { profileA.Id, "wfp_missing", "" } }).ConfigureAwait(false);
                    AssertEqual(1, deleted.Deleted, "the owner deletes its own profile");
                    AssertEqual(2, deleted.Skipped.Count, "missing and empty ids are skipped");
                    AssertEqual(DeleteMultipleStatusEnum.PartiallyDeleted, deleted.Status);
                }
            }));

            cases.Add(CaseAsync("rest_batch_delete", "POST /workflow-profiles/delete/multiple deletes visible profiles, skips the rest, and enforces tenant admin", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-rest-admin", true).ConfigureAwait(false);
                E2ETenantUser user = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-rest-user", false, admin.TenantId).ConfigureAwait(false);
                E2ETenantUser other = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-rest-other", true).ConfigureAwait(false);

                using (HttpClient adminHttp = admin.CreateClient(fx.BaseUrl))
                using (HttpClient userHttp = user.CreateClient(fx.BaseUrl))
                using (HttpClient otherHttp = other.CreateClient(fx.BaseUrl))
                {
                    HttpResponseMessage createResponse = await adminHttp.PostAsync("/api/v1/workflow-profiles", JsonHelper.ToJsonContent(new { Name = "Batch", BuildCommand = "make" })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.Created, createResponse, "create");
                    WorkflowProfile created = await JsonHelper.DeserializeAsync<WorkflowProfile>(createResponse).ConfigureAwait(false);

                    HttpResponseMessage empty = await adminHttp.PostAsync("/api/v1/workflow-profiles/delete/multiple", JsonHelper.ToJsonContent(new { Ids = Array.Empty<string>() })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.BadRequest, empty, "empty ids");
                    HttpResponseMessage noBody = await adminHttp.PostAsync("/api/v1/workflow-profiles/delete/multiple", new StringContent("")).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.BadRequest, noBody, "missing body");

                    HttpResponseMessage forbidden = await userHttp.PostAsync("/api/v1/workflow-profiles/delete/multiple", JsonHelper.ToJsonContent(new { Ids = new[] { created.Id } })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.Forbidden, forbidden, "regular user");

                    HttpResponseMessage foreign = await otherHttp.PostAsync("/api/v1/workflow-profiles/delete/multiple", JsonHelper.ToJsonContent(new { Ids = new[] { created.Id } })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.OK, foreign, "other tenant");
                    DeleteMultipleResult foreignResult = await JsonHelper.DeserializeAsync<DeleteMultipleResult>(foreign).ConfigureAwait(false);
                    AssertEqual(0, foreignResult.Deleted, "another tenant deletes nothing");
                    AssertEqual(DeleteMultipleStatusEnum.NoneDeleted, foreignResult.Status);

                    HttpResponseMessage ok = await adminHttp.PostAsync("/api/v1/workflow-profiles/delete/multiple", JsonHelper.ToJsonContent(new { Ids = new[] { created.Id, "wfp_missing" } })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.OK, ok, "owner");
                    DeleteMultipleResult result = await JsonHelper.DeserializeAsync<DeleteMultipleResult>(ok).ConfigureAwait(false);
                    AssertEqual(1, result.Deleted);
                    AssertEqual("wfp_missing", result.Skipped.Single().Id);

                    HttpResponseMessage gone = await adminHttp.GetAsync("/api/v1/workflow-profiles/" + created.Id).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.NotFound, gone, "deleted profile");
                }
            }));

            cases.Add(CaseAsync("rest_errors_unchanged", "REST keeps its 404, 403, and 400 answers after moving to the shared service", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser admin = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-reste-admin", true).ConfigureAwait(false);
                E2ETenantUser other = await E2ETenantUser.CreateAsync(fx.AuthClient, "wfm-reste-other", true).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(fx, admin, "wfm-reste").ConfigureAwait(false);

                using (HttpClient adminHttp = admin.CreateClient(fx.BaseUrl))
                using (HttpClient otherHttp = other.CreateClient(fx.BaseUrl))
                {
                    HttpResponseMessage invalid = await adminHttp.PostAsync("/api/v1/workflow-profiles", JsonHelper.ToJsonContent(new { Name = "No commands" })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.BadRequest, invalid, "no commands");
                    AssertContains("At least one", await invalid.Content.ReadAsStringAsync().ConfigureAwait(false));

                    HttpResponseMessage createResponse = await adminHttp.PostAsync("/api/v1/workflow-profiles", JsonHelper.ToJsonContent(new { Name = "Rest", Scope = "Vessel", VesselId = vessel.Id, BuildCommand = "make" })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.Created, createResponse, "create");
                    WorkflowProfile created = await JsonHelper.DeserializeAsync<WorkflowProfile>(createResponse).ConfigureAwait(false);

                    AssertStatusCode(HttpStatusCode.NotFound, await otherHttp.GetAsync("/api/v1/workflow-profiles/" + created.Id).ConfigureAwait(false), "cross-tenant get");
                    AssertStatusCode(HttpStatusCode.NotFound, await otherHttp.PutAsync("/api/v1/workflow-profiles/" + created.Id, JsonHelper.ToJsonContent(new { Name = "x", BuildCommand = "y" })).ConfigureAwait(false), "cross-tenant put");
                    AssertStatusCode(HttpStatusCode.NotFound, await otherHttp.DeleteAsync("/api/v1/workflow-profiles/" + created.Id).ConfigureAwait(false), "cross-tenant delete");
                    AssertStatusCode(HttpStatusCode.NotFound, await otherHttp.GetAsync("/api/v1/workflow-profiles/resolve/vessels/" + vessel.Id).ConfigureAwait(false), "cross-tenant resolve");
                    AssertStatusCode(HttpStatusCode.NotFound, await otherHttp.GetAsync("/api/v1/workflow-profiles/preview/vessels/" + vessel.Id).ConfigureAwait(false), "cross-tenant preview");
                    AssertStatusCode(HttpStatusCode.BadRequest, await otherHttp.PostAsync("/api/v1/workflow-profiles", JsonHelper.ToJsonContent(new { Name = "Foreign", Scope = "Vessel", VesselId = vessel.Id, BuildCommand = "make" })).ConfigureAwait(false), "cross-tenant vessel reference");

                    HttpResponseMessage badPut = await adminHttp.PutAsync("/api/v1/workflow-profiles/" + created.Id, JsonHelper.ToJsonContent(new { Name = "Rest", Scope = "Vessel", VesselId = vessel.Id })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.BadRequest, badPut, "put without commands");
                    HttpResponseMessage reread = await adminHttp.GetAsync("/api/v1/workflow-profiles/" + created.Id).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.OK, reread, "reread");
                    AssertEqual("make", (await JsonHelper.DeserializeAsync<WorkflowProfile>(reread).ConfigureAwait(false)).BuildCommand, "rejected PUT left the profile unchanged");

                    HttpResponseMessage put = await adminHttp.PutAsync("/api/v1/workflow-profiles/" + created.Id, JsonHelper.ToJsonContent(new { Name = "Rest", Scope = "Vessel", VesselId = vessel.Id, UnitTestCommand = "test" })).ConfigureAwait(false);
                    AssertStatusCode(HttpStatusCode.OK, put, "full replacement");
                    WorkflowProfile replaced = await JsonHelper.DeserializeAsync<WorkflowProfile>(put).ConfigureAwait(false);
                    AssertNull(replaced.BuildCommand, "PUT is still a full replacement");
                    AssertEqual("test", replaced.UnitTestCommand);
                    AssertStatusCode(HttpStatusCode.NoContent, await adminHttp.DeleteAsync("/api/v1/workflow-profiles/" + created.Id).ConfigureAwait(false), "delete");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Workflow profile MCP tools and REST batch delete",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<Vessel> CreateVesselAsync(E2EServerFixture fx, E2ETenantUser owner, string label)
        {
            using (HttpClient http = owner.CreateClient(fx.BaseUrl))
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage response = await http.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = label + "-" + suffix,
                    RepoUrl = "file:///tmp/" + label + "-" + suffix + ".git",
                    DefaultBranch = "main"
                })).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.Created, response, "create vessel");
                return await JsonHelper.DeserializeAsync<Vessel>(response).ConfigureAwait(false);
            }
        }

        private static McpToolClient Mcp(E2EServerFixture fx, E2ETenantUser user)
        {
            Dictionary<string, string> headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + user.BearerToken };
            return new McpToolClient("http://127.0.0.1:" + fx.McpPort + "/mcp", null, headers);
        }

        private static async Task<T> OkAsync<T>(McpToolClient client, string tool, object args)
        {
            McpToolCallResult result = await client.CallToolResultAsync(tool, JsonHelper.Serialize(args)).ConfigureAwait(false);
            AssertFalse(result.IsError, tool + ": " + result.Text);
            AssertFalse(result.Text.Contains("\"errorCode\"", StringComparison.OrdinalIgnoreCase), tool + " returned an error: " + result.Text);
            return JsonHelper.Deserialize<T>(result.Text);
        }

        private static async Task ErrorAsync(McpToolClient client, string tool, object args, McpToolErrorCodeEnum expected)
        {
            McpToolCallResult result = await client.CallToolResultAsync(tool, JsonHelper.Serialize(args)).ConfigureAwait(false);
            AssertTrue(result.Text.TrimStart().StartsWith("{", StringComparison.Ordinal), tool + " returned no McpToolError: " + result.Text);
            McpToolError error = JsonHelper.Deserialize<McpToolError>(result.Text);
            AssertEqual(expected, error.ErrorCode, tool + ": " + result.Text);
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { TestTags.EndToEnd, tag });
        }

        #endregion
    }
}
