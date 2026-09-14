namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Client;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Contract tests for the typed client against registered fork routes.</summary>
    public sealed class ArmadaApiClientSuite : IArmadaTestSuite
    {
        /// <summary>Builds the typed client route contract cases.</summary>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();
            cases.Add(CaseAsync("get_readiness_uses_route_and_deserializes", "ArmadaApiClient Readiness Uses Route", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, "{\"isReady\":true,\"vesselId\":\"vsl/a\"}");
                using (ArmadaApiClient client = CreateClient(handler))
                {
                VesselReadinessResult? result = await client.GetVesselReadinessAsync("vsl/a", environmentName: "test env");
                AssertNotNull(result);
                AssertTrue(result!.IsReady, "Readiness must deserialize IsReady.");
                AssertEqual("vsl/a", result.VesselId);
                AssertEqual("GET", handler.Method);
                AssertEqual("/api/v1/vessels/vsl%2Fa/readiness?environmentName=test%20env", handler.PathAndQuery);
                }
            }));
            cases.Add(CaseAsync("create_deployment_posts_typed_body", "ArmadaApiClient Deployment Posts Typed Body", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, "{\"id\":\"dpl_response\",\"title\":\"typed deployment\"}");
                using (ArmadaApiClient client = CreateClient(handler))
                {
                await client.CreateDeploymentAsync(new DeploymentUpsertRequest { Title = "typed deployment" });
                AssertEqual("POST", handler.Method);
                AssertEqual("/api/v1/deployments", handler.PathAndQuery);
                AssertTrue(handler.Body.Contains("\"title\":\"typed deployment\"", StringComparison.OrdinalIgnoreCase), "The deployment title must be serialized.");
                }
            }));
            cases.Add(CaseAsync("release_id_is_encoded", "ArmadaApiClient Encodes Release ID", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, "{\"id\":\"rel_response\",\"title\":\"typed release\"}");
                using (ArmadaApiClient client = CreateClient(handler))
                {
                await client.GetReleaseAsync("rel/a b");
                AssertEqual("/api/v1/releases/rel%2Fa%20b", handler.PathAndQuery);
                }
            }));
            cases.Add(CaseAsync("server_error_contains_response_body", "ArmadaApiClient Server Error Contains Body", TestTags.Negative, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.BadRequest, "route contract failed");
                using (ArmadaApiClient client = CreateClient(handler))
                {
                try
                {
                    await client.RefreshReleaseAsync("rel_test");
                    throw new InvalidOperationException("Expected the client to reject the failed response.");
                }
                catch (HttpRequestException ex)
                {
                    AssertEqual(HttpStatusCode.BadRequest, ex.StatusCode);
                    AssertTrue(ex.Message.Contains("route contract failed", StringComparison.Ordinal), "The response body must be retained in the exception.");
                }
                }
            }));
            cases.Add(CaseAsync("cancellation_is_forwarded", "ArmadaApiClient Cancellation Is Forwarded", TestTags.Negative, async () =>
            {
                RecordingHandler handler = new RecordingHandler(new OperationCanceledException());
                using (ArmadaApiClient client = CreateClient(handler))
                using (CancellationTokenSource source = new CancellationTokenSource())
                {
                Task<Job?> request = client.GetJobAsync("job_test", source.Token);
                source.CancelAfter(20);
                await AssertThrowsAsync<OperationCanceledException>(() => request);
                AssertTrue(handler.CancellationObserved, "The handler must receive the cancellation token.");
                }
            }));
            AddContractCases(cases);
            AddProfileObjectiveCases(cases);
            cases.Add(CaseAsync("live_health_route_returns_success", "ArmadaApiClient Live Health Route", TestTags.Positive, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this);
                using (ArmadaApiClient client = new ArmadaApiClient(fixture.UnauthClient, fixture.BaseUrl))
                {
                    AssertTrue(await client.HealthCheckAsync(), "The live health route must return success without authentication.");
                }
            }));
            cases.Add(CaseAsync("live_vessel_list_deserializes_server_response", "ArmadaApiClient Live Vessel List", TestTags.Positive, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this);
                using (ArmadaApiClient client = new ArmadaApiClient(fixture.AuthClient, fixture.BaseUrl))
                {
                    EnumerationResult<Vessel>? result = await client.ListVesselsAsync();
                    AssertNotNull(result);
                    AssertTrue(result!.Success, "The live vessel list response must deserialize its success flag.");
                    AssertTrue(result.Objects != null, "The live vessel list response must deserialize its object collection.");
                }
            }));
            cases.Add(CaseAsync("live_branch_route_deserializes_repository_inspection", "ArmadaApiClient Live Branch Route", TestTags.Positive, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this);
                string repositoryPath = TestGitRepoHelper.CreateWorkingRepoCopy();
                using (ArmadaApiClient client = new ArmadaApiClient(fixture.AuthClient, fixture.BaseUrl))
                {
                    Vessel? vessel = await client.CreateVesselAsync(new Vessel
                    {
                        Name = "SdkBranchRoundTrip-" + Guid.NewGuid().ToString("N"),
                        RepoUrl = "file:///sdk-branch-round-trip",
                        LocalPath = repositoryPath,
                        WorkingDirectory = repositoryPath
                    });
                    AssertNotNull(vessel);
                    BranchListResponse? result = await client.ListVesselBranchesAsync(vessel!.Id);
                    AssertNotNull(result);
                    AssertEqual(vessel.Id, result!.VesselId);
                    AssertEqual("LocalPath", result.Source);
                    AssertTrue(result.BranchCount > 0, "The live branch response must include the repository branch.");
                    AssertTrue(result.Branches.Exists(branch => String.Equals(branch.Name, "main", StringComparison.Ordinal)), "The live branch response must include main.");
                }
            }));
            cases.Add(CaseAsync("live_branch_route_rejects_missing_authentication", "ArmadaApiClient Live Branch Auth Error", TestTags.Negative, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this);
                using (ArmadaApiClient client = new ArmadaApiClient(fixture.UnauthClient, fixture.BaseUrl))
                {
                    try
                    {
                        await client.ListVesselBranchesAsync("vsl_missing");
                        AssertTrue(false, "The live branch route must reject an unauthenticated request.");
                    }
                    catch (HttpRequestException exception)
                    {
                        AssertEqual(HttpStatusCode.Unauthorized, exception.StatusCode);
                    }
                }
            }));
            return new TestSuiteDescriptor(
                suiteId: "Services.ArmadaApiClient",
                displayName: "Armada API Client",
                cases: cases);
        }

        private static void AddContractCases(List<TestCaseDescriptor> cases)
        {
            cases.Add(ContractCaseTyped("list_vessel_branches", "GET", "/api/v1/vessels/vsl_test/branches", "{\"vesselId\":\"vsl_response\",\"defaultBranch\":\"main\",\"source\":\"LocalPath\",\"headState\":\"attached\",\"headRef\":\"main\",\"branchCount\":1,\"branches\":[{\"name\":\"main\",\"isCurrent\":true}]}", (c,t) => c.ListVesselBranchesAsync("vsl_test", t), r => { AssertEqual("vsl_response", r.VesselId); AssertEqual("main", r.Branches[0].Name); }, null));
            cases.Add(ContractCaseTyped("list_check_runs", "GET", "/api/v1/check-runs", "{\"totalRecords\":1,\"objects\":[{\"id\":\"chk_list\"}]}", (c,t) => c.ListCheckRunsAsync(t), r => AssertEqual("chk_list", r.Objects[0].Id), null));
            cases.Add(ContractCaseTyped("enumerate_check_runs", "POST", "/api/v1/check-runs/enumerate", "{\"totalRecords\":1,\"objects\":[{\"id\":\"chk_enum\"}]}", (c,t) => c.EnumerateCheckRunsAsync(new EnumerationQuery { PageSize = 7 }, t), r => AssertEqual("chk_enum", r.Objects[0].Id), b => AssertJsonPropertyNumber(b, "PageSize", 7)));
            cases.Add(ContractCaseTyped("list_deployments", "GET", "/api/v1/deployments", "{\"totalRecords\":1,\"objects\":[{\"id\":\"dpl_list\"}]}", (c,t) => c.ListDeploymentsAsync(t), r => AssertEqual("dpl_list", r.Objects[0].Id), null));
            cases.Add(ContractCaseTyped("enumerate_deployments", "POST", "/api/v1/deployments/enumerate", "{\"totalRecords\":1,\"objects\":[{\"id\":\"dpl_enum\"}]}", (c,t) => c.EnumerateDeploymentsAsync(new EnumerationQuery { PageSize = 7 }, t), r => AssertEqual("dpl_enum", r.Objects[0].Id), b => AssertJsonPropertyNumber(b, "PageSize", 7)));
            cases.Add(ContractCaseTyped("list_environments", "GET", "/api/v1/environments", "{\"totalRecords\":1,\"objects\":[{\"id\":\"env_list\"}]}", (c,t) => c.ListEnvironmentsAsync(t), r => AssertEqual("env_list", r.Objects[0].Id), null));
            cases.Add(ContractCaseTyped("enumerate_environments", "POST", "/api/v1/environments/enumerate", "{\"totalRecords\":1,\"objects\":[{\"id\":\"env_enum\"}]}", (c,t) => c.EnumerateEnvironmentsAsync(new EnumerationQuery { PageSize = 7 }, t), r => AssertEqual("env_enum", r.Objects[0].Id), b => AssertJsonPropertyNumber(b, "PageSize", 7)));
            cases.Add(ContractCaseTyped("list_project_profiles", "GET", "/api/v1/project-profiles", "{\"totalRecords\":1,\"objects\":[{\"id\":\"pp_list\"}]}", (c,t) => c.ListProjectProfilesAsync(t), r => AssertEqual("pp_list", r.Objects[0].Id), null));
            cases.Add(ContractCaseTyped("enumerate_project_profiles", "POST", "/api/v1/project-profiles/enumerate", "{\"totalRecords\":1,\"objects\":[{\"id\":\"pp_enum\"}]}", (c,t) => c.EnumerateProjectProfilesAsync(new EnumerationQuery { PageSize = 7 }, t), r => AssertEqual("pp_enum", r.Objects[0].Id), b => AssertJsonPropertyNumber(b, "PageSize", 7)));
            cases.Add(ContractCaseTyped("list_releases", "GET", "/api/v1/releases", "{\"totalRecords\":1,\"objects\":[{\"id\":\"rel_list\"}]}", (c,t) => c.ListReleasesAsync(t), r => AssertEqual("rel_list", r.Objects[0].Id), null));
            cases.Add(ContractCaseTyped("enumerate_releases", "POST", "/api/v1/releases/enumerate", "{\"totalRecords\":1,\"objects\":[{\"id\":\"rel_enum\"}]}", (c,t) => c.EnumerateReleasesAsync(new EnumerationQuery { PageSize = 7 }, t), r => AssertEqual("rel_enum", r.Objects[0].Id), b => AssertJsonPropertyNumber(b, "PageSize", 7)));
            cases.Add(ContractCaseTyped("release_github_pull_requests", "GET", "/api/v1/releases/rel_test/github/pull-requests", "[{\"number\":42,\"headSha\":\"abc123\"}]", (c,t) => c.GetReleaseGitHubPullRequestsAsync("rel_test", t), r => AssertEqual(42, r[0].Number), null));
            cases.Add(ContractCaseTyped("list_skills", "GET", "/api/v1/skills", "{\"totalRecords\":1,\"objects\":[{\"id\":\"sk_list\"}]}", (c,t) => c.ListSkillsAsync(t), r => AssertEqual("sk_list", r.Objects[0].Id), null));
            cases.Add(ContractCaseTyped("enumerate_skills", "POST", "/api/v1/skills/enumerate", "{\"totalRecords\":1,\"objects\":[{\"id\":\"sk_enum\"}]}", (c,t) => c.EnumerateSkillsAsync(new EnumerationQuery { PageSize = 7 }, t), r => AssertEqual("sk_enum", r.Objects[0].Id), b => AssertJsonPropertyNumber(b, "PageSize", 7)));
            cases.Add(ContractCaseTyped("list_workflow_profiles", "GET", "/api/v1/workflow-profiles", "{\"totalRecords\":1,\"objects\":[{\"id\":\"wf_list\"}]}", (c,t) => c.ListWorkflowProfilesAsync(t), r => AssertEqual("wf_list", r.Objects[0].Id), null));
            cases.Add(ContractCaseTyped("enumerate_workflow_profiles", "POST", "/api/v1/workflow-profiles/enumerate", "{\"totalRecords\":1,\"objects\":[{\"id\":\"wf_enum\"}]}", (c,t) => c.EnumerateWorkflowProfilesAsync(new EnumerationQuery { PageSize = 7 }, t), r => AssertEqual("wf_enum", r.Objects[0].Id), b => AssertJsonPropertyNumber(b, "PageSize", 7)));
            cases.Add(ContractCaseTyped("list_objectives", "GET", "/api/v1/objectives", "{\"totalRecords\":1,\"objects\":[{\"id\":\"obj_list\"}]}", (c,t) => c.ListObjectivesAsync(t), r => AssertEqual("obj_list", r.Objects[0].Id), null));
            cases.Add(ContractCaseTyped("reorder_objectives", "POST", "/api/v1/objectives/reorder", "[{\"id\":\"obj_reordered\"}]", (c,t) => c.ReorderObjectivesAsync(new ObjectiveReorderRequest { Items = new List<ObjectiveReorderItem> { new ObjectiveReorderItem { ObjectiveId = "obj_test", Rank = 3 } } }, t), r => AssertEqual("obj_reordered", r[0].Id), AssertReorderBody));
            cases.Add(ContractCaseTyped("list_backlog_refinement_sessions", "GET", "/api/v1/backlog/obj_test/refinement-sessions", "[{\"id\":\"ses_list\"}]", (c,t) => c.ListBacklogRefinementSessionsAsync("obj_test", t), r => AssertEqual("ses_list", r[0].Id), null));
            cases.Add(ContractCaseTyped("list_objective_refinement_sessions", "GET", "/api/v1/objectives/obj_test/refinement-sessions", "[{\"id\":\"ses_list\"}]", (c,t) => c.ListObjectiveRefinementSessionsAsync("obj_test", t), r => AssertEqual("ses_list", r[0].Id), null));
            cases.Add(ContractCaseTyped("list_jobs", "GET", "/api/v1/jobs", "{\"totalRecords\":1,\"objects\":[{\"id\":\"job_list\"}]}", (c,t) => c.ListJobsAsync(t), r => AssertEqual("job_list", r.Objects[0].Id), null));
            cases.Add(ContractCaseTyped("list_token_usage", "GET", "/api/v1/token-usage?pageSize=7&model=model%2Ftest&runtime=runtime%2Ftest&source=chat&vesselId=vsl%2Ftest&captainId=cap%2Ftest", "{\"totalRecords\":1,\"objects\":[{\"id\":\"tok_list\"}]}", (c,t) => c.ListTokenUsageAsync(new TokenUsageQuery { Model = "model/test", Runtime = "runtime/test", Source = "chat", VesselId = "vsl/test", CaptainId = "cap/test", PageSize = 7 }, t), r => AssertEqual("tok_list", r.Objects[0].Id), null));
            cases.Add(ContractCase("vessel_landing_preview", "GET", "/api/v1/vessels/vsl_test/landing-preview?sourceBranch=feature%2Ftest", (client, token) => client.GetVesselLandingPreviewAsync("vsl_test", "feature/test", token)));
            cases.Add(ContractCase("environment_get", "GET", "/api/v1/environments/env_test", (client, token) => client.GetEnvironmentAsync("env_test", token)));
            cases.Add(ContractCase("environment_create", "POST", "/api/v1/environments", (client, token) => client.CreateEnvironmentAsync(new DeploymentEnvironmentUpsertRequest { Name = "typed environment" }, token)));
            cases.Add(ContractCase("environment_update", "PUT", "/api/v1/environments/env_test", (client, token) => client.UpdateEnvironmentAsync("env_test", new DeploymentEnvironmentUpsertRequest { Name = "typed environment" }, token)));
            cases.Add(ContractCaseVoid("environment_delete", "DELETE", "/api/v1/environments/env_test", async (client, token) => { await client.DeleteEnvironmentAsync("env_test", token); }));
            cases.Add(ContractCase("deployment_get", "GET", "/api/v1/deployments/dpl_test", (client, token) => client.GetDeploymentAsync("dpl_test", token)));
            cases.Add(ContractCase("deployment_create", "POST", "/api/v1/deployments", (client, token) => client.CreateDeploymentAsync(new DeploymentUpsertRequest { Title = "typed deployment" }, token)));
            cases.Add(ContractCase("deployment_update", "PUT", "/api/v1/deployments/dpl_test", (client, token) => client.UpdateDeploymentAsync("dpl_test", new DeploymentUpsertRequest { Title = "typed deployment" }, token)));
            cases.Add(ContractCase("deployment_approve", "POST", "/api/v1/deployments/dpl_test/approve", (client, token) => client.ApproveDeploymentAsync("dpl_test", "approved", token)));
            cases.Add(ContractCase("deployment_deny", "POST", "/api/v1/deployments/dpl_test/deny", (client, token) => client.DenyDeploymentAsync("dpl_test", "denied", token)));
            cases.Add(ContractCase("deployment_verify", "POST", "/api/v1/deployments/dpl_test/verify", (client, token) => client.VerifyDeploymentAsync("dpl_test", token)));
            cases.Add(ContractCase("deployment_rollback", "POST", "/api/v1/deployments/dpl_test/rollback", (client, token) => client.RollbackDeploymentAsync("dpl_test", token)));
            cases.Add(ContractCaseVoid("deployment_delete", "DELETE", "/api/v1/deployments/dpl_test", async (client, token) => { await client.DeleteDeploymentAsync("dpl_test", token); }));
            cases.Add(ContractCase("mission_landing_preview", "GET", "/api/v1/missions/msn_test/landing-preview", (client, token) => client.GetMissionLandingPreviewAsync("msn_test", token)));
            cases.Add(ContractCase("mission_github_pull_request", "GET", "/api/v1/missions/msn_test/github/pull-request", (client, token) => client.GetMissionGitHubPullRequestAsync("msn_test", token)));
            cases.Add(ContractCase("token_usage_summary", "GET", "/api/v1/token-usage/summary?model=model%2Ftest", (client, token) => client.GetTokenUsageSummaryAsync(model: "model/test", token: token)));
            cases.Add(ContractCase("release_get", "GET", "/api/v1/releases/rel_test", (client, token) => client.GetReleaseAsync("rel_test", token)));
            cases.Add(ContractCase("release_create", "POST", "/api/v1/releases", (client, token) => client.CreateReleaseAsync(new ReleaseUpsertRequest { Title = "typed release" }, token)));
            cases.Add(ContractCase("release_update", "PUT", "/api/v1/releases/rel_test", (client, token) => client.UpdateReleaseAsync("rel_test", new ReleaseUpsertRequest { Title = "typed release" }, token)));
            cases.Add(ContractCase("release_refresh", "POST", "/api/v1/releases/rel_test/refresh", (client, token) => client.RefreshReleaseAsync("rel_test", token)));
            cases.Add(ContractCaseVoid("release_delete", "DELETE", "/api/v1/releases/rel_test", async (client, token) => { await client.DeleteReleaseAsync("rel_test", token); }));
            cases.Add(ContractCase("check_get", "GET", "/api/v1/check-runs/chk_test", (client, token) => client.GetCheckRunAsync("chk_test", token)));
            cases.Add(ContractCase("check_run", "POST", "/api/v1/check-runs", (client, token) => client.RunCheckAsync(new CheckRunRequest { VesselId = "vsl_test", Label = "typed check" }, token)));
            cases.Add(ContractCase("github_actions_sync", "POST", "/api/v1/check-runs/sync/github-actions", (client, token) => client.SyncGitHubActionsAsync(new GitHubActionsSyncRequest { VesselId = "vsl_test", WorkflowName = "typed workflow" }, token)));
            cases.Add(ContractCase("check_retry", "POST", "/api/v1/check-runs/chk_test/retry", (client, token) => client.RetryCheckRunAsync("chk_test", token)));
            cases.Add(ContractCaseVoid("check_delete", "DELETE", "/api/v1/check-runs/chk_test", async (client, token) => { await client.DeleteCheckRunAsync("chk_test", token); }));
            cases.Add(ContractCase("job_get", "GET", "/api/v1/jobs/job_test", (client, token) => client.GetJobAsync("job_test", token)));
            cases.Add(ContractCase("job_cancel", "POST", "/api/v1/jobs/job_test/cancel", (client, token) => client.CancelJobAsync("job_test", token)));
        }

        private static void AddProfileObjectiveCases(List<TestCaseDescriptor> cases)
        {
            cases.Add(ContractCaseTyped("workflow_get", "GET", "/api/v1/workflow-profiles/wf_test", "{\"id\":\"wf_response\",\"name\":\"workflow response\"}", (ArmadaApiClient c, CancellationToken t) => c.GetWorkflowProfileAsync("wf_test", t), r => AssertEqual("wf_response", r.Id), null));
            cases.Add(ContractCaseTyped("workflow_validate", "POST", "/api/v1/workflow-profiles/validate", "{\"isValid\":true}", (ArmadaApiClient c, CancellationToken t) => c.ValidateWorkflowProfileAsync(new WorkflowProfile { Name = "workflow request" }, t), r => AssertTrue(r.IsValid, "Workflow validation must deserialize."), b => AssertJsonProperty(b, "Name", "workflow request")));
            cases.Add(ContractCaseTyped("workflow_preview", "GET", "/api/v1/workflow-profiles/preview/vessels/vsl_test?workflowProfileId=wf%2Ftest", "{\"resolutionMode\":\"Global\",\"resolvedProfile\":{\"id\":\"wf_response\",\"name\":\"workflow response\"}}", (ArmadaApiClient c, CancellationToken t) => c.PreviewWorkflowProfileForVesselAsync("vsl_test", "wf/test", t), r => AssertEqual("wf_response", r.ResolvedProfile!.Id), null));
            cases.Add(ContractCaseTyped("workflow_resolve", "GET", "/api/v1/workflow-profiles/resolve/vessels/vsl_test?workflowProfileId=wf%2Ftest", "{\"id\":\"wf_response\",\"name\":\"workflow response\"}", (ArmadaApiClient c, CancellationToken t) => c.ResolveWorkflowProfileAsync("vsl_test", "wf/test", t), r => AssertEqual("wf_response", r.Id), null));
            cases.Add(ContractCaseTyped("workflow_create", "POST", "/api/v1/workflow-profiles", "{\"id\":\"wf_response\",\"name\":\"workflow response\"}", (ArmadaApiClient c, CancellationToken t) => c.CreateWorkflowProfileAsync(new WorkflowProfile { Name = "workflow request" }, t), r => AssertEqual("wf_response", r.Id), b => AssertJsonProperty(b, "Name", "workflow request")));
            cases.Add(ContractCaseTyped("workflow_update", "PUT", "/api/v1/workflow-profiles/wf_test", "{\"id\":\"wf_response\",\"name\":\"workflow response\"}", (ArmadaApiClient c, CancellationToken t) => c.UpdateWorkflowProfileAsync("wf_test", new WorkflowProfile { Name = "workflow request" }, t), r => AssertEqual("wf_response", r.Id), b => AssertJsonProperty(b, "Name", "workflow request")));
            cases.Add(ContractCaseVoid("workflow_delete", "DELETE", "/api/v1/workflow-profiles/wf_test", (c,t) => c.DeleteWorkflowProfileAsync("wf_test",t)));
            cases.Add(ContractCaseTyped("project_get", "GET", "/api/v1/project-profiles/pp_test", "{\"id\":\"pp_response\",\"name\":\"project response\"}", (ArmadaApiClient c, CancellationToken t) => c.GetProjectProfileAsync("pp_test",t), r => AssertEqual("pp_response",r.Id), null));
            cases.Add(ContractCaseTyped("project_validate", "POST", "/api/v1/project-profiles/validate", "{\"isValid\":true}", (c,t) => c.ValidateProjectProfileAsync(new ProjectProfile { Name="project request" },t), r=>AssertTrue(r.IsValid,"Project validation must deserialize."), b=>AssertJsonProperty(b,"Name","project request")));
            cases.Add(ContractCaseTyped("project_resolve", "GET", "/api/v1/project-profiles/resolve/vessels/vsl_test?projectProfileId=pp%2Ftest", "{\"mode\":\"Global\",\"profile\":{\"id\":\"pp_response\",\"name\":\"project response\"}}", (c,t)=>c.ResolveProjectProfileAsync("vsl_test","pp/test",t), r=>AssertEqual("pp_response",r.Profile!.Id),null));
            cases.Add(ContractCaseTyped("persona_preview", "GET", "/api/v1/project-profiles/pp%2Ftest/persona-preview/Architect%2FLead", "{\"personaName\":\"Architect\",\"effectiveTemplateName\":\"effective\"}", (c,t)=>c.PreviewPersonaPromptAsync("pp/test","Architect/Lead",t), r=>AssertEqual("effective",r.EffectiveTemplateName),null));
            cases.Add(ContractCaseTyped("project_create", "POST", "/api/v1/project-profiles", "{\"id\":\"pp_response\",\"name\":\"project response\"}", (c,t)=>c.CreateProjectProfileAsync(new ProjectProfile {Name="project request"},t), r=>AssertEqual("pp_response",r.Id), b=>AssertJsonProperty(b,"Name","project request")));
            cases.Add(ContractCaseTyped("project_update", "PUT", "/api/v1/project-profiles/pp_test", "{\"id\":\"pp_response\",\"name\":\"project response\"}", (c,t)=>c.UpdateProjectProfileAsync("pp_test",new ProjectProfile {Name="project request"},t), r=>AssertEqual("pp_response",r.Id), b=>AssertJsonProperty(b,"Name","project request")));
            cases.Add(ContractCaseVoid("project_delete", "DELETE", "/api/v1/project-profiles/pp_test", (c,t)=>c.DeleteProjectProfileAsync("pp_test",t)));
            cases.Add(ContractCaseTyped("skill_get", "GET", "/api/v1/skills/sk_test", "{\"id\":\"sk_response\",\"name\":\"skill response\"}", (c,t)=>c.GetSkillAsync("sk_test",t), r=>AssertEqual("sk_response",r.Id),null));
            cases.Add(ContractCaseTyped("skill_create", "POST", "/api/v1/skills", "{\"id\":\"sk_response\",\"name\":\"skill response\"}", (c,t)=>c.CreateSkillAsync(new Skill {Name="skill request",Content="content"},t), r=>AssertEqual("sk_response",r.Id),b=>{AssertJsonProperty(b,"Name","skill request");AssertJsonProperty(b,"Content","content");}));
            cases.Add(ContractCaseTyped("skill_update", "PUT", "/api/v1/skills/sk_test", "{\"id\":\"sk_response\",\"name\":\"skill response\"}", (c,t)=>c.UpdateSkillAsync("sk_test",new Skill {Name="skill request",Content="content"},t), r=>AssertEqual("sk_response",r.Id),b=>{AssertJsonProperty(b,"Name","skill request");AssertJsonProperty(b,"Content","content");}));
            cases.Add(ContractCaseVoid("skill_delete", "DELETE", "/api/v1/skills/sk_test", (c,t)=>c.DeleteSkillAsync("sk_test",t)));
            cases.Add(ContractCaseTyped("ask", "POST", "/api/v1/ask", "{\"reply\":\"answer\",\"kind\":\"Answer\"}", (c,t)=>c.AskAsync("question",t), r=>AssertEqual("answer",r.Reply),b=>AssertJsonProperty(b,"Message","question")));
            cases.Add(ContractCaseTyped("objective_get", "GET", "/api/v1/objectives/obj_test", "{\"id\":\"obj_response\",\"title\":\"objective response\"}", (c,t)=>c.GetObjectiveAsync("obj_test",t), r=>AssertEqual("obj_response",r.Id),null));
            cases.Add(ContractCaseTyped("objective_create", "POST", "/api/v1/objectives", "{\"id\":\"obj_response\",\"title\":\"objective response\"}", (c,t)=>c.CreateObjectiveAsync(new ObjectiveUpsertRequest {Title="objective request"},t), r=>AssertEqual("obj_response",r.Id),b=>AssertJsonProperty(b,"Title","objective request")));
            cases.Add(ContractCaseTyped("objective_update", "PUT", "/api/v1/objectives/obj_test", "{\"id\":\"obj_response\",\"title\":\"objective response\"}", (c,t)=>c.UpdateObjectiveAsync("obj_test",new ObjectiveUpsertRequest {Title="objective request"},t), r=>AssertEqual("obj_response",r.Id),b=>AssertJsonProperty(b,"Title","objective request")));
            cases.Add(ContractCaseVoid("objective_delete", "DELETE", "/api/v1/objectives/obj_test", (c,t)=>c.DeleteObjectiveAsync("obj_test",t)));
            cases.Add(ContractCaseTyped("objective_import", "POST", "/api/v1/objectives/import/github", "{\"id\":\"obj_response\",\"title\":\"objective response\"}", (c,t)=>c.ImportObjectiveFromGitHubAsync(new GitHubObjectiveImportRequest {VesselId="vsl_test",Number=7},t), r=>AssertEqual("obj_response",r.Id),b=>{AssertJsonProperty(b,"VesselId","vsl_test");AssertJsonPropertyNumber(b,"Number",7);}));
            string sessionBody = "{\"session\":{\"id\":\"ses_response\"}}";
            cases.Add(ContractCaseTyped("objective_refine_create", "POST", "/api/v1/objectives/obj_test/refinement-sessions", sessionBody, (c,t)=>c.CreateObjectiveRefinementSessionAsync("obj_test",new ObjectiveRefinementSessionCreateRequest {CaptainId="cap_test",Title="session"},t), r=>AssertEqual("ses_response",r.Session.Id), b=>{AssertJsonProperty(b,"CaptainId","cap_test");AssertJsonProperty(b,"Title","session");}));
            cases.Add(ContractCaseTyped("backlog_refine_create", "POST", "/api/v1/backlog/obj_test/refinement-sessions", sessionBody, (c,t)=>c.CreateBacklogRefinementSessionAsync("obj_test",new ObjectiveRefinementSessionCreateRequest {CaptainId="cap_test",Title="session"},t), r=>AssertEqual("ses_response",r.Session.Id), b=>AssertJsonProperty(b,"CaptainId","cap_test")));
            cases.Add(ContractCaseTyped("refine_get", "GET", "/api/v1/objective-refinement-sessions/ses_test", sessionBody, (c,t)=>c.GetObjectiveRefinementSessionAsync("ses_test",t), r=>AssertEqual("ses_response",r.Session.Id),null));
            cases.Add(ContractCaseTyped("refine_message", "POST", "/api/v1/objective-refinement-sessions/ses_test/messages", sessionBody, (c,t)=>c.SendObjectiveRefinementMessageAsync("ses_test",new ObjectiveRefinementMessageRequest {Content="message"},t), r=>AssertEqual("ses_response",r.Session.Id),b=>AssertJsonProperty(b,"Content","message")));
            cases.Add(ContractCaseTyped("refine_summarize", "POST", "/api/v1/objective-refinement-sessions/ses_test/summarize", "{\"sessionId\":\"ses_test\",\"summary\":\"summary\"}", (c,t)=>c.SummarizeObjectiveRefinementSessionAsync("ses_test",new ObjectiveRefinementSummaryRequest {MessageId="msg_test"},t), r=>AssertEqual("summary",r.Summary),b=>AssertJsonProperty(b,"MessageId","msg_test")));
            cases.Add(ContractCaseTyped("refine_apply", "POST", "/api/v1/objective-refinement-sessions/ses_test/apply", "{\"summary\":{\"summary\":\"summary\"},\"objective\":{\"id\":\"obj_response\",\"title\":\"objective response\"}}", (c,t)=>c.ApplyObjectiveRefinementSummaryAsync("ses_test",new ObjectiveRefinementApplyRequest {MessageId="msg_test",MarkMessageSelected=false},t), r=>AssertEqual("summary",r.Summary.Summary),b=>AssertJsonProperty(b,"MessageId","msg_test")));
            cases.Add(ContractCaseTyped("refine_stop", "POST", "/api/v1/objective-refinement-sessions/ses_test/stop", sessionBody, (c,t)=>c.StopObjectiveRefinementSessionAsync("ses_test",t), r=>AssertEqual("ses_response",r.Session.Id),b=>AssertEmptyJson(b)));
            cases.Add(ContractCaseVoid("refine_delete", "DELETE", "/api/v1/objective-refinement-sessions/ses_test", (c,t)=>c.DeleteObjectiveRefinementSessionAsync("ses_test",t)));
        }

        private static TestCaseDescriptor ContractCaseTyped<T>(string id, string method, string path, string responseJson, Func<ArmadaApiClient, CancellationToken, Task<T?>> call, Action<T> assertResponse, Action<string>? assertBody)
        {
            return CaseAsync(id, "ArmadaApiClient " + id + " contract", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, responseJson);
                using (ArmadaApiClient client = CreateClient(handler))
                {
                    T? result = await call(client, CancellationToken.None);
                    AssertEqual(method, handler.Method);
                    AssertEqual(path, handler.PathAndQuery);
                    AssertNotNull(result);
                    assertResponse(result!);
                    if (assertBody != null) assertBody(handler.Body);
                }
            });
        }

        private static void AssertJsonProperty(string body, string name, string expected)
        {
            using (JsonDocument document = JsonDocument.Parse(body))
            {
                AssertEqual(expected, document.RootElement.GetProperty(name).GetString());
            }
        }

        private static void AssertJsonPropertyNumber(string body, string name, int expected)
        {
            using (JsonDocument document = JsonDocument.Parse(body))
            {
                AssertEqual(expected, document.RootElement.GetProperty(name).GetInt32());
            }
        }

        private static void AssertReorderBody(string body)
        {
            ObjectiveReorderRequest request = JsonSerializer.Deserialize<ObjectiveReorderRequest>(body)!;
            AssertEqual(1, request.Items.Count);
            AssertEqual("obj_test", request.Items[0].ObjectiveId);
            AssertEqual(3, request.Items[0].Rank);
        }

        private static void AssertEmptyJson(string body)
        {
            using (JsonDocument document = JsonDocument.Parse(body))
            {
                AssertEqual(JsonValueKind.Object, document.RootElement.ValueKind);
                int count = 0; foreach (JsonProperty _ in document.RootElement.EnumerateObject()) count++; AssertEqual(0, count);
            }
        }

        private static TestCaseDescriptor ContractCase<T>(string id, string method, string path, Func<ArmadaApiClient, CancellationToken, Task<T?>> call)
        {
            return CaseAsync(id, "ArmadaApiClient " + id + " contract", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, ResponseBody<T>());
                using (ArmadaApiClient client = CreateClient(handler))
                {
                T? result = await call(client, CancellationToken.None);
                AssertEqual(method, handler.Method);
                AssertEqual(path, handler.PathAndQuery);
                if (method != "DELETE") AssertTypedResponse(result);
                if (method == "POST" || method == "PUT")
                    AssertRequestBody(id, handler.Body);
                }
            });
        }

        private static void AssertRequestBody(string id, string body)
        {
            if (id == "environment_create" || id == "environment_update")
            {
                DeploymentEnvironmentUpsertRequest request = JsonSerializer.Deserialize<DeploymentEnvironmentUpsertRequest>(body)!;
                AssertEqual("typed environment", request.Name);
            }
            else if (id == "deployment_create" || id == "deployment_update")
            {
                DeploymentUpsertRequest request = JsonSerializer.Deserialize<DeploymentUpsertRequest>(body)!;
                AssertEqual("typed deployment", request.Title);
            }
            else if (id == "release_create" || id == "release_update")
            {
                ReleaseUpsertRequest request = JsonSerializer.Deserialize<ReleaseUpsertRequest>(body)!;
                AssertEqual("typed release", request.Title);
            }
            else if (id == "check_run")
            {
                CheckRunRequest request = JsonSerializer.Deserialize<CheckRunRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })!;
                AssertEqual("vsl_test", request.VesselId);
                AssertEqual("typed check", request.Label);
                AssertEqual("Build", request.Type.ToString());
            }
            else if (id == "github_actions_sync")
            {
                GitHubActionsSyncRequest request = JsonSerializer.Deserialize<GitHubActionsSyncRequest>(body)!;
                AssertEqual("vsl_test", request.VesselId);
                AssertEqual("typed workflow", request.WorkflowName);
            }
            else if (id == "deployment_approve" || id == "deployment_deny")
            {
                Dictionary<string, string> request = JsonSerializer.Deserialize<Dictionary<string, string>>(body)!;
                string comment = request.TryGetValue("Comment", out string? pascalComment) ? pascalComment : request["comment"];
                AssertEqual(id.EndsWith("approve", StringComparison.Ordinal) ? "approved" : "denied", comment);
            }
            else
            {
                Dictionary<string, object> request = JsonSerializer.Deserialize<Dictionary<string, object>>(body)!;
                AssertEqual(0, request.Count, "Action requests without fields must be empty JSON objects.");
            }
        }

        private static string ResponseBody<T>()
        {
            Type type = typeof(T);
            if (type == typeof(VesselReadinessResult)) return "{\"isReady\":true,\"vesselId\":\"vsl_response\"}";
            if (type == typeof(LandingPreviewResult)) return "{\"isReadyToLand\":true,\"targetBranch\":\"main\"}";
            if (type == typeof(GitHubPullRequestDetail)) return "{\"number\":42,\"headSha\":\"abc123\",\"title\":\"typed PR\"}";
            if (type == typeof(TokenUsageSummaryResult)) return "{\"totalTokens\":123}";
            if (type == typeof(GitHubActionsSyncResult)) return "{\"createdCount\":3}";
            if (type == typeof(DeploymentEnvironment)) return "{\"id\":\"env_response\",\"name\":\"typed environment\"}";
            if (type == typeof(Deployment)) return "{\"id\":\"dpl_response\",\"title\":\"typed deployment\"}";
            if (type == typeof(Release)) return "{\"id\":\"rel_response\",\"title\":\"typed release\"}";
            if (type == typeof(CheckRun)) return "{\"id\":\"chk_response\",\"label\":\"typed check\"}";
            return "{\"id\":\"job_response\",\"name\":\"typed job\"}";
        }

        private static void AssertTypedResponse<T>(T? result)
        {
            AssertNotNull(result);
            switch (result)
            {
                case VesselReadinessResult readiness:
                    AssertTrue(readiness.IsReady, "IsReady must deserialize.");
                    break;
                case LandingPreviewResult preview:
                    AssertTrue(preview.IsReadyToLand, "IsReadyToLand must deserialize.");
                    break;
                case GitHubPullRequestDetail pullRequest:
                    AssertEqual(42, pullRequest.Number);
                    AssertEqual("abc123", pullRequest.HeadSha);
                    break;
                case TokenUsageSummaryResult usage:
                    AssertEqual(123L, usage.TotalTokens);
                    break;
                case GitHubActionsSyncResult sync:
                    AssertEqual(3, sync.CreatedCount);
                    break;
                case DeploymentEnvironment environment:
                    AssertEqual("env_response", environment.Id);
                    break;
                case Deployment deployment:
                    AssertEqual("dpl_response", deployment.Id);
                    break;
                case Release release:
                    AssertEqual("rel_response", release.Id);
                    break;
                case CheckRun check:
                    AssertEqual("chk_response", check.Id);
                    break;
                case Job job:
                    AssertEqual("job_response", job.Id);
                    break;
            }
        }

        private static TestCaseDescriptor ContractCaseVoid(string id, string method, string path, Func<ArmadaApiClient, CancellationToken, Task> call)
        {
            return CaseAsync(id, "ArmadaApiClient " + id + " contract", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.NoContent, String.Empty);
                using (ArmadaApiClient client = CreateClient(handler))
                {
                await call(client, CancellationToken.None);
                AssertEqual(method, handler.Method);
                AssertEqual(path, handler.PathAndQuery);
                }
            });
        }

        private static ArmadaApiClient CreateClient(RecordingHandler handler)
        {
            return new ArmadaApiClient(new HttpClient(handler), "http://localhost:7890");
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor("Services.ArmadaApiClient", id, name, _ => body(), new List<string> { tag });
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _Status;
            private readonly string? _Content;
            private readonly Exception? _Error;

            public string Method { get; private set; } = String.Empty;
            public string PathAndQuery { get; private set; } = String.Empty;
            public string Body { get; private set; } = String.Empty;
            public bool CancellationObserved { get; private set; }

            public RecordingHandler(HttpStatusCode status, string content)
            {
                _Status = status;
                _Content = content;
            }

            public RecordingHandler(Exception error)
            {
                _Error = error;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Method = request.Method.Method;
                PathAndQuery = request.RequestUri!.PathAndQuery;
                Body = request.Content == null ? String.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                if (_Error is OperationCanceledException)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        CancellationObserved = cancellationToken.IsCancellationRequested;
                        throw;
                    }
                }
                if (_Error != null) throw _Error;
                return new HttpResponseMessage(_Status) { Content = new StringContent(_Content, Encoding.UTF8, "application/json") };
            }
        }
    }
}
