namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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
                        ReviewComment = "review detail " + ("sk-" + new string('R', 24)),
                        FailureReason = "provider detail " + ("sk-" + new string('F', 24)),
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
                        AssertEqual("review detail sk-[REDACTED]", rescuedSummary.ReviewComment,
                            "status redacts secret-shaped review feedback before it leaves the mission boundary");
                        AssertEqual("provider detail sk-[REDACTED]", rescuedSummary.FailureReason,
                            "status redacts secret-shaped failure text before it leaves the mission boundary");
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

            await RunTest("MissionReads_OmittedIdsDefaultToTheCallerOwnMissionAndObjective", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission own = await testDb.Driver.Missions.CreateAsync(new Mission("own stage")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        AgentOutput = "own stage report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "own work",
                        MissionIds = new List<string> { own.Id }
                    }).ConfigureAwait(false);
                    Func<JsonElement?, Task<object>>? readStatus = null;
                    Func<JsonElement?, Task<object>>? readOutput = null;
                    Func<JsonElement?, Task<object>>? readObjective = null;
                    McpMissionTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_mission_status") readStatus = handler;
                        if (name == "armada_mission_output") readOutput = handler;
                    }, testDb.Driver, new RecordingAdmiralDouble(), null, null);
                    McpObjectiveTools.Register((name, _, _, handler) =>
                    {
                        if (name == "get_objective") readObjective = handler;
                    }, testDb.Driver, new ObjectiveService(testDb.Driver));

                    AuthContext captain = McpTestCaller.Operator;
                    captain.MissionId = own.Id;
                    using (McpCallerContext.Begin(captain))
                    {
                        string status = JsonSerializer.Serialize(await readStatus!(JsonSerializer.SerializeToElement(new { missionId = (string?)null })).ConfigureAwait(false));
                        AssertContains(own.Id, status, "a null missionId reads the captain's own mission");
                        MissionOutputArtifactPage page = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false);
                        AssertEqual("own stage report", page.Content, "an omitted missionId reads the captain's own output");
                        Objective read = (Objective)await readObjective!(JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false);
                        AssertEqual(objective.Id, read.Id, "an omitted objectiveId reads the objective that authorizes the captain's mission");

                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("bound work")).ConfigureAwait(false);
                        own.VoyageId = voyage.Id;
                        await testDb.Driver.Missions.UpdateAsync(own).ConfigureAwait(false);
                        await testDb.Driver.Objectives.CreateAsync(new Objective
                        {
                            Title = "Legacy voyage listing",
                            VoyageIds = new List<string> { voyage.Id }
                        }).ConfigureAwait(false);
                        read = (Objective)await readObjective!(JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false);
                        AssertEqual(objective.Id, read.Id, "a markerless legacy voyage retains direct mission precedence");
                        voyage.Description = ObjectiveBriefRenderer.Render(objective);
                        await testDb.Driver.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
                        objective.MissionIds.Clear();
                        objective.VoyageIds.Add(voyage.Id);
                        await testDb.Driver.Objectives.UpdateAsync(objective).ConfigureAwait(false);
                        await testDb.Driver.Objectives.CreateAsync(new Objective
                        {
                            Title = "Later diagnostic",
                            MissionIds = new List<string> { own.Id },
                            VoyageIds = new List<string> { voyage.Id },
                            CreatedUtc = objective.CreatedUtc.AddMinutes(1)
                        }).ConfigureAwait(false);
                        read = (Objective)await readObjective!(JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false);
                        AssertEqual(objective.Id, read.Id, "default reads use the voyage authorizer before a later direct diagnostic");
                    }

                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        string status = JsonSerializer.Serialize(await readStatus!(JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false));
                        AssertContains("missionId is required", status, "an operator with no mission gets an error naming the missing argument");
                        string output = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false));
                        AssertContains("missionId is required", output, "the output read names the missing argument");
                        string objectiveRead = JsonSerializer.Serialize(await readObjective!(JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false));
                        AssertContains("objectiveId is required", objectiveRead, "the objective read names the missing argument");
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
                    Voyage otherVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("other rescue evidence chain")).ConfigureAwait(false);
                    Vessel rescueVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("rescue-evidence-vessel", "https://example.invalid/rescue-evidence.git")).ConfigureAwait(false);
                    string rescueVesselId = rescueVessel.Id;
                    Vessel otherRescueVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("other-rescue-evidence-vessel", "https://example.invalid/other-rescue-evidence.git")).ConfigureAwait(false);
                    string failedOutput = new string('r', 17000) + "\nfinal protected finding";
                    Mission failed = await testDb.Driver.Missions.CreateAsync(new Mission("failed review")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        Description = "failed review brief",
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
                    Mission legacyTitleTarget = await testDb.Driver.Missions.CreateAsync(new Mission("legacy title-only parent")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        AgentOutput = "legacy-title-only private output"
                    }).ConfigureAwait(false);
                    Mission legacyTitleRoot = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue: legacy title-only root")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        ParentMissionId = legacyTitleTarget.Id,
                        Description = "ordinary text without the rescue marker"
                    }).ConfigureAwait(false);
                    Mission legacyTitleDependent = await testDb.Driver.Missions.CreateAsync(new Mission("dependent on legacy title-only root")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = legacyTitleRoot.Id
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
                    Func<JsonElement?, Task<object>>? readStatus = null;
                    McpMissionTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_mission_output") readOutput = handler;
                        if (name == "armada_mission_status") readStatus = handler;
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
                        string deniedUnrelated = JsonSerializer.Serialize(await readOutput(JsonSerializer.SerializeToElement(new { missionId = unrelated.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedUnrelated, "A rescue chain does not expose an unrelated same-owner mission.");
                    }
                    caller.MissionId = rescueRoot.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        MissionOutputArtifactPage page = (MissionOutputArtifactPage)await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id, length = 64000 })).ConfigureAwait(false);
                        AssertEqual(failedOutput, page.Content, "The rescue Worker can read its failed parent output.");
                    }

                    Mission longChainStage = rescueRoot;
                    for (int i = 0; i < 34; i++)
                    {
                        longChainStage = await testDb.Driver.Missions.CreateAsync(new Mission("long rescue stage " + i)
                        {
                            TenantId = Armada.Core.Constants.DefaultTenantId,
                            UserId = Armada.Core.Constants.DefaultUserId,
                            VesselId = rescueVesselId,
                            VoyageId = voyage.Id,
                            DependsOnMissionId = longChainStage.Id,
                            Description = RescueMissionMarker.Marker
                        }).ConfigureAwait(false);
                    }
                    caller.MissionId = longChainStage.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        object deepOutputResult = await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id, length = 64000 })).ConfigureAwait(false);
                        AssertTrue(deepOutputResult is MissionOutputArtifactPage,
                            "A same-owner rescue stage more than 32 links from the root can read output; actual result: " + JsonSerializer.Serialize(deepOutputResult));
                        MissionOutputArtifactPage page = (MissionOutputArtifactPage)deepOutputResult;
                        AssertEqual(failedOutput, page.Content,
                            "A same-owner rescue stage more than 32 links from the root can read the complete failed output.");
                        AssertTrue(page.Complete, "The long-chain output read is complete.");

                        object deepStatusResult = await readStatus!(JsonSerializer.SerializeToElement(new
                        {
                            missionId = failed.Id,
                            includeDescription = true
                        })).ConfigureAwait(false);
                        AssertTrue(deepStatusResult is Mission,
                            "A same-owner rescue stage more than 32 links from the root can read status; actual result: " + JsonSerializer.Serialize(deepStatusResult));
                        Mission failedStatus = (Mission)deepStatusResult;
                        AssertEqual(failed.Description, failedStatus.Description,
                            "A same-owner rescue stage more than 32 links from the root can read failed-mission status.");

                        string deniedDeepTarget = JsonSerializer.Serialize(await readOutput(JsonSerializer.SerializeToElement(new { missionId = unrelated.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedDeepTarget,
                            "Long-chain scope does not expose an unrelated same-owner report.");
                    }
                    Mission missingLinkStage = await testDb.Driver.Missions.CreateAsync(new Mission("rescue stage with missing predecessor")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = "msn_missing",
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    caller.MissionId = missingLinkStage.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string deniedMissingLink = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedMissingLink,
                            "A missing predecessor stops the rescue scope walk.");
                    }
                    Mission cycleFirst = await testDb.Driver.Missions.CreateAsync(new Mission("rescue cycle first stage")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    Mission cycleSecond = await testDb.Driver.Missions.CreateAsync(new Mission("rescue cycle second stage")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = cycleFirst.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    cycleFirst.DependsOnMissionId = cycleSecond.Id;
                    await testDb.Driver.Missions.UpdateAsync(cycleFirst).ConfigureAwait(false);
                    caller.MissionId = cycleFirst.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string deniedCycle = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedCycle,
                            "A dependency cycle stops the rescue scope walk.");
                    }
                    Mission crossVesselRoot = await testDb.Driver.Missions.CreateAsync(new Mission("rescue root on another vessel")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = otherRescueVessel.Id,
                        VoyageId = voyage.Id,
                        ParentMissionId = failed.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    Mission crossVesselStage = await testDb.Driver.Missions.CreateAsync(new Mission("stage depending across vessels")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = crossVesselRoot.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    caller.MissionId = crossVesselStage.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string deniedCrossVessel = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedCrossVessel,
                            "A predecessor from another vessel stops the rescue scope walk.");
                    }
                    Mission crossVoyageRoot = await testDb.Driver.Missions.CreateAsync(new Mission("rescue root in another voyage")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = otherVoyage.Id,
                        ParentMissionId = failed.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    Mission crossVoyageStage = await testDb.Driver.Missions.CreateAsync(new Mission("stage depending across voyages")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = rescueVesselId,
                        VoyageId = voyage.Id,
                        DependsOnMissionId = crossVoyageRoot.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    caller.MissionId = crossVoyageStage.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string deniedCrossVoyage = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = failed.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedCrossVoyage,
                            "A predecessor from another voyage stops the rescue scope walk.");
                    }
                    caller.MissionId = legacyTitleDependent.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string deniedLegacyTitleParent = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = legacyTitleTarget.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedLegacyTitleParent,
                            "a legacy title prefix alone cannot grant a rescue ancestor read");
                        AssertFalse(deniedLegacyTitleParent.Contains("legacy-title-only private output", StringComparison.Ordinal),
                            "a legacy title-only chain cannot disclose the parent output");
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

            await RunTest("MissionEvidence_SameObjectiveCallerReadsEarlierVoyageMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage earlierVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("earlier objective voyage")).ConfigureAwait(false);
                    Voyage laterVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("later objective voyage")).ConfigureAwait(false);
                    Voyage markedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("brief-rendered objective voyage")).ConfigureAwait(false);
                    Mission earlier = await testDb.Driver.Missions.CreateAsync(new Mission("earlier voyage worker")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageId = earlierVoyage.Id,
                        Description = "earlier voyage brief",
                        AgentOutput = "earlier voyage report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission listedMission = await testDb.Driver.Missions.CreateAsync(new Mission("objective-listed mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        AgentOutput = "objective-listed report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("later voyage judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageId = laterVoyage.Id,
                        Persona = "Judge"
                    }).ConfigureAwait(false);
                    Mission markedJudge = await testDb.Driver.Missions.CreateAsync(new Mission("brief-rendered voyage judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageId = markedVoyage.Id,
                        Persona = "Judge"
                    }).ConfigureAwait(false);
                    Objective byVoyages = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "objective listing both voyages",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageIds = new List<string> { earlierVoyage.Id, laterVoyage.Id, markedVoyage.Id }
                    }).ConfigureAwait(false);
                    Objective byMissions = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "objective listing both missions",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        MissionIds = new List<string> { listedMission.Id, judge.Id }
                    }).ConfigureAwait(false);
                    markedVoyage.Description = "<!-- armada-objective-brief:" + byVoyages.Id + " -->\nrendered brief";
                    await testDb.Driver.Voyages.UpdateAsync(markedVoyage).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? readOutput = null;
                    Func<JsonElement?, Task<object>>? readStatus = null;
                    McpMissionTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_mission_output") readOutput = handler;
                        if (name == "armada_mission_status") readStatus = handler;
                    }, testDb.Driver, new RecordingAdmiralDouble(), null, null);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = judge.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        object outputResult = await readOutput!(JsonSerializer.SerializeToElement(new { missionId = earlier.Id })).ConfigureAwait(false);
                        AssertTrue(outputResult is MissionOutputArtifactPage,
                            "A Judge reads the output of an earlier voyage of its objective; actual result: " + JsonSerializer.Serialize(outputResult));
                        AssertEqual("earlier voyage report", ((MissionOutputArtifactPage)outputResult).Content);

                        object statusResult = await readStatus!(JsonSerializer.SerializeToElement(new { missionId = earlier.Id, includeDescription = true })).ConfigureAwait(false);
                        AssertTrue(statusResult is Mission,
                            "A Judge reads the status of an earlier voyage of its objective; actual result: " + JsonSerializer.Serialize(statusResult));
                        AssertEqual("earlier voyage brief", ((Mission)statusResult).Description);

                        object listedResult = await readOutput!(JsonSerializer.SerializeToElement(new { missionId = listedMission.Id })).ConfigureAwait(false);
                        AssertTrue(listedResult is MissionOutputArtifactPage,
                            "An objective that lists both missions authorizes the read; actual result: " + JsonSerializer.Serialize(listedResult));
                        AssertEqual("objective-listed report", ((MissionOutputArtifactPage)listedResult).Content);
                    }
                    caller.MissionId = markedJudge.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        object markedResult = await readOutput!(JsonSerializer.SerializeToElement(new { missionId = earlier.Id })).ConfigureAwait(false);
                        AssertTrue(markedResult is MissionOutputArtifactPage,
                            "The objective named by the voyage brief authorizes the read; actual result: " + JsonSerializer.Serialize(markedResult));
                        string deniedListed = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = listedMission.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedListed, "An objective that lists only the target does not authorize the read.");
                    }
                }
            });

            await RunTest("MissionEvidence_DifferentObjectiveSameOwnerMissionStaysHidden", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage callerVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("caller objective voyage")).ConfigureAwait(false);
                    Voyage otherVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("other objective voyage")).ConfigureAwait(false);
                    Mission other = await testDb.Driver.Missions.CreateAsync(new Mission("other objective worker")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageId = otherVoyage.Id,
                        AgentOutput = "other objective report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("caller objective judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageId = callerVoyage.Id,
                        Persona = "Judge"
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "caller objective",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageIds = new List<string> { callerVoyage.Id }
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "other objective",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageIds = new List<string> { otherVoyage.Id }
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? readOutput = null;
                    McpMissionTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_mission_output") readOutput = handler;
                    }, testDb.Driver, new RecordingAdmiralDouble(), null, null);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = judge.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string denied = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = other.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", denied, "A same-owner mission of a different objective stays hidden.");
                    }
                }
            });

            await RunTest("MissionEvidence_SameObjectiveOtherOwnerMissionStaysHidden", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster(Armada.Core.Constants.DefaultTenantId, "objective-other@example.com", "password")).ConfigureAwait(false);
                    Voyage earlierVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("earlier shared voyage")).ConfigureAwait(false);
                    Voyage laterVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("later shared voyage")).ConfigureAwait(false);
                    Mission foreignEarlier = await testDb.Driver.Missions.CreateAsync(new Mission("other owner earlier worker")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = otherUser.Id,
                        VoyageId = earlierVoyage.Id,
                        AgentOutput = "other owner report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission ownEarlier = await testDb.Driver.Missions.CreateAsync(new Mission("own earlier worker")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageId = earlierVoyage.Id,
                        AgentOutput = "own earlier report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("later shared judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageId = laterVoyage.Id,
                        Persona = "Judge"
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "objective owned by another user",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = otherUser.Id,
                        VoyageIds = new List<string> { earlierVoyage.Id, laterVoyage.Id }
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? readOutput = null;
                    McpMissionTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_mission_output") readOutput = handler;
                    }, testDb.Driver, new RecordingAdmiralDouble(), null, null);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = judge.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string deniedByForeignObjective = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = ownEarlier.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedByForeignObjective, "An objective owned by another user does not authorize the read.");
                    }

                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "objective owned by the caller",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageIds = new List<string> { earlierVoyage.Id, laterVoyage.Id }
                    }).ConfigureAwait(false);
                    using (McpCallerContext.Begin(caller))
                    {
                        object ownResult = await readOutput!(JsonSerializer.SerializeToElement(new { missionId = ownEarlier.Id })).ConfigureAwait(false);
                        AssertTrue(ownResult is MissionOutputArtifactPage,
                            "The caller's own objective authorizes its own earlier mission; actual result: " + JsonSerializer.Serialize(ownResult));
                        string deniedForeign = JsonSerializer.Serialize(await readOutput!(JsonSerializer.SerializeToElement(new { missionId = foreignEarlier.Id })).ConfigureAwait(false));
                        AssertContains("Mission not found", deniedForeign, "A shared objective never crosses mission ownership.");
                    }
                }
            });

            await RunTest("MissionEvidence_VoyagelessStageReadsMissionsOfItsObjectiveVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("objective worker voyage")).ConfigureAwait(false);
                    List<Mission> voyageMissions = new List<Mission>();
                    for (int i = 0; i < 3; i++)
                    {
                        // Voyage missions are stored without a user, unlike the standalone stage and the objective.
                        voyageMissions.Add(await testDb.Driver.Missions.CreateAsync(new Mission("voyage stage " + i)
                        {
                            TenantId = Armada.Core.Constants.DefaultTenantId,
                            VoyageId = voyage.Id,
                            AgentOutput = "voyage stage report " + i,
                            Status = MissionStatusEnum.Complete
                        }).ConfigureAwait(false));
                    }
                    Mission recorder = await testDb.Driver.Missions.CreateAsync(new Mission("voyage-less recorder")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Persona = "Recorder"
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "objective linking the recorder and the voyage",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageIds = new List<string> { voyage.Id },
                        MissionIds = new List<string> { recorder.Id }
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = recorder.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        foreach (Mission voyageMission in voyageMissions)
                        {
                            object result = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = voyageMission.Id })).ConfigureAwait(false);
                            AssertTrue(result is MissionOutputArtifactPage,
                                "A voyage-less stage reads each mission of its objective's voyage; actual result: " + JsonSerializer.Serialize(result));
                            AssertEqual(voyageMission.AgentOutput, ((MissionOutputArtifactPage)result).Content);
                        }
                    }
                }
            });

            await RunTest("MissionEvidence_StageReadsFailedMissionOfAnEarlierVoyageOfItsObjective", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage earlierVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("earlier review voyage")).ConfigureAwait(false);
                    Voyage laterVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("later review voyage")).ConfigureAwait(false);
                    Mission failedJudge = await testDb.Driver.Missions.CreateAsync(new Mission("failed earlier judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        VoyageId = earlierVoyage.Id,
                        Persona = "Judge",
                        AgentOutput = "failed judge report",
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);
                    Mission linter = await testDb.Driver.Missions.CreateAsync(new Mission("later linter")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        VoyageId = laterVoyage.Id,
                        Persona = "Linter"
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "objective with two review voyages",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageIds = new List<string> { earlierVoyage.Id, laterVoyage.Id }
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = linter.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        object result = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = failedJudge.Id })).ConfigureAwait(false);
                        AssertTrue(result is MissionOutputArtifactPage,
                            "A Linter reads the Failed Judge of an earlier voyage of its objective; actual result: " + JsonSerializer.Serialize(result));
                    }
                }
            });

            await RunTest("VoyageStatus_VoyagelessStageReadsAVoyageOfItsObjective", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("objective voyage under review")).ConfigureAwait(false);
                    for (int i = 0; i < 2; i++)
                    {
                        await testDb.Driver.Missions.CreateAsync(new Mission("reviewed stage " + i)
                        {
                            TenantId = Armada.Core.Constants.DefaultTenantId,
                            VoyageId = voyage.Id,
                            Status = MissionStatusEnum.Complete
                        }).ConfigureAwait(false);
                    }
                    Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("voyage-less judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Persona = "Judge"
                    }).ConfigureAwait(false);
                    Voyage unrelated = await testDb.Driver.Voyages.CreateAsync(new Voyage("unrelated voyage")).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "objective linking the judge and the voyage",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VoyageIds = new List<string> { voyage.Id },
                        MissionIds = new List<string> { judge.Id }
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = judge.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string status = JsonSerializer.Serialize(await tools.VoyageStatus!(JsonSerializer.SerializeToElement(new { voyageId = voyage.Id })).ConfigureAwait(false));
                        AssertFalse(status.Contains("\"Error\"", StringComparison.Ordinal), "A voyage-less Judge reads a voyage of its own objective; actual result: " + status);
                        AssertContains("\"TotalMissions\":2", status, "missions stored without a user still count toward their owner's voyage");
                        string denied = JsonSerializer.Serialize(await tools.VoyageStatus!(JsonSerializer.SerializeToElement(new { voyageId = unrelated.Id })).ConfigureAwait(false));
                        AssertContains("Voyage not found", denied, "an unrelated voyage stays hidden");
                    }
                }
            });

            await RunTest("VoyageStatus_VoyagelessStageReadsTheVoyageOfItsDirectDependency", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("dependency voyage")).ConfigureAwait(false);
                    Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("dependency worker")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        VoyageId = voyage.Id,
                        AgentOutput = "worker report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission sibling = await testDb.Driver.Missions.CreateAsync(new Mission("dependency voyage sibling")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        VoyageId = voyage.Id,
                        AgentOutput = "sibling report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("voyage-less dependent judge")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        DependsOnMissionId = worker.Id,
                        Persona = "Judge"
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = judge.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string status = JsonSerializer.Serialize(await tools.VoyageStatus!(JsonSerializer.SerializeToElement(new { voyageId = voyage.Id })).ConfigureAwait(false));
                        AssertFalse(status.Contains("\"Error\"", StringComparison.Ordinal), "A Judge reads the voyage of its direct dependency; actual result: " + status);
                        object siblingResult = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = sibling.Id })).ConfigureAwait(false);
                        AssertTrue(siblingResult is MissionOutputArtifactPage,
                            "A Judge reads a mission of its dependency's voyage; actual result: " + JsonSerializer.Serialize(siblingResult));
                    }
                }
            });

            await RunTest("MissionEvidence_ChallengerReadsOnlyTheJudgeItsDispatchRecordNames", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenant = Armada.Core.Constants.DefaultTenantId;
                    string user = Armada.Core.Constants.DefaultUserId;
                    Voyage reviewed = await testDb.Driver.Voyages.CreateAsync(new Voyage("reviewed voyage") { TenantId = tenant, UserId = user }).ConfigureAwait(false);
                    Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("reviewed worker")
                    {
                        TenantId = tenant,
                        UserId = user,
                        VoyageId = reviewed.Id,
                        AgentOutput = "worker report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("blocking judge")
                    {
                        TenantId = tenant,
                        UserId = user,
                        VoyageId = reviewed.Id,
                        DependsOnMissionId = worker.Id,
                        Persona = Armada.Core.PersonaCatalog.Judge,
                        AgentOutput = "judge report",
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);
                    string brief = JudgeChallenge.BuildBrief(judge, "judge report", new List<string> { "a finding" }, "main");

                    Mission challenger = await testDb.Driver.Missions.CreateAsync(new Mission("linked challenger")
                    {
                        TenantId = tenant,
                        UserId = user,
                        Persona = Armada.Core.PersonaCatalog.Challenger,
                        Description = brief
                    }).ConfigureAwait(false);
                    Mission unlinkedChallenger = await testDb.Driver.Missions.CreateAsync(new Mission("challenger the record does not name")
                    {
                        TenantId = tenant,
                        UserId = user,
                        Persona = Armada.Core.PersonaCatalog.Challenger,
                        Description = brief
                    }).ConfigureAwait(false);
                    Mission workerWithLine = await testDb.Driver.Missions.CreateAsync(new Mission("worker quoting the judge line")
                    {
                        TenantId = tenant,
                        UserId = user,
                        Persona = Armada.Core.PersonaCatalog.Worker,
                        Description = brief
                    }).ConfigureAwait(false);
                    await testDb.Driver.Events.CreateAsync(new ArmadaEvent(JudgeChallenge.DispatchedEvent, "challenger dispatched")
                    {
                        TenantId = tenant,
                        UserId = user,
                        EntityType = "mission",
                        EntityId = judge.Id,
                        MissionId = judge.Id,
                        VoyageId = reviewed.Id,
                        Payload = JsonSerializer.Serialize(new JudgeChallengeDispatchRecord { ChallengerMissionId = challenger.Id, FindingCount = 1 })
                    }).ConfigureAwait(false);
                    UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant, "challenge-other-" + Guid.NewGuid().ToString("N") + "@example.com", "password")).ConfigureAwait(false);
                    Mission otherOwnerJudge = await testDb.Driver.Missions.CreateAsync(new Mission("other owner judge")
                    {
                        TenantId = tenant,
                        UserId = otherUser.Id,
                        Persona = Armada.Core.PersonaCatalog.Judge,
                        AgentOutput = "other owner judge report",
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);
                    Mission otherOwnerChallenger = await testDb.Driver.Missions.CreateAsync(new Mission("challenger of another owner's judge")
                    {
                        TenantId = tenant,
                        UserId = user,
                        Persona = Armada.Core.PersonaCatalog.Challenger,
                        Description = JudgeChallenge.BuildBrief(otherOwnerJudge, "judge report", new List<string> { "a finding" }, "main")
                    }).ConfigureAwait(false);
                    await testDb.Driver.Events.CreateAsync(new ArmadaEvent(JudgeChallenge.DispatchedEvent, "challenger dispatched")
                    {
                        TenantId = tenant,
                        UserId = otherUser.Id,
                        EntityType = "mission",
                        EntityId = otherOwnerJudge.Id,
                        MissionId = otherOwnerJudge.Id,
                        Payload = JsonSerializer.Serialize(new JudgeChallengeDispatchRecord { ChallengerMissionId = otherOwnerChallenger.Id, FindingCount = 1 })
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    List<ScopedReadCase> missionCases = new List<ScopedReadCase>
                    {
                        new ScopedReadCase(challenger, judge, true),
                        new ScopedReadCase(challenger, worker, true),
                        new ScopedReadCase(unlinkedChallenger, judge, false),
                        new ScopedReadCase(unlinkedChallenger, worker, false),
                        new ScopedReadCase(workerWithLine, judge, false),
                        new ScopedReadCase(workerWithLine, worker, false),
                        new ScopedReadCase(otherOwnerChallenger, otherOwnerJudge, false)
                    };
                    foreach (ScopedReadCase item in missionCases)
                    {
                        AuthContext caller = McpTestCaller.Operator;
                        caller.MissionId = item.Caller.Id;
                        using (McpCallerContext.Begin(caller))
                        {
                            object result = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = item.Target.Id })).ConfigureAwait(false);
                            AssertEqual(item.Readable, result is MissionOutputArtifactPage,
                                item.Caller.Title + " reading " + item.Target.Title + "; actual result: " + JsonSerializer.Serialize(result));
                        }
                    }

                    AuthContext linked = McpTestCaller.Operator;
                    linked.MissionId = challenger.Id;
                    using (McpCallerContext.Begin(linked))
                    {
                        string status = JsonSerializer.Serialize(await tools.VoyageStatus!(JsonSerializer.SerializeToElement(new { voyageId = reviewed.Id })).ConfigureAwait(false));
                        AssertFalse(status.Contains("\"Error\"", StringComparison.Ordinal), "the linked Challenger reads the Judge's voyage; actual result: " + status);
                    }
                    AuthContext unlinked = McpTestCaller.Operator;
                    unlinked.MissionId = unlinkedChallenger.Id;
                    using (McpCallerContext.Begin(unlinked))
                    {
                        string status = JsonSerializer.Serialize(await tools.VoyageStatus!(JsonSerializer.SerializeToElement(new { voyageId = reviewed.Id })).ConfigureAwait(false));
                        AssertContains("Voyage not found", status, "a Challenger the dispatch record does not name stays outside the voyage");
                    }
                }
            });

            await RunTest("MissionEvidence_AbbreviatedIdsResolveToTheOneRecordTheCallerCanSee", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenant = Armada.Core.Constants.DefaultTenantId;
                    string user = Armada.Core.Constants.DefaultUserId;
                    UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant, "abbrev-other-" + Guid.NewGuid().ToString("N") + "@example.com", "password")).ConfigureAwait(false);

                    // Each mission gets its own creation-time part, except the twins, which share one on purpose.
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Voyage earlier = await testDb.Driver.Voyages.CreateAsync(new Voyage("earlier rescue voyage") { TenantId = tenant, UserId = user }).ConfigureAwait(false);
                    Mission cited = await testDb.Driver.Missions.CreateAsync(new Mission("earlier rescue worker")
                    {
                        Id = TestRecordIds.At("msn", now, "citedWorker"),
                        TenantId = tenant,
                        UserId = user,
                        VoyageId = earlier.Id,
                        AgentOutput = "rescue worker report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    // A record of another owner created in the same millisecond shares the abbreviation; it is
                    // never a candidate, so the caller's abbreviation still resolves to its own record.
                    Mission otherOwnerTwin = await testDb.Driver.Missions.CreateAsync(new Mission("other owner twin")
                    {
                        Id = TestRecordIds.At("msn", now, "otherOwnerTwin"),
                        TenantId = tenant,
                        UserId = otherUser.Id,
                        AgentOutput = "other owner report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission otherOwnerOnly = await testDb.Driver.Missions.CreateAsync(new Mission("other owner mission")
                    {
                        Id = TestRecordIds.At("msn", now + 1, "otherOwnerOnly"),
                        TenantId = tenant,
                        UserId = otherUser.Id,
                        AgentOutput = "hidden report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission twinA = await testDb.Driver.Missions.CreateAsync(new Mission("same-owner twin a")
                    {
                        Id = TestRecordIds.At("msn", now + 2, "firstTwin"),
                        TenantId = tenant,
                        UserId = user,
                        AgentOutput = "twin a",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(new Mission("same-owner twin b")
                    {
                        Id = TestRecordIds.At("msn", now + 2, "secondTwin"),
                        TenantId = tenant,
                        UserId = user,
                        AgentOutput = "twin b",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);

                    Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("follow-up worker")
                    {
                        Id = TestRecordIds.At("msn", now + 3, "followUpWorker"),
                        TenantId = tenant,
                        UserId = user,
                        Persona = Armada.Core.PersonaCatalog.Worker
                    }).ConfigureAwait(false);
                    Objective own = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "follow-up objective citing short ids",
                        TenantId = tenant,
                        UserId = user,
                        MissionIds = new List<string> { worker.Id },
                        Description = "Read the rescue worker report in mission " + cited.Id.Substring(0, 12)
                            + " of voyage " + earlier.Id.Substring(0, 12) + ", and the twin "
                            + twinA.Id.Substring(0, 12) + "."
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = worker.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        object byShort = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = cited.Id.Substring(0, 12) })).ConfigureAwait(false);
                        AssertTrue(byShort is MissionOutputArtifactPage, "an abbreviated id reads the one visible mission; actual result: " + JsonSerializer.Serialize(byShort));
                        AssertEqual(cited.AgentOutput, ((MissionOutputArtifactPage)byShort).Content, "the abbreviation resolves to the caller's record, not the other owner's twin");
                        object byFull = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = cited.Id })).ConfigureAwait(false);
                        AssertTrue(byFull is MissionOutputArtifactPage, "a short id cited by the objective links the mission like its full id; actual result: " + JsonSerializer.Serialize(byFull));

                        string voyageStatus = JsonSerializer.Serialize(await tools.VoyageStatus!(JsonSerializer.SerializeToElement(new { voyageId = earlier.Id.Substring(0, 12) })).ConfigureAwait(false));
                        AssertFalse(voyageStatus.Contains("\"Error\"", StringComparison.Ordinal), "an abbreviated cited voyage reads; actual result: " + voyageStatus);
                        AssertContains("\"TotalMissions\":1", voyageStatus, "the voyage read uses the resolved id");

                        object objective = await tools.ReadObjective!(JsonSerializer.SerializeToElement(new { objectiveId = own.Id.Substring(0, 12) })).ConfigureAwait(false);
                        AssertTrue(objective is Objective, "an abbreviated objective id reads the caller's objective; actual result: " + JsonSerializer.Serialize(objective));

                        string ambiguous = JsonSerializer.Serialize(await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = twinA.Id.Substring(0, 12) })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"ambiguous_id\"", ambiguous, "two visible missions make the abbreviation ambiguous");
                        AssertFalse(ambiguous.Contains("twin a", StringComparison.Ordinal), "an ambiguous read returns no record");
                        object twinByFull = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = twinA.Id })).ConfigureAwait(false);
                        AssertFalse(twinByFull is MissionOutputArtifactPage, "an ambiguous citation links neither twin");

                        string hidden = JsonSerializer.Serialize(await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = otherOwnerOnly.Id.Substring(0, 12) })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"not_found\"", hidden, "another owner's record reads as absent through its abbreviation");
                    }

                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        object operatorRead = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = otherOwnerOnly.Id.Substring(0, 12) })).ConfigureAwait(false);
                        AssertTrue(operatorRead is MissionOutputArtifactPage, "an operator resolves an abbreviation within its own scope; actual result: " + JsonSerializer.Serialize(operatorRead));
                    }
                }
            });

            await RunTest("MissionEvidence_ReviewStageReadsEarlierStagesOfItsOwnVoyageOnly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenant = Armada.Core.Constants.DefaultTenantId;
                    // Pipeline stages are stored without a user and with no objective, as a dispatched voyage stores them.
                    Voyage pipeline = await testDb.Driver.Voyages.CreateAsync(new Voyage("worker linter judge voyage") { TenantId = tenant }).ConfigureAwait(false);
                    Voyage otherPipeline = await testDb.Driver.Voyages.CreateAsync(new Voyage("other voyage of the vessel") { TenantId = tenant }).ConfigureAwait(false);
                    Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("pipeline worker")
                    {
                        TenantId = tenant,
                        VoyageId = pipeline.Id,
                        Persona = Armada.Core.PersonaCatalog.Worker,
                        AgentOutput = "worker report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission linter = await testDb.Driver.Missions.CreateAsync(new Mission("pipeline linter")
                    {
                        TenantId = tenant,
                        VoyageId = pipeline.Id,
                        DependsOnMissionId = worker.Id,
                        Persona = "Linter",
                        AgentOutput = "linter report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("pipeline judge")
                    {
                        TenantId = tenant,
                        VoyageId = pipeline.Id,
                        DependsOnMissionId = linter.Id,
                        Persona = Armada.Core.PersonaCatalog.Judge
                    }).ConfigureAwait(false);
                    Mission otherWorker = await testDb.Driver.Missions.CreateAsync(new Mission("other voyage worker")
                    {
                        TenantId = tenant,
                        VoyageId = otherPipeline.Id,
                        Persona = Armada.Core.PersonaCatalog.Worker,
                        AgentOutput = "other voyage report",
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = judge.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        foreach (Mission earlierStage in new[] { worker, linter })
                        {
                            object result = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = earlierStage.Id })).ConfigureAwait(false);
                            AssertTrue(result is MissionOutputArtifactPage,
                                "the Judge reads " + earlierStage.Title + " of its own voyage; actual result: " + JsonSerializer.Serialize(result));
                            AssertEqual(earlierStage.AgentOutput, ((MissionOutputArtifactPage)result).Content);
                        }
                        string denied = JsonSerializer.Serialize(await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = otherWorker.Id })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"out_of_scope\"", denied, "a mission of another voyage stays out of scope");
                        AssertFalse(denied.Contains("other voyage report", StringComparison.Ordinal), "the denial carries no output of the other voyage");
                    }
                }
            });

            await RunTest("MissionEvidence_AnAbbreviatedObjectiveCitationLinksTheCitedObjective", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenant = Armada.Core.Constants.DefaultTenantId;
                    string user = Armada.Core.Constants.DefaultUserId;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    // The cited objective sits in another objective tree, so only the citation links it.
                    Objective citedTree = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Id = TestRecordIds.At("obj", now - 5000, "citedTree"),
                        Title = "other tree root",
                        TenantId = tenant,
                        UserId = user
                    }).ConfigureAwait(false);
                    Objective cited = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Id = TestRecordIds.At("obj", now - 4000, "citedInput"),
                        Title = "missing inputs objective",
                        TenantId = tenant,
                        UserId = user,
                        ParentObjectiveId = citedTree.Id
                    }).ConfigureAwait(false);
                    Objective uncited = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Id = TestRecordIds.At("obj", now - 3000, "uncited"),
                        Title = "uncited objective",
                        TenantId = tenant,
                        UserId = user,
                        ParentObjectiveId = citedTree.Id
                    }).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("citing voyage") { TenantId = tenant }).ConfigureAwait(false);
                    Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("citing worker")
                    {
                        TenantId = tenant,
                        VoyageId = voyage.Id,
                        Persona = Armada.Core.PersonaCatalog.Worker
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Id = TestRecordIds.At("obj", now - 2000, "authorizing"),
                        Title = "authorizing objective",
                        TenantId = tenant,
                        UserId = user,
                        VoyageIds = new List<string> { voyage.Id },
                        Description = "The missing inputs are tracked by " + TestRecordIds.Abbreviate(cited.Id) + "; consult it for the exact list."
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = worker.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        foreach (string id in new[] { cited.Id, TestRecordIds.Abbreviate(cited.Id) })
                        {
                            object result = await tools.ReadObjective!(JsonSerializer.SerializeToElement(new { objectiveId = id })).ConfigureAwait(false);
                            AssertTrue(result is Objective, "the objective cited by its abbreviation reads by " + id + "; actual result: " + JsonSerializer.Serialize(result));
                        }
                        string denied = JsonSerializer.Serialize(await tools.ReadObjective!(JsonSerializer.SerializeToElement(new { objectiveId = uncited.Id })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"out_of_scope\"", denied, "an uncited objective of the same tree stays out of scope");
                    }
                }
            });

            await RunTest("MissionEvidence_CallerReadsAncestorLinkedChildAndCitedRecordsOnly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LinkedEvidenceFixture fixture = await LinkedEvidenceFixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = fixture.Anchor.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        foreach (Objective readable in fixture.Readable)
                        {
                            object result = await tools.ReadObjective!(JsonSerializer.SerializeToElement(new { objectiveId = readable.Id })).ConfigureAwait(false);
                            AssertTrue(result is Objective, "linked objective '" + readable.Title + "' is readable; actual result: " + JsonSerializer.Serialize(result));
                        }
                        foreach (Objective hidden in fixture.Hidden)
                        {
                            string denied = JsonSerializer.Serialize(await tools.ReadObjective!(JsonSerializer.SerializeToElement(new { objectiveId = hidden.Id })).ConfigureAwait(false));
                            AssertContains("Objective not found", denied, "objective '" + hidden.Title + "' stays hidden");
                        }
                        foreach (Mission readableMission in new[] { fixture.CitedHistoricalMission, fixture.AncestorMission })
                        {
                            object result = await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = readableMission.Id })).ConfigureAwait(false);
                            AssertTrue(result is MissionOutputArtifactPage,
                                "mission '" + readableMission.Title + "' is readable; actual result: " + JsonSerializer.Serialize(result));
                        }
                        foreach (Mission hiddenMission in new[] { fixture.UncitedHistoricalMission, fixture.OtherOwnerCitedMission })
                        {
                            string denied = JsonSerializer.Serialize(await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = hiddenMission.Id })).ConfigureAwait(false));
                            AssertContains("Mission not found", denied, "mission '" + hiddenMission.Title + "' stays hidden");
                        }
                    }
                }
            });

            await RunTest("ObjectiveEnumerate_MissionCallerListsExactlyTheObjectivesItCanRead", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LinkedEvidenceFixture fixture = await LinkedEvidenceFixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = fixture.Anchor.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        EnumerationResult<Objective> listed = (EnumerationResult<Objective>)await tools.Enumerate!(
                            JsonSerializer.SerializeToElement(new { entityType = "objectives", pageSize = 100 })).ConfigureAwait(false);
                        List<string> listedIds = listed.Objects.Select(objective => objective.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
                        List<string> expected = fixture.Readable.Select(objective => objective.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
                        AssertEqual(String.Join(",", expected), String.Join(",", listedIds), "enumerate lists exactly the objectives get_objective returns");
                        AssertEqual((long)expected.Count, (long)listed.TotalRecords, "the total counts only readable objectives");
                    }
                }
            });

            await RunTest("MissionAndVoyageEnumerate_MissionCallerListsExactlyWhatStatusReadsReturn", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata otherTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("listing other tenant")).ConfigureAwait(false);
                    UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster(Armada.Core.Constants.DefaultTenantId, "listing-other@example.com", "password")).ConfigureAwait(false);
                    Voyage ownVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("own voyage") { TenantId = Armada.Core.Constants.DefaultTenantId }).ConfigureAwait(false);
                    Voyage citedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("cited voyage") { TenantId = Armada.Core.Constants.DefaultTenantId }).ConfigureAwait(false);
                    Voyage unrelatedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("unrelated voyage") { TenantId = Armada.Core.Constants.DefaultTenantId }).ConfigureAwait(false);
                    Voyage foreignVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("foreign voyage") { TenantId = otherTenant.Id }).ConfigureAwait(false);
                    Voyage otherOwnerVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("other owner voyage")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = otherUser.Id
                    }).ConfigureAwait(false);
                    Mission dependency = await testDb.Driver.Missions.CreateAsync(new Mission("dependency") { TenantId = Armada.Core.Constants.DefaultTenantId }).ConfigureAwait(false);
                    Mission own = await testDb.Driver.Missions.CreateAsync(new Mission("reader")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        VoyageId = ownVoyage.Id,
                        DependsOnMissionId = dependency.Id
                    }).ConfigureAwait(false);
                    Mission sibling = await testDb.Driver.Missions.CreateAsync(new Mission("sibling") { TenantId = Armada.Core.Constants.DefaultTenantId, VoyageId = ownVoyage.Id }).ConfigureAwait(false);
                    Mission legacy = await testDb.Driver.Missions.CreateAsync(new Mission("legacy owner") { VoyageId = ownVoyage.Id }).ConfigureAwait(false);
                    Mission cited = await testDb.Driver.Missions.CreateAsync(new Mission("cited voyage mission") { TenantId = Armada.Core.Constants.DefaultTenantId, VoyageId = citedVoyage.Id }).ConfigureAwait(false);
                    Mission unrelated = await testDb.Driver.Missions.CreateAsync(new Mission("unrelated") { TenantId = Armada.Core.Constants.DefaultTenantId, VoyageId = unrelatedVoyage.Id }).ConfigureAwait(false);
                    Mission foreign = await testDb.Driver.Missions.CreateAsync(new Mission("foreign") { TenantId = otherTenant.Id, VoyageId = ownVoyage.Id }).ConfigureAwait(false);
                    Mission foreignVoyageMission = await testDb.Driver.Missions.CreateAsync(new Mission("foreign voyage mission") { TenantId = otherTenant.Id, VoyageId = foreignVoyage.Id }).ConfigureAwait(false);
                    Mission otherOwner = await testDb.Driver.Missions.CreateAsync(new Mission("other owner")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = otherUser.Id,
                        VoyageId = ownVoyage.Id
                    }).ConfigureAwait(false);
                    Mission otherOwnerVoyageMission = await testDb.Driver.Missions.CreateAsync(new Mission("other owner voyage mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = otherUser.Id,
                        VoyageId = otherOwnerVoyage.Id
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "authorized work",
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        MissionIds = new List<string> { own.Id },
                        EvidenceLinks = new List<string> { "earlier attempt " + citedVoyage.Id }
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = own.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        List<string> readableMissions = new List<string>();
                        foreach (Mission mission in await testDb.Driver.Missions.EnumerateAsync().ConfigureAwait(false))
                        {
                            object status = await tools.ReadStatus!(JsonSerializer.SerializeToElement(new { missionId = mission.Id })).ConfigureAwait(false);
                            if (!(status is McpScopeDenial)) readableMissions.Add(mission.Id);
                        }
                        readableMissions.Sort(StringComparer.Ordinal);
                        foreach (Mission expectedReadable in new[] { own, dependency, sibling, legacy, cited })
                            AssertTrue(readableMissions.Contains(expectedReadable.Id), "the status read returns '" + expectedReadable.Title + "'");
                        foreach (Mission expectedHidden in new[] { unrelated, foreign, foreignVoyageMission, otherOwner, otherOwnerVoyageMission })
                            AssertFalse(readableMissions.Contains(expectedHidden.Id), "the status read refuses '" + expectedHidden.Title + "'");

                        string missionListing = JsonSerializer.Serialize(await tools.Enumerate!(
                            JsonSerializer.SerializeToElement(new { entityType = "missions", pageSize = 1000 })).ConfigureAwait(false));
                        List<string> listedMissions = new List<string>();
                        foreach (Mission mission in await testDb.Driver.Missions.EnumerateAsync().ConfigureAwait(false))
                        {
                            if (missionListing.Contains(mission.Id, StringComparison.Ordinal)) listedMissions.Add(mission.Id);
                        }
                        listedMissions.Sort(StringComparer.Ordinal);
                        AssertEqual(String.Join(",", readableMissions), String.Join(",", listedMissions),
                            "the mission listing holds exactly the missions armada_mission_status returns");
                        AssertContains("\"TotalRecords\":" + readableMissions.Count, missionListing, "the mission total counts only readable missions");

                        string secondPage = JsonSerializer.Serialize(await tools.Enumerate!(
                            JsonSerializer.SerializeToElement(new { entityType = "missions", pageSize = 2, pageNumber = 2 })).ConfigureAwait(false));
                        AssertContains("\"TotalRecords\":" + readableMissions.Count, secondPage, "a later page reports the same readable total");

                        List<string> readableVoyages = new List<string>();
                        List<string> listedVoyages = new List<string>();
                        string voyageListing = JsonSerializer.Serialize(await tools.Enumerate!(
                            JsonSerializer.SerializeToElement(new { entityType = "voyages", pageSize = 1000 })).ConfigureAwait(false));
                        foreach (Voyage voyage in await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false))
                        {
                            object status = await tools.VoyageStatus!(JsonSerializer.SerializeToElement(new { voyageId = voyage.Id })).ConfigureAwait(false);
                            if (!(status is McpScopeDenial)) readableVoyages.Add(voyage.Id);
                            if (voyageListing.Contains(voyage.Id, StringComparison.Ordinal)) listedVoyages.Add(voyage.Id);
                        }
                        readableVoyages.Sort(StringComparer.Ordinal);
                        listedVoyages.Sort(StringComparer.Ordinal);
                        AssertTrue(readableVoyages.Contains(ownVoyage.Id) && readableVoyages.Contains(citedVoyage.Id), "the voyage read returns the own and the cited voyage");
                        foreach (Voyage hiddenVoyage in new[] { unrelatedVoyage, foreignVoyage, otherOwnerVoyage })
                            AssertFalse(readableVoyages.Contains(hiddenVoyage.Id), "the voyage read refuses '" + hiddenVoyage.Title + "'");
                        AssertEqual(String.Join(",", readableVoyages), String.Join(",", listedVoyages),
                            "the voyage listing holds exactly the voyages armada_voyage_status returns");
                    }
                }
            });

            await RunTest("MissionEvidence_DenialsNameTheReasonWithoutRevealingOtherOwners", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LinkedEvidenceFixture fixture = await LinkedEvidenceFixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    Voyage unrelatedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("unrelated same-owner voyage")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    }).ConfigureAwait(false);
                    EvidenceHandlers tools = EvidenceHandlers.Register(testDb.Driver);

                    AuthContext caller = McpTestCaller.Operator;
                    caller.MissionId = fixture.Anchor.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        string sameOwnerMission = JsonSerializer.Serialize(await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = fixture.UncitedHistoricalMission.Id })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"out_of_scope\"", sameOwnerMission, "a same-owner mission outside the evidence scope says so");
                        string sameOwnerStatus = JsonSerializer.Serialize(await tools.ReadStatus!(JsonSerializer.SerializeToElement(new { missionId = fixture.UncitedHistoricalMission.Id })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"out_of_scope\"", sameOwnerStatus, "the status read names the same reason");
                        string otherOwnerMission = JsonSerializer.Serialize(await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = fixture.OtherOwnerCitedMission.Id })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"not_found\"", otherOwnerMission, "another owner's mission reads as absent");
                        string absentMission = JsonSerializer.Serialize(await tools.ReadOutput!(JsonSerializer.SerializeToElement(new { missionId = "msn_absent" })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"not_found\"", absentMission, "an absent mission reads as absent");

                        string sameOwnerObjective = JsonSerializer.Serialize(await tools.ReadObjective!(JsonSerializer.SerializeToElement(new { objectiveId = fixture.Unrelated.Id })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"out_of_scope\"", sameOwnerObjective, "a same-owner objective outside the evidence scope says so");
                        string otherOwnerObjective = JsonSerializer.Serialize(await tools.ReadObjective!(JsonSerializer.SerializeToElement(new { objectiveId = fixture.OtherOwnerCited.Id })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"not_found\"", otherOwnerObjective, "another owner's objective reads as absent");

                        string sameOwnerVoyage = JsonSerializer.Serialize(await tools.VoyageStatus!(JsonSerializer.SerializeToElement(new { voyageId = unrelatedVoyage.Id })).ConfigureAwait(false));
                        AssertContains("\"Reason\":\"out_of_scope\"", sameOwnerVoyage, "a same-owner voyage outside the evidence scope says so");
                        AssertContains("Voyage not found", sameOwnerVoyage, "the error text stays the same");
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

            await RunTest("CreateMission_OptionalTier_SetsTheFloorAndRejectsUnknownText", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("mission-tier-tenant")).ConfigureAwait(false);
                    UserMaster user = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "mission-tier@example.com", "password")).ConfigureAwait(false);
                    AuthContext caller = AuthContext.Authenticated(tenant.Id, user.Id, false, false, "Test");
                    Vessel vessel = new Vessel("mission-tier-vessel", "https://github.com/test/mission-tier.git");
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
                        await createHandler!(JsonSerializer.SerializeToElement(new { title = "omit", description = "no tier", vesselId = vessel.Id })).ConfigureAwait(false);
                    }
                    AssertNull(admiralDouble.LastDispatched!.Tier, "omitting tier leaves the mission floor unset");

                    using (McpCallerContext.Begin(caller))
                    {
                        await createHandler!(JsonSerializer.SerializeToElement(new { title = "premium", description = "named tier", vesselId = vessel.Id, tier = "premium" })).ConfigureAwait(false);
                    }
                    AssertEqual(CaptainTierEnum.Premium, admiralDouble.LastDispatched!.Tier, "a tier name is matched without regard to case");

                    admiralDouble = new RecordingAdmiralDouble();
                    createHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_create_mission") createHandler = handler; },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);
                    object refused;
                    using (McpCallerContext.Begin(caller))
                    {
                        refused = await createHandler!(JsonSerializer.SerializeToElement(new { title = "bad", description = "bad tier", vesselId = vessel.Id, tier = "gold" })).ConfigureAwait(false);
                    }
                    string refusedJson = JsonSerializer.Serialize(refused);
                    AssertContains("Unknown captain tier", refusedJson, "an unknown tier is refused");
                    AssertContains("gold", refusedJson, "the refusal repeats the rejected value");
                    AssertNull(admiralDouble.LastDispatched, "an unknown tier does not dispatch");
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

            await RunTest("UpdateMission_RunningBriefEditWithoutMidMissionDelivery_ReturnsStoredUntilNextLaunch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Captain captain = await testDb.Driver.Captains.CreateAsync(
                        new Captain("brief-opencode", AgentRuntimeEnum.OpenCode) { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                    Mission running = await testDb.Driver.Missions.CreateAsync(new Mission("running brief", "original brief")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        CaptainId = captain.Id,
                        Status = MissionStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Mission pending = await testDb.Driver.Missions.CreateAsync(new Mission("pending brief", "pending original")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        CaptainId = captain.Id,
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

                    string runningJson;
                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        runningJson = JsonSerializer.Serialize(await updateHandler!(JsonSerializer.SerializeToElement(new
                        {
                            missionId = running.Id,
                            description = "revised brief for the running stage"
                        })).ConfigureAwait(false));
                    }
                    Mission? noticed = JsonSerializer.Deserialize<Mission>(runningJson);
                    AssertNotNull(noticed);
                    AssertEqual(SignalDelivery.StoredUntilNextLaunch, noticed!.Delivery, runningJson);
                    AssertContains("OpenCode", noticed.DeliveryReason!, runningJson);
                    AssertContains("next launch", noticed.DeliveryReason!, runningJson);

                    Mission? stored = await testDb.Driver.Missions.ReadAsync(running.Id).ConfigureAwait(false);
                    AssertEqual("revised brief for the running stage", stored!.Description);
                    AssertNull(stored.Delivery, "Delivery is an update-response notice and is not stored");
                    AssertNull(stored.DeliveryReason);

                    string pendingJson;
                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        pendingJson = JsonSerializer.Serialize(await updateHandler!(JsonSerializer.SerializeToElement(new
                        {
                            missionId = pending.Id,
                            description = "revised brief for a pending stage"
                        })).ConfigureAwait(false));
                    }
                    Mission? pendingUpdated = JsonSerializer.Deserialize<Mission>(pendingJson);
                    AssertNotNull(pendingUpdated);
                    AssertNull(pendingUpdated!.Delivery, "a pending mission is not running, so a brief edit has no mid-mission notice");
                    AssertNull(pendingUpdated.DeliveryReason);

                    string titleJson;
                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        titleJson = JsonSerializer.Serialize(await updateHandler!(JsonSerializer.SerializeToElement(new
                        {
                            missionId = running.Id,
                            title = "renamed running brief"
                        })).ConfigureAwait(false));
                    }
                    Mission? titleOnly = JsonSerializer.Deserialize<Mission>(titleJson);
                    AssertNotNull(titleOnly);
                    AssertNull(titleOnly!.Delivery, "a title-only update is not a brief edit");
                    AssertNull(titleOnly.DeliveryReason);
                }
            });
        }

        private sealed class EvidenceHandlers
        {
            public Func<JsonElement?, Task<object>>? ReadOutput { get; set; }
            public Func<JsonElement?, Task<object>>? ReadStatus { get; set; }
            public Func<JsonElement?, Task<object>>? ReadObjective { get; set; }
            public Func<JsonElement?, Task<object>>? VoyageStatus { get; set; }
            public Func<JsonElement?, Task<object>>? Enumerate { get; set; }

            public static EvidenceHandlers Register(DatabaseDriver database)
            {
                EvidenceHandlers handlers = new EvidenceHandlers();
                // Handlers go through the MCP argument seam, as the registrar wires every tool in production.
                McpMissionTools.Register((name, _, _, handler) =>
                {
                    if (name == "armada_mission_output") handlers.ReadOutput = McpAbbreviatedIdArguments.Wrap(name, handler, database);
                    if (name == "armada_mission_status") handlers.ReadStatus = McpAbbreviatedIdArguments.Wrap(name, handler, database);
                }, database, new RecordingAdmiralDouble(), null, null);
                McpObjectiveTools.Register((name, _, _, handler) =>
                {
                    if (name == "get_objective") handlers.ReadObjective = McpAbbreviatedIdArguments.Wrap(name, handler, database);
                }, database, new ObjectiveService(database));
                McpVoyageTools.Register((name, _, _, handler) =>
                {
                    if (name == "armada_voyage_status") handlers.VoyageStatus = McpAbbreviatedIdArguments.Wrap(name, handler, database);
                }, database, new RecordingAdmiralDouble(), null);
                McpEnumerateTools.Register((name, _, _, handler) =>
                {
                    if (name == "armada_enumerate") handlers.Enumerate = McpAbbreviatedIdArguments.Wrap(name, handler, database);
                }, database);
                return handlers;
            }
        }

        /// <summary>One mission read by a mission caller and whether its scope allows it.</summary>
        private sealed class ScopedReadCase
        {
            public ScopedReadCase(Mission caller, Mission target, bool readable)
            {
                Caller = caller;
                Target = target;
                Readable = readable;
            }

            public Mission Caller { get; }
            public Mission Target { get; }
            public bool Readable { get; }
        }

        /// <summary>
        /// An objective tree around one voyage-less stage: its own objective, the parent and grandparent, a
        /// blocker, a child, objectives and missions cited by id in the objective text, and records that
        /// must stay hidden (a sibling, an unrelated objective, a second-hand citation, other-owner records).
        /// </summary>
        private sealed class LinkedEvidenceFixture
        {
            public Mission Anchor { get; set; } = new Mission();
            public List<Objective> Readable { get; set; } = new List<Objective>();
            public List<Objective> Hidden { get; set; } = new List<Objective>();
            public Objective Unrelated { get; set; } = new Objective();
            public Objective OtherOwnerCited { get; set; } = new Objective();
            public Mission CitedHistoricalMission { get; set; } = new Mission();
            public Mission UncitedHistoricalMission { get; set; } = new Mission();
            public Mission AncestorMission { get; set; } = new Mission();
            public Mission OtherOwnerCitedMission { get; set; } = new Mission();

            public static async Task<LinkedEvidenceFixture> CreateAsync(DatabaseDriver database)
            {
                LinkedEvidenceFixture fixture = new LinkedEvidenceFixture();
                string tenant = Armada.Core.Constants.DefaultTenantId;
                string user = Armada.Core.Constants.DefaultUserId;
                UserMaster otherUser = await database.Users.CreateAsync(new UserMaster(tenant, "linked-other-" + Guid.NewGuid().ToString("N") + "@example.com", "password")).ConfigureAwait(false);

                fixture.Anchor = await database.Missions.CreateAsync(new Mission("voyage-less study stage") { TenantId = tenant, UserId = user, Persona = "Judge" }).ConfigureAwait(false);
                fixture.AncestorMission = await database.Missions.CreateAsync(new Mission("grandparent study mission") { TenantId = tenant, UserId = user, AgentOutput = "grandparent report", Status = MissionStatusEnum.Complete }).ConfigureAwait(false);
                fixture.CitedHistoricalMission = await database.Missions.CreateAsync(new Mission("cited historical judge") { TenantId = tenant, UserId = user, AgentOutput = "historical judge report", Status = MissionStatusEnum.Complete }).ConfigureAwait(false);
                fixture.UncitedHistoricalMission = await database.Missions.CreateAsync(new Mission("uncited historical judge") { TenantId = tenant, UserId = user, AgentOutput = "uncited report", Status = MissionStatusEnum.Complete }).ConfigureAwait(false);
                fixture.OtherOwnerCitedMission = await database.Missions.CreateAsync(new Mission("other owner cited mission") { TenantId = tenant, UserId = otherUser.Id, AgentOutput = "other owner report", Status = MissionStatusEnum.Complete }).ConfigureAwait(false);

                Objective grandparent = await database.Objectives.CreateAsync(new Objective { Title = "grandparent study", TenantId = tenant, UserId = user, MissionIds = new List<string> { fixture.AncestorMission.Id } }).ConfigureAwait(false);
                Objective parent = await database.Objectives.CreateAsync(new Objective { Title = "parent study", TenantId = tenant, UserId = user, ParentObjectiveId = grandparent.Id }).ConfigureAwait(false);
                Objective blocker = await database.Objectives.CreateAsync(new Objective { Title = "blocking objective", TenantId = tenant, UserId = user }).ConfigureAwait(false);
                Objective secondHand = await database.Objectives.CreateAsync(new Objective { Title = "second-hand citation", TenantId = tenant, UserId = user }).ConfigureAwait(false);
                Objective cited = await database.Objectives.CreateAsync(new Objective { Title = "cited closed objective", TenantId = tenant, UserId = user, Description = "See " + secondHand.Id + " for history." }).ConfigureAwait(false);
                Objective historical = await database.Objectives.CreateAsync(new Objective { Title = "historical objective", TenantId = tenant, UserId = user, MissionIds = new List<string> { fixture.CitedHistoricalMission.Id, fixture.UncitedHistoricalMission.Id } }).ConfigureAwait(false);
                fixture.OtherOwnerCited = await database.Objectives.CreateAsync(new Objective { Title = "other owner cited objective", TenantId = tenant, UserId = otherUser.Id }).ConfigureAwait(false);
                Objective own = await database.Objectives.CreateAsync(new Objective
                {
                    Title = "own study",
                    TenantId = tenant,
                    UserId = user,
                    ParentObjectiveId = parent.Id,
                    MissionIds = new List<string> { fixture.Anchor.Id },
                    BlockedByObjectiveIds = new List<string> { blocker.Id },
                    Description = "Reproduce the criteria of " + cited.Id + " and the verdict in " + fixture.CitedHistoricalMission.Id
                        + ". Compare with " + fixture.OtherOwnerCited.Id + " and " + fixture.OtherOwnerCitedMission.Id + "."
                }).ConfigureAwait(false);
                Objective child = await database.Objectives.CreateAsync(new Objective { Title = "child follow-up", TenantId = tenant, UserId = user, ParentObjectiveId = own.Id }).ConfigureAwait(false);
                Objective sibling = await database.Objectives.CreateAsync(new Objective { Title = "sibling study", TenantId = tenant, UserId = user, ParentObjectiveId = parent.Id }).ConfigureAwait(false);
                fixture.Unrelated = await database.Objectives.CreateAsync(new Objective { Title = "unrelated objective", TenantId = tenant, UserId = user }).ConfigureAwait(false);

                fixture.Readable = new List<Objective> { own, parent, grandparent, blocker, cited, child };
                fixture.Hidden = new List<Objective> { sibling, fixture.Unrelated, secondHand, historical, fixture.OtherOwnerCited };
                return fixture;
            }
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
