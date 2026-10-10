namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for McpMissionTools: verifies DefaultPlaybooks merge in armada_create_mission.
    /// </summary>
    public class McpMissionToolsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MCP Mission Tools";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MissionStatus_MissionCallerCannotReadUnrelatedMissionMetadata", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata otherTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("status other tenant")).ConfigureAwait(false);
                    UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster(Armada.Core.Constants.DefaultTenantId, "status-other@example.com", "password")).ConfigureAwait(false);
                    Mission callerMission = await testDb.Driver.Missions.CreateAsync(new Mission("status caller")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Description = "own mission brief is opt-in"
                    }).ConfigureAwait(false);
                    Mission directParent = await testDb.Driver.Missions.CreateAsync(new Mission("direct status parent")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Description = "authorized direct parent brief",
                        AgentOutput = "authorized direct parent output"
                    }).ConfigureAwait(false);
                    Mission directChild = await testDb.Driver.Missions.CreateAsync(new Mission("direct status child")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Description = "direct child brief is opt-in",
                        ParentMissionId = directParent.Id
                    }).ConfigureAwait(false);
                    Voyage rescueVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("status rescue chain")).ConfigureAwait(false);
                    Vessel rescueVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("status-rescue-vessel", "https://example.invalid/status-rescue.git")).ConfigureAwait(false);
                    string rescueVesselId = rescueVessel.Id;
                    Mission failedJudge = await testDb.Driver.Missions.CreateAsync(new Mission("failed status review")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        Description = "authorized failed review brief",
                        AgentOutput = "authorized failed review output",
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);
                    Mission rescueRoot = await testDb.Driver.Missions.CreateAsync(new Mission("status rescue root")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = rescueVoyage.Id,
                        ParentMissionId = failedJudge.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    Mission rescueJudge = await testDb.Driver.Missions.CreateAsync(new Mission("status rescue judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = rescueVoyage.Id,
                        DependsOnMissionId = rescueRoot.Id,
                        Description = RescueMissionMarker.Marker,
                        Persona = "Judge"
                    }).ConfigureAwait(false);
                    Mission unrelated = await testDb.Driver.Missions.CreateAsync(new Mission("private status target")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Description = "private mission instructions",
                        AgentOutput = "private mission output",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission foreignTenant = await testDb.Driver.Missions.CreateAsync(new Mission("private foreign tenant status")
                    {
                        TenantId = otherTenant.Id,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Description = "foreign tenant brief"
                    }).ConfigureAwait(false);
                    Mission foreignUser = await testDb.Driver.Missions.CreateAsync(new Mission("private foreign user status")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = otherUser.Id,
                        Description = "foreign user brief"
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? readStatus = null;
                    McpMissionTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_mission_status") readStatus = handler;
                    }, testDb.Driver, new RecordingAdmiralDouble(), null, null);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = callerMission.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        Mission ownSummary = (Mission)await readStatus!(JsonSerializer.SerializeToElement(new
                        {
                            missionId = callerMission.Id
                        })).ConfigureAwait(false);
                        AssertEqual(null, ownSummary.Description, "status omits the brief unless requested");
                        AssertEqual(null, ownSummary.AgentOutput, "status never returns the report body");
                    }
                    caller.MissionId = directChild.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        Mission directSummary = (Mission)await readStatus!(JsonSerializer.SerializeToElement(new
                        {
                            missionId = directParent.Id,
                            includeDescription = true
                        })).ConfigureAwait(false);
                        AssertEqual("authorized direct parent brief", directSummary.Description, "direct parent status remains readable");
                        AssertEqual(null, directSummary.AgentOutput, "status never returns the report body");

                        string result = JsonSerializer.Serialize(await readStatus!(JsonSerializer.SerializeToElement(new
                        {
                            missionId = unrelated.Id,
                            includeDescription = true
                        })).ConfigureAwait(false));
                        AssertContains("Mission not found", result, "mission status must apply the caller's evidence scope");
                        AssertFalse(result.Contains("private status target", StringComparison.Ordinal), "denied status must not disclose mission metadata");
                        AssertFalse(result.Contains("private mission instructions", StringComparison.Ordinal), "denied status must not disclose the description");
                        AssertFalse(result.Contains("private mission output", StringComparison.Ordinal), "denied status must not disclose agent output");
                        foreach (Mission deniedMission in new[] { foreignTenant, foreignUser })
                        {
                            string denied = JsonSerializer.Serialize(await readStatus!(JsonSerializer.SerializeToElement(new
                            {
                                missionId = deniedMission.Id,
                                includeDescription = true
                            })).ConfigureAwait(false));
                            AssertContains("Mission not found", denied, "mission status must preserve tenant and user boundaries");
                            AssertFalse(denied.Contains(deniedMission.Description!, StringComparison.Ordinal), "denied status must not disclose foreign descriptions");
                        }
                    }
                    caller.MissionId = rescueJudge.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        Mission rescuedSummary = (Mission)await readStatus!(JsonSerializer.SerializeToElement(new
                        {
                            missionId = failedJudge.Id,
                            includeDescription = true
                        })).ConfigureAwait(false);
                        AssertEqual("authorized failed review brief", rescuedSummary.Description,
                            "a rescue stage can inspect its root failed Judge metadata");
                        AssertEqual(null, rescuedSummary.AgentOutput, "rescued status does not return the report body");
                    }
                }
            });

            await RunTest("MissionEvidence_AdminOwnedCallerReadsOnlyRelatedRecords", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata otherTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("evidence other tenant")).ConfigureAwait(false);
                    UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster(Armada.Core.Constants.DefaultTenantId, "evidence-other@example.com", "password")).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("report chain")).ConfigureAwait(false);
                    Mission dependency = await testDb.Driver.Missions.CreateAsync(new Mission("dependency")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        AgentOutput = "dependency report"
                    }).ConfigureAwait(false);
                    Mission missionParent = await testDb.Driver.Missions.CreateAsync(new Mission("mission parent")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        AgentOutput = "parent report"
                    }).ConfigureAwait(false);
                    Mission own = await testDb.Driver.Missions.CreateAsync(new Mission("reader")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = dependency.Id,
                        ParentMissionId = missionParent.Id,
                        AgentOutput = "own final report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    string output = "complete report with unicode 🙂";
                    Mission predecessor = await testDb.Driver.Missions.CreateAsync(new Mission("predecessor")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        VoyageId = voyage.Id,
                        AgentOutput = output,
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission legacy = await testDb.Driver.Missions.CreateAsync(new Mission("legacy owner")
                    {
                        VoyageId = voyage.Id,
                        AgentOutput = "legacy report"
                    }).ConfigureAwait(false);
                    Mission unrelated = await testDb.Driver.Missions.CreateAsync(new Mission("unrelated")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        AgentOutput = "private unrelated report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission foreign = await testDb.Driver.Missions.CreateAsync(new Mission("foreign")
                    {
                        TenantId = otherTenant.Id,
                        VoyageId = voyage.Id,
                        AgentOutput = "foreign report"
                    }).ConfigureAwait(false);
                    Mission otherOwner = await testDb.Driver.Missions.CreateAsync(new Mission("other owner")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = otherUser.Id,
                        VoyageId = voyage.Id,
                        AgentOutput = "other owner report"
                    }).ConfigureAwait(false);
                    Objective parent = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "parent",
                        Description = new string('p', 20000)
                    }).ConfigureAwait(false);
                    Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "authorized work",
                        ParentObjectiveId = parent.Id,
                        MissionIds = new List<string> { own.Id },
                        EvidenceLinks = new List<string> { "full report reference" }
                    }).ConfigureAwait(false);
                    Objective otherObjective = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "unrelated work", TenantId = Armada.Core.Constants.DefaultTenantId }).ConfigureAwait(false);
                    Objective otherOwnerObjective = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "other owner work",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = otherUser.Id,
                        MissionIds = new List<string> { own.Id }
                    }).ConfigureAwait(false);
                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = own.Id;
                    Func<JsonElement?, Task<object>>? readOutput = null;
                    Func<JsonElement?, Task<object>>? readObjective = null;
                    McpMissionTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_mission_output") readOutput = handler;
                    }, testDb.Driver, new RecordingAdmiralDouble(), null, null);
                    McpObjectiveTools.Register((name, _, _, handler) =>
                    {
                        if (name == "get_objective") readObjective = handler;
                    }, testDb.Driver, new ObjectiveService(testDb.Driver));
                    using (McpCallerContext.Begin(caller))
                    {
                        string combined = String.Empty;
                        int offset = 0;
                        MissionOutputArtifactPage page;
                        do
                        {
                            page = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { missionId = predecessor.Id, offset, length = 7 })).ConfigureAwait(false);
                            combined += page.Content;
                            offset = page.NextOffset ?? page.TotalLength;
                        } while (page.HasMore);
                        AssertEqual(output, combined, "the captain reconstructs the complete predecessor artifact");
                        AssertTrue(page.Complete, "final output is complete");
                        AssertEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined))).ToLowerInvariant(), page.Sha256);
                        MissionOutputArtifactPage ownPage = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { missionId = own.Id })).ConfigureAwait(false);
                        AssertEqual("own final report", ownPage.Content);
                        AssertTrue(ownPage.Complete);
                        foreach (Mission source in new[] { dependency, missionParent })
                        {
                            MissionOutputArtifactPage sourcePage = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { missionId = source.Id })).ConfigureAwait(false);
                            AssertEqual(source.AgentOutput, sourcePage.Content, "direct and legacy report sources are readable");
                        }
                        foreach (string deniedId in new[] { unrelated.Id, foreign.Id, otherOwner.Id })
                        {
                            string result = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = deniedId })).ConfigureAwait(false));
                            AssertContains("Mission not found", result, "mission owner admin rights do not widen evidence scope");
                        }
                        MissionOutputArtifactPage legacyPage = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { missionId = legacy.Id })).ConfigureAwait(false);
                        AssertEqual("legacy report", legacyPage.Content, "null tenant and user normalize to default ownership");
                        Objective full = (Objective)await readObjective!(JsonSerializer.SerializeToElement(new { objectiveId = objective.Id })).ConfigureAwait(false);
                        AssertEqual("full report reference", full.EvidenceLinks[0]);
                        Objective fullParent = (Objective)await readObjective!(JsonSerializer.SerializeToElement(new { objectiveId = parent.Id })).ConfigureAwait(false);
                        AssertEqual(20000, fullParent.Description!.Length, "parent evidence is not a preview");
                        foreach (string deniedObjective in new[] { otherObjective.Id, otherOwnerObjective.Id })
                        {
                            string denied = JsonSerializer.Serialize(await readObjective!(JsonSerializer.SerializeToElement(new { objectiveId = deniedObjective })).ConfigureAwait(false));
                            AssertContains("Objective not found", denied);
                        }
                    }
                }
            });

            await RunTest("MissionEvidence_RescueChainCanReadFailedParentOutputButKeepsOwnerBoundary", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata otherTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("rescue evidence other tenant")).ConfigureAwait(false);
                    UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster(Armada.Core.Constants.DefaultTenantId, "rescue-evidence-other@example.com", "password")).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("rescue evidence chain")).ConfigureAwait(false);
                    Vessel rescueVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("rescue-evidence-vessel", "https://example.invalid/rescue-evidence.git")).ConfigureAwait(false);
                    string rescueVesselId = rescueVessel.Id;
                    string failedOutput = new string('r', 17000) + "\nfinal protected finding";
                    Mission failed = await testDb.Driver.Missions.CreateAsync(new Mission("failed review")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        AgentOutput = failedOutput,
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);
                    Mission rescueRoot = await testDb.Driver.Missions.CreateAsync(new Mission("rescue worker")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        ParentMissionId = failed.Id,
                        Description = RescueMissionMarker.Marker,
                        AgentOutput = "rescue work"
                    }).ConfigureAwait(false);
                    Mission intermediate = await testDb.Driver.Missions.CreateAsync(new Mission("rescue test engineer")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = rescueRoot.Id,
                        Description = RescueMissionMarker.Marker,
                        Persona = "TestEngineer"
                    }).ConfigureAwait(false);
                    Mission downstream = await testDb.Driver.Missions.CreateAsync(new Mission("rescue judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = intermediate.Id,
                        Description = RescueMissionMarker.Marker,
                        Persona = "Judge",
                        AgentOutput = "re-review"
                    }).ConfigureAwait(false);
                    Mission unrelated = await testDb.Driver.Missions.CreateAsync(new Mission("unrelated same-owner mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        AgentOutput = "unrelated same-owner output"
                    }).ConfigureAwait(false);
                    Mission intermediateRescueStage = await testDb.Driver.Missions.CreateAsync(new Mission("intermediate rescue stage with a valid dependency")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = rescueRoot.Id,
                        Description = RescueMissionMarker.Marker,
                        Persona = "TestEngineer"
                    }).ConfigureAwait(false);

                    Mission foreignTenant = await testDb.Driver.Missions.CreateAsync(new Mission("foreign tenant parent")
                    {
                        TenantId = otherTenant.Id,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        AgentOutput = "foreign tenant output"
                    }).ConfigureAwait(false);
                    Mission foreignTenantRoot = await testDb.Driver.Missions.CreateAsync(new Mission("mismatched tenant rescue root")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        ParentMissionId = foreignTenant.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    Mission tenantMismatchStage = await testDb.Driver.Missions.CreateAsync(new Mission("tenant mismatch rescue judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = foreignTenantRoot.Id
                    }).ConfigureAwait(false);
                    Mission foreignUser = await testDb.Driver.Missions.CreateAsync(new Mission("foreign user parent")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = otherUser.Id,
                        VesselId = rescueVesselId,
                        AgentOutput = "foreign user output"
                    }).ConfigureAwait(false);
                    Mission foreignUserRoot = await testDb.Driver.Missions.CreateAsync(new Mission("mismatched user rescue root")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        ParentMissionId = foreignUser.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    Mission userMismatchStage = await testDb.Driver.Missions.CreateAsync(new Mission("user mismatch rescue judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = foreignUserRoot.Id
                    }).ConfigureAwait(false);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = downstream.Id;
                    Func<JsonElement?, Task<object>>? readOutput = null;
                    McpMissionTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_mission_output") readOutput = handler;
                    }, testDb.Driver, new RecordingAdmiralDouble(), null, null);
                    using (McpCallerContext.Begin(caller))
                    {
                        string combined = String.Empty;
                        int offset = 0;
                        MissionOutputArtifactPage page;
                        do
                        {
                            page = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id, offset, length = 4096 })).ConfigureAwait(false);
                            combined += page.Content;
                            offset = page.NextOffset ?? page.TotalLength;
                        } while (page.HasMore);
                        AssertEqual(failedOutput, combined, "The final Judge can page the complete failed review through its rescue chain.");
                        AssertTrue(page.Complete, "The final page reports the full artifact as complete.");
                        AssertEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(failedOutput))).ToLowerInvariant(), page.Sha256,
                            "The final page carries the complete output digest.");
                        string unrelatedResult = JsonSerializer.Serialize(await readOutput(JsonSerializer.SerializeToElement(new { missionId = unrelated.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", unrelatedResult, "The rescue chain does not expose unrelated same-owner output.");

                    }
                    caller.MissionId = intermediate.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        MissionOutputArtifactPage page = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id, length = 64000 })).ConfigureAwait(false);
                        AssertEqual(failedOutput, page.Content, "An intermediate TestEngineer can read the failed review through its rescue chain.");
                    }
                    caller.MissionId = rescueRoot.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        MissionOutputArtifactPage page = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id, length = 64000 })).ConfigureAwait(false);
                        AssertEqual(failedOutput, page.Content, "The rescue Worker can read its failed parent output.");
                    }
                    caller.MissionId = intermediateRescueStage.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        MissionOutputArtifactPage page = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id, length = 64000 })).ConfigureAwait(false);
                        AssertEqual(failedOutput, page.Content, "A malformed intermediate parent link does not hide the actual rescue-root parent.");
                        string deniedMalformedParent = JsonSerializer.Serialize(await readOutput(JsonSerializer.SerializeToElement(new { missionId = unrelated.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedMalformedParent, "A rescue chain does not expose an unrelated same-owner mission.");
                    }
                    caller.MissionId = tenantMismatchStage.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string deniedTenant = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = foreignTenant.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedTenant, "A rescue chain cannot cross tenant ownership.");
                    }
                    caller.MissionId = userMismatchStage.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string deniedUser = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = foreignUser.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedUser, "A rescue chain cannot cross user ownership.");
                    }
                }
            });

            await RunTest("TransitionMissionStatus_CallerOutsideMissionOwnerIsRefusedWithoutChange", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("owned transition")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? transitionHandler = null;
                    // No transition service is supplied, so a caller who passes the scope check
                    // receives the unavailable refusal; only the scope check yields "Mission not found".
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_transition_mission_status") transitionHandler = handler; },
                        testDb.Driver,
                        new RecordingAdmiralDouble(),
                        null,
                        null);
                    AssertNotNull(transitionHandler, "armada_transition_mission_status handler must be registered");
                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id, status = "Assigned" });

                    AuthContext otherTenantUser = AuthContext.Authenticated("ten_other", "usr_other", false, false, "Test");
                    string foreignJson;
                    using (McpCallerContext.Begin(otherTenantUser))
                    {
                        foreignJson = JsonSerializer.Serialize(await transitionHandler!(args).ConfigureAwait(false));
                    }
                    AssertContains("Mission not found", foreignJson, "another tenant's caller cannot transition or discover the mission");

                    AuthContext otherTenantAdmin = AuthContext.Authenticated("ten_other", "usr_other_admin", false, true, "Test");
                    string foreignAdminJson;
                    using (McpCallerContext.Begin(otherTenantAdmin))
                    {
                        foreignAdminJson = JsonSerializer.Serialize(await transitionHandler!(args).ConfigureAwait(false));
                    }
                    AssertContains("Mission not found", foreignAdminJson, "a tenant administrator cannot transition another tenant's mission");

                    AuthContext ownerTenantAdmin = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, "usr_owner_admin", false, true, "Test");
                    string ownerJson;
                    using (McpCallerContext.Begin(ownerTenantAdmin))
                    {
                        ownerJson = JsonSerializer.Serialize(await transitionHandler!(args).ConfigureAwait(false));
                    }
                    AssertFalse(ownerJson.Contains("Mission not found", StringComparison.Ordinal),
                        "the owning tenant's administrator passes the scope check: " + ownerJson);

                    Mission? unchanged = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, unchanged!.Status, "a refused transition leaves the mission unchanged");
                }
            });

            await RunTest("CreateMission_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("mission-owner-tenant")).ConfigureAwait(false);
                    UserMaster user = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "mission-owner@example.com", "password")).ConfigureAwait(false);
                    AuthContext caller = AuthContext.Authenticated(tenant.Id, user.Id, false, false, "Test");
                    Vessel vessel = new Vessel("mission-owner-vessel", "https://github.com/test/mission-owner.git");
                    vessel.TenantId = tenant.Id;
                    vessel.UserId = user.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    Func<JsonElement?, Task<object>>? createHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_create_mission") createHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);
                    AssertNotNull(createHandler, "armada_create_mission handler must be registered");

                    using (McpCallerContext.Begin(caller))
                    {
                        await createHandler!(JsonSerializer.SerializeToElement(new { title = "owned", description = "owned mission", vesselId = vessel.Id })).ConfigureAwait(false);
                    }

                    AssertNotNull(admiralDouble.LastDispatched, "the mission reaches dispatch");
                    AssertEqual(tenant.Id, admiralDouble.LastDispatched!.TenantId, "the mission belongs to the caller's tenant");
                    AssertEqual(user.Id, admiralDouble.LastDispatched.UserId, "the mission belongs to the calling user");
                }
            });

            await RunTest("RestartMission_ProgressSignalIsOwnedLikeTheMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("restart-owner-tenant")).ConfigureAwait(false);
                    UserMaster user = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "restart-owner@example.com", "password")).ConfigureAwait(false);
                    Vessel vessel = new Vessel("restart-vessel", "https://github.com/test/restart.git");
                    vessel.TenantId = tenant.Id;
                    vessel.UserId = user.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("restart owned")
                    {
                        TenantId = tenant.Id,
                        UserId = user.Id,
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? restartHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_restart_mission") restartHandler = handler; },
                        testDb.Driver,
                        new RecordingAdmiralDouble(),
                        null,
                        null);
                    AssertNotNull(restartHandler, "armada_restart_mission handler must be registered");

                    string json;
                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        json = JsonSerializer.Serialize(await restartHandler!(JsonSerializer.SerializeToElement(new { missionId = mission.Id })).ConfigureAwait(false));
                    }

                    List<Signal> signals = await testDb.Driver.Signals.EnumerateRecentAsync(100).ConfigureAwait(false);
                    Signal? restarted = signals.Find(s => (s.Payload ?? String.Empty).Contains(mission.Id + " restarted", StringComparison.Ordinal));
                    AssertNotNull(restarted, "the restart writes a progress signal: " + json);
                    AssertEqual(tenant.Id, restarted!.TenantId, "the restart signal belongs to the mission's tenant");
                    AssertEqual(user.Id, restarted.UserId, "the restart signal belongs to the mission's user");
                }
            });

            await RunTest("UpdateMission_RefusesVesselOrVoyageRebinding_AndWritesNothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("bound-vessel", "https://github.com/test/bound.git")).ConfigureAwait(false);
                    Vessel otherVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("other-vessel", "https://github.com/test/other.git")).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("bound voyage")).ConfigureAwait(false);
                    Voyage otherVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("other voyage")).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("bound mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_mission") updateHandler = handler; },
                        testDb.Driver,
                        new RecordingAdmiralDouble(),
                        null,
                        null);
                    AssertNotNull(updateHandler, "armada_update_mission handler must be registered");

                    foreach (object args in new object[]
                    {
                        new { missionId = mission.Id, title = "moved", vesselId = otherVessel.Id },
                        new { missionId = mission.Id, title = "moved", voyageId = otherVoyage.Id }
                    })
                    {
                        string json;
                        using (McpCallerContext.Begin(McpTestCaller.Operator))
                        {
                            json = JsonSerializer.Serialize(await updateHandler!(JsonSerializer.SerializeToElement(args)).ConfigureAwait(false));
                        }
                        AssertContains("cannot be changed", json, "a rebinding update is refused like REST and WebSocket: " + json);
                        Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(vessel.Id, stored!.VesselId, "the refused update keeps the vessel");
                        AssertEqual(voyage.Id, stored.VoyageId, "the refused update keeps the voyage");
                        AssertEqual("bound mission", stored.Title, "the refused update writes no other field");
                    }

                    string sameBinding;
                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        sameBinding = JsonSerializer.Serialize(await updateHandler!(JsonSerializer.SerializeToElement(new { missionId = mission.Id, title = "renamed", vesselId = vessel.Id, voyageId = voyage.Id })).ConfigureAwait(false));
                    }
                    Mission? renamed = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual("renamed", renamed!.Title, "an update naming the current bindings is accepted: " + sameBinding);
                }
            });

            await RunTest("UpdateMission_ReadsTheMissionAndItsLinksInTheCallersScope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("scoped update")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);
                    TenantMetadata otherTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("Other MCP tenant")).ConfigureAwait(false);
                    Mission foreign = await testDb.Driver.Missions.CreateAsync(new Mission("foreign link")
                    {
                        TenantId = otherTenant.Id,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_mission") updateHandler = handler; },
                        testDb.Driver,
                        new RecordingAdmiralDouble(),
                        null,
                        null);
                    AssertNotNull(updateHandler, "armada_update_mission handler must be registered");

                    AuthContext otherTenantAdmin = AuthContext.Authenticated(otherTenant.Id, "usr_other_admin", false, true, "Test");
                    string foreignJson;
                    using (McpCallerContext.Begin(otherTenantAdmin))
                    {
                        foreignJson = JsonSerializer.Serialize(await updateHandler!(JsonSerializer.SerializeToElement(new { missionId = mission.Id, title = "taken over" })).ConfigureAwait(false));
                    }
                    AssertContains("Mission not found", foreignJson, "another tenant's administrator cannot change the mission");

                    AuthContext ownerTenantAdmin = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, "usr_owner_admin", false, true, "Test");
                    foreach (object args in new object[]
                    {
                        new { missionId = mission.Id, dependsOnMissionId = foreign.Id },
                        new { missionId = mission.Id, parentMissionId = foreign.Id }
                    })
                    {
                        string linkJson;
                        using (McpCallerContext.Begin(ownerTenantAdmin))
                        {
                            linkJson = JsonSerializer.Serialize(await updateHandler!(JsonSerializer.SerializeToElement(args)).ConfigureAwait(false));
                        }
                        AssertContains("not found", linkJson, "a link to another tenant's mission is refused as missing: " + linkJson);
                    }

                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual("scoped update", stored!.Title, "the refused update writes nothing");
                    AssertNull(stored.DependsOnMissionId, "the refused dependency is not stored");
                    AssertNull(stored.ParentMissionId, "the refused parent is not stored");
                }
            });

            await RunTest("MissionOutput_ReturnsPersistedDigestBackedPage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("persisted output")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        AgentOutput = "page one 🙂 page two",
                        Status = MissionStatusEnum.Complete
                    };
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? outputHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_output") outputHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        new RecordingAdmiralDouble(),
                        null,
                        null);

                    AssertNotNull(outputHandler, "armada_mission_output handler must be registered");
                    object result = await outputHandler!(JsonSerializer.SerializeToElement(new
                    {
                        missionId = mission.Id,
                        offset = 0,
                        length = 9
                    })).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);
                    AssertContains("mission-output:" + mission.Id, json);
                    AssertContains("\"HasMore\":true", json);
                    AssertContains("\"Complete\":true", json);
                    AssertContains("\"Sha256\":", json);
                }
            });

            await RunTest("CreateMission_VesselWithDefaultPlaybooks_MergesIntoMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("msn-dp-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    string defaultPlaybooksJson = "[{\"playbookId\":\"pbk_msn1\",\"deliveryMode\":\"InlineFullContent\"},{\"playbookId\":\"pbk_msn2\",\"deliveryMode\":\"AttachIntoWorktree\"}]";
                    vessel.DefaultPlaybooks = defaultPlaybooksJson;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    Func<JsonElement?, Task<object>>? createHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_create_mission") createHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(createHandler, "armada_create_mission handler must be registered");

                    // Caller passes no selectedPlaybooks -- vessel defaults should be applied.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "test mission",
                        description = "test desc",
                        vesselId = vessel.Id
                    });
                    object result = await createHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                    AssertNotNull(admiralDouble.LastDispatched, "DispatchMissionAsync should have been called");
                    AssertEqual(2, admiralDouble.LastDispatched!.SelectedPlaybooks.Count, "Both vessel defaults should be in SelectedPlaybooks");
                    AssertEqual("pbk_msn1", admiralDouble.LastDispatched.SelectedPlaybooks[0].PlaybookId);
                    AssertEqual("pbk_msn2", admiralDouble.LastDispatched.SelectedPlaybooks[1].PlaybookId);
                }
            });

            await RunTest("CreateMission_CallerPlaybooksOverrideDefaultOnCollision", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("msn-coll-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    string defaultPlaybooksJson = "[{\"playbookId\":\"pbk_shared\",\"deliveryMode\":\"InlineFullContent\"}]";
                    vessel.DefaultPlaybooks = defaultPlaybooksJson;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    RecordingAdmiralDouble admiralDouble = new RecordingAdmiralDouble();
                    Func<JsonElement?, Task<object>>? createHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_create_mission") createHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(createHandler);

                    // Caller supplies pbk_shared with different deliveryMode -- should override default.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "collision test",
                        description = "collision desc",
                        vesselId = vessel.Id,
                        selectedPlaybooks = new[]
                        {
                            new { playbookId = "pbk_shared", deliveryMode = "AttachIntoWorktree" }
                        }
                    });
                    object result = await createHandler!(args).ConfigureAwait(false);

                    AssertNotNull(admiralDouble.LastDispatched);
                    AssertEqual(1, admiralDouble.LastDispatched!.SelectedPlaybooks.Count, "Collision merges to one entry");
                    AssertEqual("pbk_shared", admiralDouble.LastDispatched.SelectedPlaybooks[0].PlaybookId);
                    AssertEqual(PlaybookDeliveryModeEnum.AttachIntoWorktree, admiralDouble.LastDispatched.SelectedPlaybooks[0].DeliveryMode,
                        "Caller deliveryMode should override default on collision");
                }
            });
        }

        private sealed class RecordingAdmiralDouble : IAdmiralService
        {
            /// <summary>The last mission passed to DispatchMissionAsync.</summary>
            public Mission? LastDispatched { get; private set; }

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                LastDispatched = mission;
                mission.Status = MissionStatusEnum.InProgress;
                return Task.FromResult(mission);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();
            public Task StopAllAgentProcessesAsync(CancellationToken token = default) => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HandleProcessExitAsync(
                int processId, int? exitCode, string captainId, string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
