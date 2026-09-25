namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end descriptors for workflow profiles and structured check runs, ported 1:1 from the
    /// retired automated WorkflowProfileCheckRunTests suite. Cases share the singleton e2e server
    /// fixture and carry created vessel, profile, and check-run identifiers across cases; the
    /// working directory is provisioned by the first case and torn down by a trailing cleanup case,
    /// mirroring the legacy try/finally teardown.
    /// </summary>
    public sealed class WorkflowProfileCheckRunSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.WorkflowProfileCheckRun";

        private string _WorkingDirectory = String.Empty;
        private string _VesselId = String.Empty;
        private string _GlobalProfileId = String.Empty;
        private string _VesselProfileId = String.Empty;
        private string _MissingInputProfileId = String.Empty;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Workflow Profiles and Checks suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("workflow_profiles_create_resolve_update_and_enumerate", "WorkflowProfiles_CreateResolveUpdateAndEnumerate", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                _WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada-workflow-checks-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(_WorkingDirectory, "artifacts"));
                await File.WriteAllTextAsync(Path.Combine(_WorkingDirectory, "artifacts", "existing.txt"), "artifact").ConfigureAwait(false);

                HttpResponseMessage vesselResponse = await authClient.PostAsync("/api/v1/vessels",
                    JsonHelper.ToJsonContent(new
                    {
                        Name = "Workflow Check Vessel",
                        RepoUrl = "file:///tmp/workflow-check-vessel.git",
                        LocalPath = _WorkingDirectory,
                        WorkingDirectory = _WorkingDirectory,
                        DefaultBranch = "main",
                        RequirePassingChecksToLand = true
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, vesselResponse.StatusCode);

                Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
                _VesselId = vessel.Id;
                AssertStartsWith("vsl_", _VesselId);

                HttpResponseMessage globalProfileResponse = await authClient.PostAsync("/api/v1/workflow-profiles",
                    JsonHelper.ToJsonContent(new
                    {
                        Name = "Global Workflow Profile",
                        Scope = WorkflowProfileScopeEnum.Global,
                        BuildCommand = "dotnet --version"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, globalProfileResponse.StatusCode);

                WorkflowProfile globalProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(globalProfileResponse).ConfigureAwait(false);
                _GlobalProfileId = globalProfile.Id;
                AssertStartsWith("wfp_", _GlobalProfileId);

                HttpResponseMessage vesselProfileResponse = await authClient.PostAsync("/api/v1/workflow-profiles",
                    JsonHelper.ToJsonContent(new
                    {
                        Name = "Vessel Workflow Profile",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = _VesselId,
                        IsDefault = true,
                        BuildCommand = "dotnet --version",
                        UnitTestCommand = "dotnet --version",
                        ExpectedArtifacts = new[] { "artifacts/existing.txt" },
                        Environments = new[]
                        {
                            new
                            {
                                EnvironmentName = "staging",
                                DeployCommand = "echo deploy-staging"
                            }
                        }
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, vesselProfileResponse.StatusCode);

                WorkflowProfile vesselProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(vesselProfileResponse).ConfigureAwait(false);
                _VesselProfileId = vesselProfile.Id;

                HttpResponseMessage resolveResponse = await authClient.GetAsync("/api/v1/workflow-profiles/resolve/vessels/" + _VesselId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, resolveResponse.StatusCode);

                WorkflowProfile resolved = await JsonHelper.DeserializeAsync<WorkflowProfile>(resolveResponse).ConfigureAwait(false);
                AssertEqual(_VesselProfileId, resolved.Id);

                HttpResponseMessage previewResponse = await authClient.GetAsync("/api/v1/workflow-profiles/preview/vessels/" + _VesselId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, previewResponse.StatusCode);

                WorkflowProfileResolutionPreviewResult preview = await JsonHelper.DeserializeAsync<WorkflowProfileResolutionPreviewResult>(previewResponse).ConfigureAwait(false);
                AssertNotNull(preview.ResolvedProfile);
                AssertEqual(_VesselProfileId, preview.ResolvedProfile!.Id);
                AssertEqual(WorkflowProfileResolutionModeEnum.Vessel, preview.ResolutionMode);
                AssertTrue(preview.AvailableCheckTypes.Exists(type => type == CheckRunTypeEnum.Build.ToString()), "Expected build check type in preview.");
                AssertTrue(preview.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.Build && command.Command == "dotnet --version"), "Expected build command preview.");
                AssertTrue(preview.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.Deploy && command.EnvironmentName == "staging" && command.Command == "echo deploy-staging"), "Expected staging deploy preview.");

                HttpResponseMessage listResponse = await authClient.GetAsync("/api/v1/workflow-profiles?vesselId=" + Uri.EscapeDataString(_VesselId) + "&pageSize=100").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);
                EnumerationResult<WorkflowProfile> profiles = await JsonHelper.DeserializeAsync<EnumerationResult<WorkflowProfile>>(listResponse).ConfigureAwait(false);
                AssertTrue(profiles.Objects.Count >= 1);

                HttpResponseMessage updateResponse = await authClient.PutAsync("/api/v1/workflow-profiles/" + _VesselProfileId,
                    JsonHelper.ToJsonContent(new
                    {
                        Id = _VesselProfileId,
                        Name = "Vessel Workflow Profile Updated",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = _VesselId,
                        IsDefault = true,
                        BuildCommand = "dotnet --version",
                        UnitTestCommand = "dotnet --version",
                        ExpectedArtifacts = new[] { "artifacts/existing.txt" },
                        Environments = new[]
                        {
                            new
                            {
                                EnvironmentName = "staging",
                                DeployCommand = "echo deploy-staging"
                            }
                        }
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, updateResponse.StatusCode);

                WorkflowProfile updated = await JsonHelper.DeserializeAsync<WorkflowProfile>(updateResponse).ConfigureAwait(false);
                AssertEqual("Vessel Workflow Profile Updated", updated.Name);
            }));

            cases.Add(CaseAsync("vessel_readiness_and_check_run_block_when_required_input_is_missing", "VesselReadiness_And_CheckRun_Block_When_Required_Input_Is_Missing", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string missingVariable = "ARMADA_AUTOMATED_INPUT_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                string stagingVariable = "ARMADA_AUTOMATED_STAGING_" + Guid.NewGuid().ToString("N").ToUpperInvariant();

                HttpResponseMessage profileResponse = await authClient.PostAsync("/api/v1/workflow-profiles",
                    JsonHelper.ToJsonContent(new
                    {
                        Name = "Missing Input Workflow Profile",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = _VesselId,
                        BuildCommand = "dotnet --version",
                        Environments = new[]
                        {
                            new
                            {
                                EnvironmentName = "staging",
                                DeployCommand = "echo deploy-staging"
                            }
                        },
                        RequiredInputs = new[]
                        {
                            new
                            {
                                Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                                Key = missingVariable,
                                EnvironmentName = (string?)null,
                                Description = (string?)null
                            },
                            new
                            {
                                Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                                Key = stagingVariable,
                                EnvironmentName = (string?)"staging",
                                Description = (string?)"Staging deploy token"
                            },
                            new
                            {
                                Provider = WorkflowInputReferenceProviderEnum.OnePassword,
                                Key = "op://armada/staging/deploy-token",
                                EnvironmentName = (string?)"staging",
                                Description = (string?)"Provider-backed deploy token"
                            }
                        }
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, profileResponse.StatusCode);

                WorkflowProfile missingInputProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(profileResponse).ConfigureAwait(false);
                _MissingInputProfileId = missingInputProfile.Id;
                AssertTrue(missingInputProfile.RequiredInputs.Count == 3, "Expected required inputs to round-trip.");
                AssertEqual(WorkflowInputReferenceProviderEnum.OnePassword, missingInputProfile.RequiredInputs[2].Provider);
                AssertEqual("staging", missingInputProfile.RequiredInputs[2].EnvironmentName);
                AssertEqual("Provider-backed deploy token", missingInputProfile.RequiredInputs[2].Description);

                HttpResponseMessage readinessResponse = await authClient.GetAsync(
                    "/api/v1/vessels/" + _VesselId
                    + "/readiness?workflowProfileId=" + Uri.EscapeDataString(_MissingInputProfileId)
                    + "&checkType=Build").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, readinessResponse.StatusCode);

                VesselReadinessResult readiness = await JsonHelper.DeserializeAsync<VesselReadinessResult>(readinessResponse).ConfigureAwait(false);
                AssertFalse(readiness.IsReady);
                AssertTrue(readiness.ErrorCount >= 1);
                AssertTrue(readiness.Issues.Exists(issue => issue.Code == "required_input_missing"), "Expected a required_input_missing readiness issue.");
                AssertContains(missingVariable, String.Join(" ", readiness.Issues.ConvertAll(issue => issue.Message)));
                AssertFalse(readiness.Issues.Exists(issue => issue.Message.Contains(stagingVariable, StringComparison.OrdinalIgnoreCase)), "Build readiness should ignore staging-scoped inputs.");

                HttpResponseMessage deployReadinessResponse = await authClient.GetAsync(
                    "/api/v1/vessels/" + _VesselId
                    + "/readiness?workflowProfileId=" + Uri.EscapeDataString(_MissingInputProfileId)
                    + "&checkType=Deploy&environmentName=staging").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, deployReadinessResponse.StatusCode);

                VesselReadinessResult deployReadiness = await JsonHelper.DeserializeAsync<VesselReadinessResult>(deployReadinessResponse).ConfigureAwait(false);
                AssertTrue(deployReadiness.Issues.Exists(issue => issue.Message.Contains(stagingVariable, StringComparison.OrdinalIgnoreCase)), "Deploy readiness should include staging-scoped inputs.");

                HttpResponseMessage blockedRunResponse = await authClient.PostAsync("/api/v1/check-runs",
                    JsonHelper.ToJsonContent(new
                    {
                        VesselId = _VesselId,
                        WorkflowProfileId = _MissingInputProfileId,
                        Type = CheckRunTypeEnum.Build
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, blockedRunResponse.StatusCode);

                string blockedBody = await blockedRunResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                AssertContains(missingVariable, blockedBody);
                AssertFalse(blockedBody.Contains(stagingVariable, StringComparison.OrdinalIgnoreCase), "Build check failure should not mention staging-scoped inputs.");
            }));

            cases.Add(CaseAsync("workflow_profile_check_run_cleanup_resources", "WorkflowProfileCheckRun_CleanupResources", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                if (!String.IsNullOrWhiteSpace(_VesselProfileId))
                {
                    try { await authClient.DeleteAsync("/api/v1/workflow-profiles/" + _VesselProfileId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(_MissingInputProfileId))
                {
                    try { await authClient.DeleteAsync("/api/v1/workflow-profiles/" + _MissingInputProfileId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(_GlobalProfileId))
                {
                    try { await authClient.DeleteAsync("/api/v1/workflow-profiles/" + _GlobalProfileId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(_VesselId))
                {
                    try { await authClient.DeleteAsync("/api/v1/vessels/" + _VesselId).ConfigureAwait(false); } catch { }
                }

                try
                {
                    if (!String.IsNullOrWhiteSpace(_WorkingDirectory) && Directory.Exists(_WorkingDirectory))
                        Directory.Delete(_WorkingDirectory, true);
                }
                catch
                {
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Workflow Profiles and Checks",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
