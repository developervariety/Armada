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
    /// tenant or a missing user.
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

            await RunTest("UpdatePromptTemplate_CreatingANewNameIsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    PromptTemplateService templates = new PromptTemplateService(testDb.Driver, CreateLogging());
                    Func<JsonElement?, Task<object>> upsert = Capture(r => McpPromptTemplateTools.Register(r, testDb.Driver, templates), "update_prompt_template");

                    PromptTemplate created = (PromptTemplate)await CallAsAsync(upsert, caller, new { name = "owned.template", content = "body" }).ConfigureAwait(false);

                    PromptTemplate? stored = await testDb.Driver.PromptTemplates.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the template belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the template belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("CreateWorkflowProfile_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
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
