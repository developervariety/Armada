namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A record created through an MCP create tool is owned by the authenticated caller: its tenant and user
    /// are the caller's, exactly as the matching REST create sets them. Each test acts as a non-administrator
    /// in a tenant other than the default one, so a create that ignores the caller is visible as a default
    /// tenant or a missing user. Persona and pipeline changes follow the REST rule too: only the owning tenant's
    /// administrator, or a global administrator, may make them.
    /// </summary>
    public class McpCreateOwnershipTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MCP Create Ownership";

        private static async Task<AuthContext> SeedCallerAsync(TestDatabase testDb)
        {
            TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("owner-tenant-" + Guid.NewGuid().ToString("N"))).ConfigureAwait(false);
            UserMaster user = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "owner-" + Guid.NewGuid().ToString("N") + "@example.com", "password")).ConfigureAwait(false);
            return AuthContext.Authenticated(tenant.Id, user.Id, false, false, "Test");
        }

        private static async Task<object> CallAsAsync(Func<JsonElement?, Task<object>> handler, AuthContext caller, object args)
        {
            using (McpCallerContext.Begin(caller))
            {
                return await handler(JsonSerializer.SerializeToElement(args)).ConfigureAwait(false);
            }
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static Func<JsonElement?, Task<object>> Capture(Action<RegisterToolDelegate> register, string toolName)
        {
            Func<JsonElement?, Task<object>>? captured = null;
            register((name, _, _, handler) => { if (name == toolName) captured = handler; });
            if (captured == null) throw new InvalidOperationException(toolName + " handler must be registered");
            return captured;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateFleet_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> create = Capture(r => McpFleetTools.Register(r, testDb.Driver), "armada_create_fleet");

                    Fleet created = (Fleet)await CallAsAsync(create, caller, new { name = "owned-fleet" }).ConfigureAwait(false);

                    Fleet? stored = await testDb.Driver.Fleets.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the fleet belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the fleet belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("CreateCaptain_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = new ArmadaSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(testDb.Driver, settings, logging);
                    Func<JsonElement?, Task<object>> create = Capture(
                        r => McpCaptainTools.Register(r, testDb.Driver, null!, settings, null, null, logging, quarantine),
                        "armada_create_captain");

                    string json = JsonSerializer.Serialize(await CallAsAsync(create, caller, new { name = "owned-captain" }).ConfigureAwait(false));
                    List<Captain> captains = await testDb.Driver.Captains.EnumerateAsync().ConfigureAwait(false);
                    Captain? stored = captains.Find(c => c.Name == "owned-captain");
                    AssertNotNull(stored, "the captain is created: " + json);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the captain belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the captain belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("SendSignal_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    Captain recipient = await testDb.Driver.Captains.CreateAsync(new Captain("signal-recipient")).ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> send = Capture(r => McpSignalTools.Register(r, testDb.Driver), "armada_send_signal");

                    Signal created = (Signal)await CallAsAsync(send, caller, new { captainId = recipient.Id, message = "hello" }).ConfigureAwait(false);

                    Signal? stored = await testDb.Driver.Signals.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the signal belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the signal belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("NudgeVoyage_MailboxSignalIsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("owned mailbox voyage")).ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> nudge = Capture(r => McpSignalTools.Register(r, testDb.Driver), "armada_nudge_voyage");

                    Signal created = (Signal)await CallAsAsync(nudge, caller, new { voyageId = voyage.Id, type = "Mail", message = "note" }).ConfigureAwait(false);

                    Signal? stored = await testDb.Driver.Signals.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the mailbox signal belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the mailbox signal belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("CreatePlaybook_IsOwnedByTheCallerAndUniqueWithinItsTenant", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    Playbook defaultTenantCopy = new Playbook("shared-name.md", "# default tenant copy");
                    defaultTenantCopy.TenantId = Armada.Core.Constants.DefaultTenantId;
                    defaultTenantCopy.UserId = Armada.Core.Constants.DefaultUserId;
                    await testDb.Driver.Playbooks.CreateAsync(defaultTenantCopy).ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> create = Capture(r => McpPlaybookTools.Register(r, testDb.Driver, CreateLogging()), "create_playbook");

                    object result = await CallAsAsync(create, caller, new { fileName = "shared-name.md", content = "# caller copy" }).ConfigureAwait(false);
                    Playbook? created = result as Playbook;
                    AssertNotNull(created, "a file name used only in another tenant does not block the caller: " + JsonSerializer.Serialize(result));

                    Playbook? stored = await testDb.Driver.Playbooks.ReadAsync(created!.Id).ConfigureAwait(false);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the playbook belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the playbook belongs to the calling user");

                    string duplicate = JsonSerializer.Serialize(await CallAsAsync(create, caller, new { fileName = "shared-name.md", content = "# again" }).ConfigureAwait(false));
                    AssertContains("already exists", duplicate, "a second playbook of the same name in the caller's tenant is refused");
                }
            }).ConfigureAwait(false);

            await RunTest("CreatePromptTemplate_IsOwnedByTheCaller_AndUpdateNeverCreates", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    PromptTemplateService templates = new PromptTemplateService(testDb.Driver, CreateLogging());
                    Func<JsonElement?, Task<object>> create = Capture(r => McpPromptTemplateTools.Register(r, testDb.Driver, templates), "create_prompt_template");
                    Func<JsonElement?, Task<object>> update = Capture(r => McpPromptTemplateTools.Register(r, testDb.Driver, templates), "update_prompt_template");

                    // An update of a name that does not exist is refused; it no longer creates the template.
                    string refused = JsonSerializer.Serialize(await CallAsAsync(update, caller, new { name = "missing.template", content = "body" }).ConfigureAwait(false));
                    AssertContains("not_found", refused, "an update of a missing template is refused: " + refused);
                    AssertNull(await testDb.Driver.PromptTemplates.ReadByNameAsync("missing.template").ConfigureAwait(false), "the refused update creates nothing");

                    PromptTemplate created = (PromptTemplate)await CallAsAsync(create, caller, new { name = "owned.template", category = "mission", content = "body" }).ConfigureAwait(false);
                    PromptTemplate? stored = await testDb.Driver.PromptTemplates.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the template belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the template belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("CreateWorkflowProfile_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Every surface requires a tenant administrator to manage workflow profiles, so the caller is
                    // one; it is still not a global administrator, so the record's tenant field is ignored.
                    AuthContext seeded = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    AuthContext caller = AuthContext.Authenticated(seeded.TenantId!, seeded.UserId!, false, true, "Test");
                    WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, CreateLogging());
                    Func<JsonElement?, Task<object>> create = Capture(r => McpWorkflowProfileTools.Register(r, testDb.Driver, profiles), "create_workflow_profile");

                    object result = await CallAsAsync(create, caller, new
                    {
                        profile = new
                        {
                            name = "owned-profile",
                            tenantId = Armada.Core.Constants.DefaultTenantId,
                            scope = "Global",
                            buildCommand = "echo build"
                        }
                    }).ConfigureAwait(false);
                    WorkflowProfile? created = result as WorkflowProfile;
                    AssertNotNull(created, "the profile is created: " + JsonSerializer.Serialize(result));

                    WorkflowProfile? stored = await testDb.Driver.WorkflowProfiles.ReadAsync(created!.Id).ConfigureAwait(false);
                    AssertEqual(caller.TenantId, stored!.TenantId, "a non-administrator's profile belongs to the caller's tenant, whatever tenant the record names");
                    AssertEqual(caller.UserId, stored.UserId, "the profile belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("PersonaAndPipelineChanges_OnlyTheOwningTenantsAdministratorMayMakeThem", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext user = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    AuthContext owningAdmin = AuthContext.Authenticated(user.TenantId!, user.UserId! + "_admin", false, true, "Test");
                    AuthContext otherTenantAdmin = AuthContext.Authenticated("ten_other_" + Guid.NewGuid().ToString("N"), "usr_other_admin", false, true, "Test");

                    // The tool policy matches REST: a tenant administrator reaches persona and pipeline changes, a
                    // tenant user does not.
                    foreach (string tool in new[] { "create_persona", "update_persona", "delete_persona", "create_pipeline", "update_pipeline", "delete_pipeline" })
                    {
                        AssertTrue(McpToolAccessPolicy.IsAllowed(owningAdmin, tool), tool + " admits a tenant administrator");
                        AssertFalse(McpToolAccessPolicy.IsAllowed(user, tool), tool + " refuses a tenant user");
                    }

                    Func<JsonElement?, Task<object>> updatePipeline = Capture(r => McpPipelineTools.Register(r, testDb.Driver), "update_pipeline");
                    Func<JsonElement?, Task<object>> deletePipeline = Capture(r => McpPipelineTools.Register(r, testDb.Driver), "delete_pipeline");
                    Func<JsonElement?, Task<object>> updatePersona = Capture(r => McpPersonaTools.Register(r, testDb.Driver), "update_persona");
                    Func<JsonElement?, Task<object>> deletePersona = Capture(r => McpPersonaTools.Register(r, testDb.Driver), "delete_persona");

                    Pipeline pipeline = new Pipeline("McpOwnedPipeline" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    pipeline.TenantId = user.TenantId;
                    pipeline.UserId = user.UserId;
                    pipeline.Description = "original";
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                    Persona persona = new Persona("McpOwnedPersona" + Guid.NewGuid().ToString("N").Substring(0, 8), "persona.worker");
                    persona.TenantId = user.TenantId;
                    persona.UserId = user.UserId;
                    persona.Description = "original";
                    persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    // Another tenant's administrator cannot find either record by name, so nothing is changed or deleted.
                    string foreignUpdate = JsonSerializer.Serialize(await CallAsAsync(updatePipeline, otherTenantAdmin, new { name = pipeline.Name, description = "changed" }).ConfigureAwait(false));
                    AssertContains("Pipeline not found", foreignUpdate, "another tenant's administrator cannot update the pipeline: " + foreignUpdate);
                    string foreignDelete = JsonSerializer.Serialize(await CallAsAsync(deletePipeline, otherTenantAdmin, new { name = pipeline.Name }).ConfigureAwait(false));
                    AssertContains("Pipeline not found", foreignDelete, "another tenant's administrator cannot delete the pipeline: " + foreignDelete);
                    string foreignPersona = JsonSerializer.Serialize(await CallAsAsync(deletePersona, otherTenantAdmin, new { name = persona.Name }).ConfigureAwait(false));
                    AssertContains("Persona not found", foreignPersona, "another tenant's administrator cannot delete the persona: " + foreignPersona);
                    Pipeline? unchanged = await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(unchanged, "the refused delete keeps the pipeline");
                    AssertEqual("original", unchanged!.Description, "the refused update writes nothing");
                    AssertNotNull(await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false), "the refused delete keeps the persona");

                    // The owning tenant's administrator may change both.
                    await CallAsAsync(updatePipeline, owningAdmin, new { name = pipeline.Name, description = "changed" }).ConfigureAwait(false);
                    AssertEqual("changed", (await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false))!.Description, "the owning tenant's administrator updates the pipeline");
                    await CallAsAsync(updatePersona, owningAdmin, new { name = persona.Name, description = "changed" }).ConfigureAwait(false);
                    AssertEqual("changed", (await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false))!.Description, "the owning tenant's administrator updates the persona");
                    await CallAsAsync(deletePipeline, owningAdmin, new { name = pipeline.Name }).ConfigureAwait(false);
                    AssertNull(await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false), "the owning tenant's administrator deletes the pipeline");
                }
            }).ConfigureAwait(false);

            await RunTest("EnqueueMerge_IsOwnedByTheCaller", async () =>
            {
                AuthContext caller = AuthContext.Authenticated("ten_merge_owner", "usr_merge_owner", false, false, "Test");
                IMergeQueueService mergeQueue = DispatchProxy.Create<IMergeQueueService, RecordingMergeQueueProxy>();
                Func<JsonElement?, Task<object>> enqueue = Capture(r => McpMergeQueueTools.Register(r, mergeQueue), "armada_enqueue_merge");

                MergeEntry entry = (MergeEntry)await CallAsAsync(enqueue, caller, new { vesselId = "vsl_owned", branchName = "feature/owned" }).ConfigureAwait(false);

                AssertEqual("ten_merge_owner", entry.TenantId, "the merge entry belongs to the caller's tenant");
                AssertEqual("usr_merge_owner", entry.UserId, "the merge entry belongs to the calling user");
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Merge queue stand-in that returns the entry passed to EnqueueAsync unchanged, so a test reads the
        /// owner the tool set without a landing pipeline.
        /// </summary>
        public class RecordingMergeQueueProxy : DispatchProxy
        {
            /// <inheritdoc />
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod != null && targetMethod.Name == "EnqueueAsync" && args != null && args.Length > 0 && args[0] is MergeEntry entry)
                    return Task.FromResult(entry);
                throw new NotSupportedException((targetMethod?.Name ?? "unknown") + " is not used by these tests.");
            }
        }
    }
}
