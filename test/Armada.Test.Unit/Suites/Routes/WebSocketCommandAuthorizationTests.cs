namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A WebSocket command enforces its own declared authorization rule, whoever calls the handler. Owned
    /// configuration reads find the record through the shared caller scope, so another tenant's record reads as
    /// not found and returns no data. Persona and pipeline changes admit the owning tenant's administrator, as REST
    /// and MCP do. Operator commands refuse every caller but a global administrator and write nothing. Updates made by an administrator keep the server-owned fields REST keeps.
    /// </summary>
    public class WebSocketCommandAuthorizationTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "WebSocket Command Authorization";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _ServerJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        #endregion

        #region Protected-Methods

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            // Owned configuration reads: persona, pipeline and prompt template share one family.
            foreach (OwnedFamily family in OwnedFamilies())
            {
                OwnedFamily current = family;
                await RunTest(current.Label + "_Get_CrossTenant_IsNotFoundAndReturnsNoData", async () =>
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                        OwnedRecordHandle record = await current.SeedAsync(testDb.Driver, callers.TenantA, callers.AdminA.UserId!, false).ConfigureAwait(false);

                        string json = await SendAsync(CreateHandler(testDb), current.GetAction, record.Name, new { }, callers.AdminB).ConfigureAwait(false);

                        AssertContains("command.error", json, "another tenant's record is refused");
                        AssertContains("\"code\":\"not_found\"", json, "the refusal reads as not found, so existence is not confirmed");
                        AssertFalse(json.Contains(record.Id, StringComparison.Ordinal), "the refusal returns none of the record");
                    }
                }).ConfigureAwait(false);

                await RunTest(current.Label + "_Get_SameTenantReader_SharedRecord_AndAdministrator_AreAllowed", async () =>
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                        OwnedRecordHandle record = await current.SeedAsync(testDb.Driver, callers.TenantA, callers.AdminA.UserId!, false).ConfigureAwait(false);
                        OwnedRecordHandle shared = await current.SeedAsync(testDb.Driver, Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, true).ConfigureAwait(false);
                        WebSocketCommandHandler handler = CreateHandler(testDb);

                        string reader = await SendAsync(handler, current.GetAction, record.Name, new { }, callers.UserA).ConfigureAwait(false);
                        AssertContains("command.result", reader, "a same-tenant user reads a tenant-wide record");
                        AssertContains(record.Id, reader, "the reader receives the record");

                        string sharedRead = await SendAsync(handler, current.GetAction, shared.Name, new { }, callers.AdminB).ConfigureAwait(false);
                        AssertContains(shared.Id, sharedRead, "a built-in record is readable by every tenant");

                        string admin = await SendAsync(handler, current.GetAction, record.Name, new { }, McpTestCaller.Operator).ConfigureAwait(false);
                        AssertContains(record.Id, admin, "a global administrator reads every tenant");
                    }
                }).ConfigureAwait(false);

                foreach (string writeAction in current.WriteActions)
                {
                    string action = writeAction;
                    if (current.TenantAdministratorsMayWrite)
                    {
                        await RunTest(current.Label + "_" + action + "_OtherTenantsAndTenantUsers_AreRefusedAndWriteNothing", async () =>
                        {
                            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                            {
                                Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                                OwnedRecordHandle record = await current.SeedAsync(testDb.Driver, callers.TenantA, callers.AdminA.UserId!, false).ConfigureAwait(false);
                                WebSocketCommandHandler handler = CreateHandler(testDb);

                                // Another tenant's administrator cannot find the record, so the refusal never confirms it exists.
                                string foreign = await SendAsync(handler, action, record.Name, new { Description = "changed-by-other-tenant" }, callers.AdminB).ConfigureAwait(false);
                                AssertContains("\"code\":\"not_found\"", foreign, action + " by another tenant's administrator reads as not found: " + foreign);
                                AssertFalse(foreign.Contains(record.Id, StringComparison.Ordinal), "the refusal returns none of the record");
                                AssertTrue(await current.IsUnchangedAsync(testDb.Driver, record).ConfigureAwait(false), action + " by another tenant's administrator writes nothing");

                                // A same-tenant user is not an administrator, as REST and MCP require.
                                string user = await SendAsync(handler, action, record.Name, new { Description = "changed-by-user" }, callers.UserA).ConfigureAwait(false);
                                AssertContains("\"code\":\"tenant_administrator_required\"", user, action + " names the missing role: " + user);
                                AssertTrue(await current.IsUnchangedAsync(testDb.Driver, record).ConfigureAwait(false), action + " by a tenant user writes nothing");
                            }
                        }).ConfigureAwait(false);

                        await RunTest(current.Label + "_" + action + "_OwningTenantAdministrator_AndGlobalAdministrator_AreAllowed", async () =>
                        {
                            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                            {
                                Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                                WebSocketCommandHandler handler = CreateHandler(testDb);
                                foreach (AuthContext caller in new[] { callers.AdminA, McpTestCaller.Operator })
                                {
                                    OwnedRecordHandle record = await current.SeedAsync(testDb.Driver, callers.TenantA, callers.AdminA.UserId!, false).ConfigureAwait(false);
                                    string json = await SendAsync(handler, action, record.Name, new { Description = "changed-by-" + caller.UserId }, caller).ConfigureAwait(false);
                                    AssertContains("command.result", json, action + " runs for " + caller.PrincipalDisplay + ": " + json);
                                    AssertFalse(await current.IsUnchangedAsync(testDb.Driver, record).ConfigureAwait(false), action + " by " + caller.PrincipalDisplay + " is written");
                                }
                            }
                        }).ConfigureAwait(false);
                        continue;
                    }

                    await RunTest(current.Label + "_" + action + "_NonGlobalAdministrators_AreRefusedAndWriteNothing", async () =>
                    {
                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                        {
                            Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                            OwnedRecordHandle record = await current.SeedAsync(testDb.Driver, callers.TenantA, callers.AdminA.UserId!, false).ConfigureAwait(false);
                            WebSocketCommandHandler handler = CreateHandler(testDb);

                            // Another tenant's administrator, a same-tenant non-editor and the owning tenant's
                            // administrator: MCP refuses this change to all three, so the command does too.
                            foreach (AuthContext caller in new[] { callers.AdminB, callers.UserA, callers.AdminA })
                            {
                                string json = await SendAsync(handler, action, record.Name, new { Description = "changed-by-" + caller.UserId, Content = "changed" }, caller).ConfigureAwait(false);
                                AssertContains("command.error", json, action + " is refused for " + caller.PrincipalDisplay);
                                AssertContains("\"code\":\"global_administrator_required\"", json, "the refusal names the missing role");
                                AssertTrue(await current.IsUnchangedAsync(testDb.Driver, record).ConfigureAwait(false), action + " by " + caller.PrincipalDisplay + " writes nothing");
                            }
                        }
                    }).ConfigureAwait(false);

                    await RunTest(current.Label + "_" + action + "_GlobalAdministrator_IsAllowed", async () =>
                    {
                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                        {
                            Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                            OwnedRecordHandle record = await current.SeedAsync(testDb.Driver, callers.TenantA, callers.AdminA.UserId!, false).ConfigureAwait(false);

                            string json = await SendAsync(CreateHandler(testDb), action, record.Name, new { Description = "changed-by-operator", Content = "changed" }, McpTestCaller.Operator).ConfigureAwait(false);

                            AssertContains("command.result", json, action + " runs for a global administrator");
                            AssertFalse(await current.IsUnchangedAsync(testDb.Driver, record).ConfigureAwait(false), action + " by a global administrator is written");
                        }
                    }).ConfigureAwait(false);
                }
            }

            await RunTest("CreatePersonaAndPipeline_TenantAdministratorCreatesInOwnTenant_TenantUserIsRefused", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                    WebSocketCommandHandler handler = CreateHandler(testDb);

                    string personaName = "WsTenantPersona" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    string refusedPersona = await SendAsync(handler, "create_persona", null, new { Name = personaName, PromptTemplateName = "persona.worker" }, callers.UserA).ConfigureAwait(false);
                    AssertContains("\"code\":\"tenant_administrator_required\"", refusedPersona, "a tenant user cannot create a persona: " + refusedPersona);
                    AssertNull(await testDb.Driver.Personas.ReadByNameAsync(callers.TenantA, personaName).ConfigureAwait(false), "the refused create writes nothing");

                    string createdPersona = await SendAsync(handler, "create_persona", null, new { Name = personaName, PromptTemplateName = "persona.worker", TenantId = callers.TenantB }, callers.AdminA).ConfigureAwait(false);
                    AssertContains("command.result", createdPersona, "a tenant administrator creates a persona: " + createdPersona);
                    Persona? storedPersona = await testDb.Driver.Personas.ReadByNameAsync(callers.TenantA, personaName).ConfigureAwait(false);
                    AssertNotNull(storedPersona, "the persona is created in the caller's tenant, not the tenant the body names");

                    string pipelineName = "WsTenantPipeline" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    string refusedPipeline = await SendAsync(handler, "create_pipeline", null, new { Name = pipelineName, Stages = new[] { new { PersonaName = "Worker" } } }, callers.UserA).ConfigureAwait(false);
                    AssertContains("\"code\":\"tenant_administrator_required\"", refusedPipeline, "a tenant user cannot create a pipeline: " + refusedPipeline);
                    AssertNull(await testDb.Driver.Pipelines.ReadByNameAsync(callers.TenantA, pipelineName).ConfigureAwait(false), "the refused create writes nothing");

                    string createdPipeline = await SendAsync(handler, "create_pipeline", null, new { Name = pipelineName, TenantId = callers.TenantB, Stages = new[] { new { PersonaName = "Worker" } } }, callers.AdminA).ConfigureAwait(false);
                    AssertContains("command.result", createdPipeline, "a tenant administrator creates a pipeline: " + createdPipeline);
                    AssertNotNull(await testDb.Driver.Pipelines.ReadByNameAsync(callers.TenantA, pipelineName).ConfigureAwait(false), "the pipeline is created in the caller's tenant");
                }
            }).ConfigureAwait(false);

            // Operator record commands: every read, update and delete refuses non-administrators before it runs.
            await RunTest("OperatorRecordCommands_RefuseTenantCallers_AndWriteNothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                    OperatorRecords records = await OperatorRecords.SeedAsync(testDb.Driver, callers).ConfigureAwait(false);
                    string before = await records.SnapshotAsync(testDb.Driver).ConfigureAwait(false);
                    WebSocketCommandHandler handler = CreateHandler(testDb);

                    List<OperatorCall> calls = new List<OperatorCall>
                    {
                        new OperatorCall("get_fleet", records.Fleet.Id), new OperatorCall("update_fleet", records.Fleet.Id), new OperatorCall("delete_fleet", records.Fleet.Id),
                        new OperatorCall("list_fleets", null),
                        new OperatorCall("get_vessel", records.Vessel.Id), new OperatorCall("update_vessel", records.Vessel.Id), new OperatorCall("update_vessel_context", records.Vessel.Id), new OperatorCall("delete_vessel", records.Vessel.Id),
                        new OperatorCall("get_voyage", records.Voyage.Id), new OperatorCall("cancel_voyage", records.Voyage.Id), new OperatorCall("purge_voyage", records.Voyage.Id),
                        new OperatorCall("get_mission", records.Mission.Id), new OperatorCall("update_mission", records.Mission.Id), new OperatorCall("cancel_mission", records.Mission.Id), new OperatorCall("purge_mission", records.Mission.Id),
                        new OperatorCall("get_captain", records.Captain.Id), new OperatorCall("update_captain", records.Captain.Id), new OperatorCall("delete_captain", records.Captain.Id),
                        new OperatorCall("list_missions", null), new OperatorCall("list_captains", null), new OperatorCall("list_signals", null), new OperatorCall("list_events", null)
                    };

                    foreach (OperatorCall call in calls)
                    {
                        foreach (AuthContext caller in new[] { callers.AdminB, callers.UserA, callers.AdminA })
                        {
                            string json = await SendAsync(handler, call.Action, call.Id, new { Name = "renamed", Title = "renamed", ProjectContext = "renamed" }, caller).ConfigureAwait(false);
                            AssertContains("\"code\":\"global_administrator_required\"", json, call.Action + " refuses " + caller.PrincipalDisplay + ": " + json);
                            AssertFalse(json.Contains("ws-auth-", StringComparison.Ordinal), call.Action + " returns no record data");
                        }
                    }

                    AssertEqual(before, await records.SnapshotAsync(testDb.Driver).ConfigureAwait(false), "no refused command changed any record");
                }
            }).ConfigureAwait(false);

            await RunTest("CommandWithoutCaller_IsRefusedBeforeItRuns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                    OperatorRecords records = await OperatorRecords.SeedAsync(testDb.Driver, callers).ConfigureAwait(false);
                    string before = await records.SnapshotAsync(testDb.Driver).ConfigureAwait(false);

                    string json = await SendAsync(CreateHandler(testDb), "delete_fleet", records.Fleet.Id, new { }, null).ConfigureAwait(false);

                    AssertContains("\"code\":\"authentication_required\"", json, "a command without a caller is refused: " + json);
                    AssertEqual(before, await records.SnapshotAsync(testDb.Driver).ConfigureAwait(false), "the refused command wrote nothing");
                }
            }).ConfigureAwait(false);

            // Administrator updates keep the fields REST keeps.
            await RunTest("UpdateFleet_KeepsStoredOwnership", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                    OperatorRecords records = await OperatorRecords.SeedAsync(testDb.Driver, callers).ConfigureAwait(false);

                    string json = await SendAsync(CreateHandler(testDb), "update_fleet", records.Fleet.Id,
                        new { Name = "renamed-fleet", TenantId = callers.TenantB, UserId = callers.AdminB.UserId }, McpTestCaller.Operator).ConfigureAwait(false);

                    Fleet? stored = await testDb.Driver.Fleets.ReadAsync(records.Fleet.Id).ConfigureAwait(false);
                    AssertContains("command.result", json);
                    AssertEqual("renamed-fleet", stored!.Name, "the editable field is written");
                    AssertEqual(callers.TenantA, stored.TenantId, "the body cannot move the fleet to another tenant");
                    AssertEqual(callers.AdminA.UserId, stored.UserId, "the body cannot change the fleet's owner");
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateVessel_KeepsStoredOwnership", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                    OperatorRecords records = await OperatorRecords.SeedAsync(testDb.Driver, callers).ConfigureAwait(false);

                    string json = await SendAsync(CreateHandler(testDb), "update_vessel", records.Vessel.Id,
                        new { Name = "renamed-vessel", RepoUrl = records.Vessel.RepoUrl, TenantId = callers.TenantB, UserId = callers.AdminB.UserId }, McpTestCaller.Operator).ConfigureAwait(false);

                    Vessel? stored = await testDb.Driver.Vessels.ReadAsync(records.Vessel.Id).ConfigureAwait(false);
                    AssertContains("command.result", json);
                    AssertEqual("renamed-vessel", stored!.Name, "the editable field is written");
                    AssertEqual(callers.TenantA, stored.TenantId, "the body cannot move the vessel to another tenant");
                    AssertEqual(callers.AdminA.UserId, stored.UserId, "the body cannot change the vessel's owner");
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateMission_WritesMetadataOnly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                    OperatorRecords records = await OperatorRecords.SeedAsync(testDb.Driver, callers).ConfigureAwait(false);
                    WebSocketCommandHandler handler = CreateHandler(testDb);

                    string json = await SendAsync(handler, "update_mission", records.Mission.Id,
                        new { Title = "renamed-mission", Status = "Complete", TenantId = callers.TenantB, UserId = callers.AdminB.UserId }, McpTestCaller.Operator).ConfigureAwait(false);

                    Mission? stored = await testDb.Driver.Missions.ReadAsync(records.Mission.Id).ConfigureAwait(false);
                    AssertContains("command.result", json);
                    AssertEqual("renamed-mission", stored!.Title, "the metadata field is written");
                    AssertEqual(MissionStatusEnum.Pending, stored.Status, "the update cannot set a status past the transition gates");
                    AssertEqual(callers.TenantA, stored.TenantId, "the body cannot move the mission to another tenant");
                    AssertEqual(records.Vessel.Id, stored.VesselId, "the vessel binding is kept");

                    string moved = await SendAsync(handler, "update_mission", records.Mission.Id,
                        new { Title = "moved-mission", VesselId = "vsl_elsewhere" }, McpTestCaller.Operator).ConfigureAwait(false);
                    AssertContains("command.error", moved, "a vessel change is refused like the REST route");
                    AssertEqual("renamed-mission", (await testDb.Driver.Missions.ReadAsync(records.Mission.Id).ConfigureAwait(false))!.Title, "the refused update writes nothing");
                }
            }).ConfigureAwait(false);

            await RunTest("CreatePersona_CannotCreateBuiltIn_AndAppliesTheDefaultCaptainRule", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Callers callers = await Callers.SeedAsync(testDb.Driver).ConfigureAwait(false);
                    Captain foreign = new Captain("ws-auth-foreign-captain");
                    foreign.TenantId = callers.TenantB;
                    foreign = await testDb.Driver.Captains.CreateAsync(foreign).ConfigureAwait(false);
                    WebSocketCommandHandler handler = CreateHandler(testDb);

                    string builtIn = await SendAsync(handler, "create_persona", null, new { Name = "WsAuthBuiltIn", PromptTemplateName = "persona.worker", IsBuiltIn = true }, McpTestCaller.Operator).ConfigureAwait(false);
                    Persona? stored = await testDb.Driver.Personas.ReadByNameAsync("WsAuthBuiltIn").ConfigureAwait(false);
                    AssertNotNull(stored, "the persona is created: " + builtIn);
                    AssertFalse(stored!.IsBuiltIn, "a request cannot create a built-in persona");

                    string refused = await SendAsync(handler, "create_persona", null, new { Name = "WsAuthForeignDefault", PromptTemplateName = "persona.worker", DefaultCaptainId = foreign.Id }, McpTestCaller.Operator).ConfigureAwait(false);
                    AssertContains(PersonaDefaultCaptainRule.NotFoundErrorCode, refused, "a captain outside the persona's tenant is refused");
                    AssertNull(await testDb.Driver.Personas.ReadByNameAsync("WsAuthForeignDefault").ConfigureAwait(false), "the refused create writes nothing");
                }
            }).ConfigureAwait(false);

            await RunTest("CreatePipeline_CannotCreateBuiltIn", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string json = await SendAsync(CreateHandler(testDb), "create_pipeline", null, new { Name = "WsAuthBuiltInPipeline", IsBuiltIn = true, Stages = new[] { new { PersonaName = "Worker" } } }, McpTestCaller.Operator).ConfigureAwait(false);
                    Pipeline? stored = await testDb.Driver.Pipelines.ReadByNameAsync("WsAuthBuiltInPipeline").ConfigureAwait(false);
                    AssertNotNull(stored, "the pipeline is created: " + json);
                    AssertFalse(stored!.IsBuiltIn, "a request cannot create a built-in pipeline");
                }
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static WebSocketCommandHandler CreateHandler(TestDatabase testDb)
        {
            return new WebSocketCommandHandler(
                null!,
                testDb.Driver,
                null!,
                null,
                null,
                null,
                _ServerJsonOptions,
                mission => { },
                voyage => { });
        }

        private static async Task<string> SendAsync(WebSocketCommandHandler handler, string action, string? id, object data, AuthContext? caller)
        {
            string rawBody = JsonSerializer.Serialize(new { Route = "command", action = action, id = id, data = data });
            object result;
            try
            {
                result = await handler.HandleCommandAsync(action, new WebSocketCommand { Action = action, Id = id }, rawBody, caller).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The hub reports a thrown command as a command.error; a refusal must not depend on that path.
                return "{\"type\":\"command.exception\",\"error\":" + JsonSerializer.Serialize(ex.GetType().Name + ": " + ex.Message) + "}";
            }
            return JsonSerializer.Serialize(result, _ServerJsonOptions);
        }

        private static List<OwnedFamily> OwnedFamilies()
        {
            return new List<OwnedFamily>
            {
                new OwnedFamily(
                    "Persona",
                    "get_persona",
                    new[] { "update_persona", "delete_persona" },
                    true,
                    async (db, tenantId, userId, builtIn) =>
                    {
                        Persona persona = new Persona("WsAuthPersona" + Guid.NewGuid().ToString("N").Substring(0, 8), "persona.worker");
                        persona.TenantId = tenantId;
                        persona.UserId = userId;
                        persona.IsBuiltIn = builtIn;
                        persona.Description = "original";
                        persona = await db.Personas.CreateAsync(persona).ConfigureAwait(false);
                        return new OwnedRecordHandle(persona.Id, persona.Name);
                    },
                    async (db, handle) =>
                    {
                        Persona? stored = await db.Personas.ReadAsync(handle.Id).ConfigureAwait(false);
                        return stored != null && stored.Description == "original";
                    }),
                new OwnedFamily(
                    "Pipeline",
                    "get_pipeline",
                    new[] { "update_pipeline", "delete_pipeline" },
                    true,
                    async (db, tenantId, userId, builtIn) =>
                    {
                        Pipeline pipeline = new Pipeline("WsAuthPipeline" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        pipeline.TenantId = tenantId;
                        pipeline.UserId = userId;
                        pipeline.IsBuiltIn = builtIn;
                        pipeline.Description = "original";
                        pipeline = await db.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                        return new OwnedRecordHandle(pipeline.Id, pipeline.Name);
                    },
                    async (db, handle) =>
                    {
                        Pipeline? stored = await db.Pipelines.ReadAsync(handle.Id).ConfigureAwait(false);
                        return stored != null && stored.Description == "original";
                    }),
                new OwnedFamily(
                    "PromptTemplate",
                    "get_prompt_template",
                    new[] { "update_prompt_template" },
                    false,
                    async (db, tenantId, userId, builtIn) =>
                    {
                        PromptTemplate template = new PromptTemplate("ws.auth." + Guid.NewGuid().ToString("N").Substring(0, 8), "original");
                        template.TenantId = tenantId;
                        template.UserId = userId;
                        template.IsBuiltIn = builtIn;
                        template.Description = "original";
                        template = await db.PromptTemplates.CreateAsync(template).ConfigureAwait(false);
                        return new OwnedRecordHandle(template.Id, template.Name);
                    },
                    async (db, handle) =>
                    {
                        PromptTemplate? stored = await db.PromptTemplates.ReadAsync(handle.Id).ConfigureAwait(false);
                        return stored != null && stored.Content == "original";
                    })
            };
        }

        #endregion

        #region Private-Types

        private sealed class OwnedRecordHandle
        {
            public string Id { get; }
            public string Name { get; }

            public OwnedRecordHandle(string id, string name)
            {
                Id = id;
                Name = name;
            }
        }

        private sealed class OwnedFamily
        {
            public string Label { get; }
            public string GetAction { get; }
            public string[] WriteActions { get; }
            public bool TenantAdministratorsMayWrite { get; }
            public Func<DatabaseDriver, string, string, bool, Task<OwnedRecordHandle>> SeedAsync { get; }
            public Func<DatabaseDriver, OwnedRecordHandle, Task<bool>> IsUnchangedAsync { get; }

            public OwnedFamily(
                string label,
                string getAction,
                string[] writeActions,
                bool tenantAdministratorsMayWrite,
                Func<DatabaseDriver, string, string, bool, Task<OwnedRecordHandle>> seedAsync,
                Func<DatabaseDriver, OwnedRecordHandle, Task<bool>> isUnchangedAsync)
            {
                Label = label;
                GetAction = getAction;
                WriteActions = writeActions;
                TenantAdministratorsMayWrite = tenantAdministratorsMayWrite;
                SeedAsync = seedAsync;
                IsUnchangedAsync = isUnchangedAsync;
            }
        }

        private sealed class OperatorCall
        {
            public string Action { get; }
            public string? Id { get; }

            public OperatorCall(string action, string? id)
            {
                Action = action;
                Id = id;
            }
        }

        /// <summary>
        /// Two tenants: an administrator in each, and an ordinary user in tenant A who may read but not edit.
        /// </summary>
        private sealed class Callers
        {
            public string TenantA { get; private set; } = "";
            public string TenantB { get; private set; } = "";
            public AuthContext AdminA { get; private set; } = null!;
            public AuthContext UserA { get; private set; } = null!;
            public AuthContext AdminB { get; private set; } = null!;

            public static async Task<Callers> SeedAsync(DatabaseDriver db)
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                TenantMetadata tenantA = await db.Tenants.CreateAsync(new TenantMetadata("ws-auth-a-" + suffix)).ConfigureAwait(false);
                TenantMetadata tenantB = await db.Tenants.CreateAsync(new TenantMetadata("ws-auth-b-" + suffix)).ConfigureAwait(false);
                UserMaster adminA = await db.Users.CreateAsync(new UserMaster(tenantA.Id, "ws-auth-admin-a-" + suffix + "@example.com", "password")).ConfigureAwait(false);
                UserMaster userA = await db.Users.CreateAsync(new UserMaster(tenantA.Id, "ws-auth-user-a-" + suffix + "@example.com", "password")).ConfigureAwait(false);
                UserMaster adminB = await db.Users.CreateAsync(new UserMaster(tenantB.Id, "ws-auth-admin-b-" + suffix + "@example.com", "password")).ConfigureAwait(false);
                Callers callers = new Callers();
                callers.TenantA = tenantA.Id;
                callers.TenantB = tenantB.Id;
                callers.AdminA = AuthContext.Authenticated(tenantA.Id, adminA.Id, false, true, "Test", null, "tenant A administrator");
                callers.UserA = AuthContext.Authenticated(tenantA.Id, userA.Id, false, false, "Test", null, "tenant A user");
                callers.AdminB = AuthContext.Authenticated(tenantB.Id, adminB.Id, false, true, "Test", null, "tenant B administrator");
                return callers;
            }
        }

        /// <summary>
        /// One record of each operator entity, owned by tenant A's administrator.
        /// </summary>
        private sealed class OperatorRecords
        {
            public Fleet Fleet { get; private set; } = null!;
            public Vessel Vessel { get; private set; } = null!;
            public Voyage Voyage { get; private set; } = null!;
            public Mission Mission { get; private set; } = null!;
            public Captain Captain { get; private set; } = null!;

            public static async Task<OperatorRecords> SeedAsync(DatabaseDriver db, Callers callers)
            {
                OperatorRecords records = new OperatorRecords();
                Fleet fleet = new Fleet("ws-auth-fleet");
                fleet.TenantId = callers.TenantA;
                fleet.UserId = callers.AdminA.UserId;
                records.Fleet = await db.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Vessel vessel = new Vessel("ws-auth-vessel", "https://github.com/test/ws-auth.git");
                vessel.TenantId = callers.TenantA;
                vessel.UserId = callers.AdminA.UserId;
                vessel.FleetId = records.Fleet.Id;
                records.Vessel = await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Voyage voyage = new Voyage("ws-auth-voyage", "voyage");
                voyage.TenantId = callers.TenantA;
                voyage.UserId = callers.AdminA.UserId;
                voyage.Status = VoyageStatusEnum.Complete;
                records.Voyage = await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                Mission mission = new Mission("ws-auth-mission");
                mission.TenantId = callers.TenantA;
                mission.UserId = callers.AdminA.UserId;
                mission.VesselId = records.Vessel.Id;
                mission.VoyageId = records.Voyage.Id;
                records.Mission = await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                Captain captain = new Captain("ws-auth-captain");
                captain.TenantId = callers.TenantA;
                captain.UserId = callers.AdminA.UserId;
                records.Captain = await db.Captains.CreateAsync(captain).ConfigureAwait(false);
                return records;
            }

            public async Task<string> SnapshotAsync(DatabaseDriver db)
            {
                Fleet? fleet = await db.Fleets.ReadAsync(Fleet.Id).ConfigureAwait(false);
                Vessel? vessel = await db.Vessels.ReadAsync(Vessel.Id).ConfigureAwait(false);
                Voyage? voyage = await db.Voyages.ReadAsync(Voyage.Id).ConfigureAwait(false);
                Mission? mission = await db.Missions.ReadAsync(Mission.Id).ConfigureAwait(false);
                Captain? captain = await db.Captains.ReadAsync(Captain.Id).ConfigureAwait(false);
                return (fleet == null ? "no-fleet" : fleet.Name + "|" + fleet.TenantId)
                    + ";" + (vessel == null ? "no-vessel" : vessel.Name + "|" + vessel.TenantId + "|" + vessel.ProjectContext)
                    + ";" + (voyage == null ? "no-voyage" : voyage.Title + "|" + voyage.Status)
                    + ";" + (mission == null ? "no-mission" : mission.Title + "|" + mission.Status)
                    + ";" + (captain == null ? "no-captain" : captain.Name + "|" + captain.TenantId);
            }
        }

        #endregion
    }
}
