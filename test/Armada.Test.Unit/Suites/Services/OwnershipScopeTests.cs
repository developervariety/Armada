namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Behavioural tests for the shared ownership rule and the consumers that apply it: the
    /// authorization matrix across two tenants and two users, scoped paging and name lookup of
    /// personas, pipelines and prompt templates, and dispatch-time use of a private record.
    /// </summary>
    public class OwnershipScopeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Ownership Scope";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Policy matrix covers anonymous, users, tenant administrators and global administrators", () =>
            {
                AuthContext anonymous = new AuthContext();
                AuthContext userA1 = User("ten_a", "usr_a1");
                AuthContext userA2 = User("ten_a", "usr_a2");
                AuthContext tenantAdminA = TenantAdmin("ten_a", "usr_admin_a");
                AuthContext tenantAdminB = TenantAdmin("ten_b", "usr_admin_b");
                AuthContext userB = User("ten_b", "usr_b1");
                AuthContext global = AuthContext.Authenticated("ten_other", "usr_global", true, true, "Bearer");

                // Tenant-wide record in tenant A.
                AssertFalse(OwnershipPolicy.CanView(anonymous, "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide), "anonymous view tenant-wide");
                AssertTrue(OwnershipPolicy.CanView(userA2, "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide), "same-tenant user view tenant-wide");
                AssertFalse(OwnershipPolicy.CanEdit(userA1, "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide), "owner cannot edit tenant-wide");
                AssertTrue(OwnershipPolicy.CanEdit(tenantAdminA, "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide), "tenant admin edit tenant-wide");
                AssertFalse(OwnershipPolicy.CanView(userB, "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide), "other-tenant user view");
                AssertFalse(OwnershipPolicy.CanView(tenantAdminB, "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide), "other-tenant admin view");
                AssertTrue(OwnershipPolicy.CanEdit(global, "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide), "global admin edit");

                // User-specific record of user A1.
                AssertTrue(OwnershipPolicy.CanView(userA1, "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific), "owner view");
                AssertTrue(OwnershipPolicy.CanEdit(userA1, "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific), "owner edit");
                AssertFalse(OwnershipPolicy.CanView(userA2, "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific), "same-tenant other user view");
                AssertFalse(OwnershipPolicy.CanEdit(userA2, "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific), "same-tenant other user edit");
                AssertTrue(OwnershipPolicy.CanView(tenantAdminA, "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific), "tenant admin view");
                AssertFalse(OwnershipPolicy.CanView(tenantAdminB, "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific), "other tenant admin view");
                AssertTrue(OwnershipPolicy.CanView(global, "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific), "global admin view");

                // A shared record is readable by any authenticated caller and grants no edit.
                AssertTrue(OwnershipPolicy.CanView(userB, "default", null, OwnershipScopeEnum.TenantWide, true), "shared view");
                AssertFalse(OwnershipPolicy.CanView(anonymous, "default", null, OwnershipScopeEnum.TenantWide, true), "anonymous shared view");
                AssertFalse(OwnershipPolicy.CanEdit(userB, "default", null, OwnershipScopeEnum.TenantWide), "shared edit");
                return Task.CompletedTask;
            });

            await RunTest("A private record is never usable for another owner's work, even when an administrator starts it", () =>
            {
                AssertTrue(OwnershipPolicy.CanUseFor("ten_a", "usr_a1", "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific, false), "owner's own work");
                AssertFalse(OwnershipPolicy.CanUseFor("ten_a", "usr_a2", "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific, false), "other user's work");
                AssertTrue(OwnershipPolicy.CanUseFor("ten_a", "usr_a2", "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide, false), "tenant-wide in tenant");
                AssertFalse(OwnershipPolicy.CanUseFor("ten_b", "usr_b1", "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide, false), "other tenant");
                AssertTrue(OwnershipPolicy.CanUseFor("ten_b", "usr_b1", "default", null, OwnershipScopeEnum.TenantWide, true), "shared");
                return Task.CompletedTask;
            });

            await RunTest("A record or owner without a tenant belongs to the default tenant", () =>
            {
                // Records written before tenancy, and work with no owner, carry empty owner fields.
                // They must keep matching each other, or every such pipeline is refused at dispatch.
                AssertTrue(OwnershipPolicy.CanUseFor(null, null, null, null, OwnershipScopeEnum.TenantWide, false), "unowned record for unowned work");
                AssertTrue(OwnershipPolicy.CanUseFor(null, null, "default", null, OwnershipScopeEnum.TenantWide, false), "default-tenant record for unowned work");
                AssertTrue(OwnershipPolicy.CanUseFor("default", "default", null, null, OwnershipScopeEnum.TenantWide, false), "unowned record for default-owner work");
                AssertFalse(OwnershipPolicy.CanUseFor("ten_a", "usr_a1", null, null, OwnershipScopeEnum.TenantWide, false), "unowned record stays in the default tenant");
                AssertTrue(OwnershipPolicy.CanView(User("default", "usr_x"), null, null, OwnershipScopeEnum.TenantWide), "default-tenant user views an unowned tenant-wide record");
                AssertFalse(OwnershipPolicy.CanView(User("ten_a", "usr_a1"), null, null, OwnershipScopeEnum.TenantWide), "other tenant does not view an unowned record");
                return Task.CompletedTask;
            });

            await RunTest("Scoped paging counts only visible personas and keeps built-ins", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedOwnersAsync(testDb).ConfigureAwait(false);
                    await CreatePersonaAsync(testDb, "BuiltInShared", "default", null, OwnershipScopeEnum.TenantWide, true).ConfigureAwait(false);
                    await CreatePersonaAsync(testDb, "TenantAWide", "ten_a", "usr_a1", OwnershipScopeEnum.TenantWide, false).ConfigureAwait(false);
                    await CreatePersonaAsync(testDb, "TenantAPrivate", "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific, false).ConfigureAwait(false);
                    await CreatePersonaAsync(testDb, "TenantBWide", "ten_b", "usr_b1", OwnershipScopeEnum.TenantWide, false).ConfigureAwait(false);

                    List<Persona> all = await testDb.Driver.Personas.EnumerateAsync().ConfigureAwait(false);
                    EnumerationQuery query = new EnumerationQuery { PageNumber = 1, PageSize = 2 };

                    EnumerationResult<Persona> other = OwnedRecordScope.Page(all, User("ten_a", "usr_a2"), query);
                    AssertEqual(2L, other.TotalRecords, "Same-tenant other user sees shared and tenant-wide records only");
                    AssertFalse(other.Objects.Any(p => p.Name == "TenantAPrivate" || p.Name == "TenantBWide"), "No private or other-tenant record on the page");

                    EnumerationResult<Persona> owner = OwnedRecordScope.Page(all, User("ten_a", "usr_a1"), query);
                    AssertEqual(3L, owner.TotalRecords, "Owner also sees its private record");
                    AssertEqual(2, owner.TotalPages, "Pages are computed from the visible total");

                    EnumerationResult<Persona> global = OwnedRecordScope.Page(all, AuthContext.Authenticated("default", "default", true, true, "ApiKey"), new EnumerationQuery { PageSize = 100 });
                    AssertEqual((long)all.Count, global.TotalRecords, "A global administrator sees everything");

                    EnumerationResult<Persona> anonymous = OwnedRecordScope.Page(all, new AuthContext(), new EnumerationQuery { PageSize = 100 });
                    AssertEqual(0L, anonymous.TotalRecords, "An anonymous caller sees nothing");
                }
            });

            await RunTest("Name lookup prefers a visible own-tenant record and otherwise falls back to the shared record", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedOwnersAsync(testDb).ConfigureAwait(false);
                    await CreatePersonaAsync(testDb, "Reviewer", "default", null, OwnershipScopeEnum.TenantWide, true).ConfigureAwait(false);
                    await CreatePersonaAsync(testDb, "Reviewer", "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific, false).ConfigureAwait(false);
                    await CreatePersonaAsync(testDb, "Reviewer", "ten_b", "usr_b1", OwnershipScopeEnum.TenantWide, false).ConfigureAwait(false);

                    Persona? owner = await ReadPersonaAsync(testDb, User("ten_a", "usr_a1"), "Reviewer").ConfigureAwait(false);
                    AssertEqual("ten_a", owner!.TenantId, "Owner reads its own tenant's record");

                    Persona? otherUser = await ReadPersonaAsync(testDb, User("ten_a", "usr_a2"), "Reviewer").ConfigureAwait(false);
                    AssertTrue(otherUser!.IsBuiltIn, "Another user falls back to the shared record, never the private one");

                    Persona? tenantB = await ReadPersonaAsync(testDb, User("ten_b", "usr_b2"), "Reviewer").ConfigureAwait(false);
                    AssertEqual("ten_b", tenantB!.TenantId, "Tenant B reads its own tenant-wide record");

                    Persona? anonymous = await ReadPersonaAsync(testDb, new AuthContext(), "Reviewer").ConfigureAwait(false);
                    AssertNull(anonymous, "An anonymous caller reads nothing");

                    Persona? missing = await ReadPersonaAsync(testDb, User("ten_c", "usr_c"), "TenantOnly").ConfigureAwait(false);
                    AssertNull(missing, "An absent name reads nothing");
                }
            });

            await RunTest("Ownership columns persist through create, update and reopen", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedOwnersAsync(testDb).ConfigureAwait(false);
                    Persona persona = await CreatePersonaAsync(testDb, "Persisted", "ten_a", "usr_a1", OwnershipScopeEnum.UserSpecific, false).ConfigureAwait(false);
                    Persona? read = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertEqual("usr_a1", read!.UserId, "Persona user");
                    AssertEqual(OwnershipScopeEnum.UserSpecific, read.OwnershipScope, "Persona scope");
                    read.OwnershipScope = OwnershipScopeEnum.TenantWide;
                    await testDb.Driver.Personas.UpdateAsync(read).ConfigureAwait(false);
                    AssertEqual(OwnershipScopeEnum.TenantWide, (await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false))!.OwnershipScope, "Persona scope updated");

                    Pipeline pipeline = new Pipeline("PrivatePipeline") { TenantId = "ten_a", UserId = "usr_a1", OwnershipScope = OwnershipScopeEnum.UserSpecific };
                    pipeline.Stages.Add(new PipelineStage(1, "Worker"));
                    await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                    Pipeline? readPipeline = await testDb.Driver.Pipelines.ReadByNameAsync("ten_a", "PrivatePipeline").ConfigureAwait(false);
                    AssertEqual("usr_a1", readPipeline!.UserId, "Pipeline user");
                    AssertEqual(OwnershipScopeEnum.UserSpecific, readPipeline.OwnershipScope, "Pipeline scope");

                    PromptTemplate template = new PromptTemplate("private.template", "content") { TenantId = "ten_a", UserId = "usr_a1", OwnershipScope = OwnershipScopeEnum.UserSpecific };
                    await testDb.Driver.PromptTemplates.CreateAsync(template).ConfigureAwait(false);
                    PromptTemplate? readTemplate = await testDb.Driver.PromptTemplates.ReadAsync(template.Id).ConfigureAwait(false);
                    AssertEqual("usr_a1", readTemplate!.UserId, "Template user");
                    AssertEqual(OwnershipScopeEnum.UserSpecific, readTemplate.OwnershipScope, "Template scope");
                }
            });

            await RunTest("Dispatch does not use another user's private pipeline through a vessel default", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedOwnersAsync(testDb).ConfigureAwait(false);
                    Pipeline privatePipeline = new Pipeline("OwnerOnly") { TenantId = "ten_a", UserId = "usr_a1", OwnershipScope = OwnershipScopeEnum.UserSpecific };
                    privatePipeline.Stages.Add(new PipelineStage(1, "Worker"));
                    privatePipeline = await testDb.Driver.Pipelines.CreateAsync(privatePipeline).ConfigureAwait(false);

                    Vessel otherUsersVessel = new Vessel("other-users-vessel", "https://example.invalid/other.git");
                    otherUsersVessel.TenantId = "ten_a";
                    otherUsersVessel.UserId = "usr_a2";
                    otherUsersVessel.DefaultPipelineId = privatePipeline.Id;
                    otherUsersVessel = await testDb.Driver.Vessels.CreateAsync(otherUsersVessel).ConfigureAwait(false);

                    Vessel ownersVessel = new Vessel("owners-vessel", "https://example.invalid/owner.git");
                    ownersVessel.TenantId = "ten_a";
                    ownersVessel.UserId = "usr_a1";
                    ownersVessel.DefaultPipelineId = privatePipeline.Id;
                    ownersVessel = await testDb.Driver.Vessels.CreateAsync(ownersVessel).ConfigureAwait(false);

                    AdmiralService admiral = CreateAdmiralService(testDb.Driver, new ArmadaSettings());

                    Pipeline? forOther = await admiral.ResolvePipelineAsync(null, otherUsersVessel).ConfigureAwait(false);
                    AssertNull(forOther, "Another user's private pipeline is not inherited");
                    Pipeline? explicitForOther = await admiral.ResolvePipelineAsync(privatePipeline.Id, otherUsersVessel).ConfigureAwait(false);
                    AssertNull(explicitForOther, "Another user's private pipeline is not usable by id");
                    Pipeline? byNameForOther = await admiral.ResolvePipelineAsync("OwnerOnly", otherUsersVessel).ConfigureAwait(false);
                    AssertNull(byNameForOther, "Another user's private pipeline is not usable by name");

                    Pipeline? forOwner = await admiral.ResolvePipelineAsync(null, ownersVessel).ConfigureAwait(false);
                    AssertNotNull(forOwner, "The owner's own vessel still inherits its private pipeline");

                    Vessel reread = (await testDb.Driver.Vessels.ReadAsync(otherUsersVessel.Id).ConfigureAwait(false))!;
                    AssertEqual(privatePipeline.Id, reread.DefaultPipelineId, "An unusable reference is refused, not cleared as missing");
                }
            });

            await RunTest("A user-specific prompt template is never resolved into a mission prompt", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedOwnersAsync(testDb).ConfigureAwait(false);
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    // A fresh database holds no templates until the service seeds its built-in defaults.
                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplate embedded = (await service.ResolveAsync("mission.rules").ConfigureAwait(false))!;
                    AssertNotNull(embedded, "The shared mission.rules template resolves");

                    PromptTemplate shadow = new PromptTemplate("mission.rules.private-shadow", "private") { TenantId = "default", UserId = "usr_a1", OwnershipScope = OwnershipScopeEnum.UserSpecific };
                    await testDb.Driver.PromptTemplates.CreateAsync(shadow).ConfigureAwait(false);
                    AssertNull(await service.ResolveAsync("mission.rules.private-shadow").ConfigureAwait(false), "A private template with no shared default resolves to nothing");

                    PromptTemplate stored = (await testDb.Driver.PromptTemplates.ReadByNameAsync("mission.rules").ConfigureAwait(false))!;
                    stored.OwnershipScope = OwnershipScopeEnum.UserSpecific;
                    stored.Content = "private override";
                    await testDb.Driver.PromptTemplates.UpdateAsync(stored).ConfigureAwait(false);
                    PromptTemplate resolved = (await service.ResolveAsync("mission.rules").ConfigureAwait(false))!;
                    AssertNotEqual("private override", resolved.Content, "A private record never replaces the shared template");
                    AssertEqual(embedded.Content, resolved.Content, "The embedded default is used instead");
                }
            });
        }

        private static AuthContext User(string tenantId, string userId)
        {
            return AuthContext.Authenticated(tenantId, userId, false, false, "Bearer");
        }

        private static AuthContext TenantAdmin(string tenantId, string userId)
        {
            return AuthContext.Authenticated(tenantId, userId, false, true, "Bearer");
        }

        /// <summary>
        /// Create the tenants and users the cases own records with. Owned tables reference
        /// tenants and users, so a record cannot name an owner that does not exist.
        /// </summary>
        private static async Task SeedOwnersAsync(TestDatabase testDb)
        {
            string[][] owners = new[]
            {
                new[] { "ten_a", "usr_a1", "usr_a2" },
                new[] { "ten_b", "usr_b1", "usr_b2" },
                new[] { "ten_c", "usr_c" }
            };

            foreach (string[] owner in owners)
            {
                if (await testDb.Driver.Tenants.ReadAsync(owner[0]).ConfigureAwait(false) == null)
                {
                    TenantMetadata tenant = new TenantMetadata();
                    tenant.Id = owner[0];
                    tenant.Name = owner[0];
                    await testDb.Driver.Tenants.CreateAsync(tenant).ConfigureAwait(false);
                }

                for (int i = 1; i < owner.Length; i++)
                {
                    if (await testDb.Driver.Users.ReadByIdAsync(owner[i]).ConfigureAwait(false) != null) continue;
                    UserMaster user = new UserMaster();
                    user.Id = owner[i];
                    user.TenantId = owner[0];
                    user.Email = owner[i] + "@ownership.armada";
                    user.PasswordSha256 = UserMaster.ComputePasswordHash("ownership");
                    await testDb.Driver.Users.CreateAsync(user).ConfigureAwait(false);
                }
            }
        }

        private static async Task<Persona> CreatePersonaAsync(TestDatabase testDb, string name, string tenantId, string? userId, OwnershipScopeEnum scope, bool builtIn)
        {
            Persona persona = new Persona(name, "persona.worker");
            persona.TenantId = tenantId;
            persona.UserId = userId;
            persona.OwnershipScope = scope;
            persona.IsBuiltIn = builtIn;
            return await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);
        }

        private static Task<Persona?> ReadPersonaAsync(TestDatabase testDb, AuthContext auth, string name)
        {
            return OwnedRecordScope.ReadByNameAsync(
                auth,
                name,
                (tenantId, personaName) => testDb.Driver.Personas.ReadByNameAsync(tenantId, personaName),
                () => testDb.Driver.Personas.EnumerateAsync(),
                persona => persona.Name);
        }

        private static AdmiralService CreateAdmiralService(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            IMissionService missionService = new MissionService(logging, db, settings, dockService, captainService, null, git);
            IVoyageService voyageService = new VoyageService(logging, db);
            return new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService);
        }
    }
}
