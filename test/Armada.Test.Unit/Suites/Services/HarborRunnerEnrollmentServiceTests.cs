namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Tests durable Harbor enrollment ownership and revocation revalidation.</summary>
    public sealed class HarborRunnerEnrollmentServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Harbor Runner Enrollment Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateResolveRevokeAndReenroll_RevalidatesOwnerAndCredential", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_one");
                principals.Add("ten_one", "usr_two");
                credentials.Add("crd_one", "ten_one", "usr_one");
                credentials.Add("crd_two", "ten_one", "usr_two");
                HarborRunnerEnrollmentService service = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                AuthContext owner = Authenticated("ten_one", "usr_one", "crd_one");
                AuthContext administrator = Authenticated("ten_one", "admin", null, false, true);

                HarborRunnerEnrollment first = await service.CreateAsync("hbr_one", owner, administrator).ConfigureAwait(false);
                AssertEqual(1L, first.Generation, "initial generation");
                AssertTrue(service.TryGetOwner("hbr_one", out AuthContext? resolved), "active enrollment resolves");
                AssertEqual("ten_one", resolved!.TenantId, "resolved tenant");
                AssertEqual("usr_one", resolved.UserId, "resolved user");
                AssertFalse(service.TryGetOwner("hbr_unknown", out AuthContext? unknown), "unknown runner rejected");
                AssertNull(unknown, "unknown owner");
                await AssertThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
                    "hbr_one",
                    Authenticated("ten_one", "usr_two", "crd_two"),
                    administrator), "active owner cannot be substituted").ConfigureAwait(false);

                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.RevokeAsync("hbr_one", Authenticated("ten_other", "admin", null, false, true)), "cross-tenant revoke rejected").ConfigureAwait(false);

                AssertTrue(await service.RevokeAsync("hbr_one", administrator).ConfigureAwait(false), "original tenant administrator revokes before rebind");
                principals.Add("ten_two", "usr_three");
                credentials.Add("crd_three", "ten_two", "usr_three");
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(
                    "hbr_one",
                    Authenticated("ten_two", "usr_three", "crd_three"),
                    Authenticated("ten_two", "admin_two", null, false, true)),
                    "new tenant administrator cannot claim old tenant runner").ConfigureAwait(false);
                HarborRunnerEnrollment rebound = await service.CreateAsync(
                    "hbr_one",
                    Authenticated("ten_two", "usr_three", "crd_three"),
                    Authenticated("ten_two", "global_admin", null, true)).ConfigureAwait(false);
                AssertEqual(3L, rebound.Generation, "global administrator can authorize cross-tenant rebind");
            });

            await RunTest("RevokeAndCredentialUpdate_FailClosedWithoutCache", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_one");
                credentials.Add("crd_one", "ten_one", "usr_one");
                HarborRunnerEnrollmentService service = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                AuthContext owner = Authenticated("ten_one", "usr_one", "crd_one");
                AuthContext administrator = Authenticated("ten_one", "admin", null, false, true);

                HarborRunnerEnrollment first = await service.CreateAsync("hbr_one", owner, administrator).ConfigureAwait(false);
                long firstGeneration = first.Generation;
                credentials.SetActive("crd_one", false);
                AssertFalse(service.TryGetOwner("hbr_one", out AuthContext? revokedCredential), "inactive credential rejected");
                AssertNull(revokedCredential, "inactive credential owner");
                AssertEqual("credential_revoked_or_mismatched", service.LastResolutionFailure, "credential failure is named");
                credentials.SetActive("crd_one", true);
                principals.SetUserActive("ten_one", "usr_one", false);
                AssertFalse(service.TryGetOwner("hbr_one", out AuthContext? inactiveUser), "inactive user rejected");
                AssertNull(inactiveUser, "inactive user owner");
                AssertEqual("runner_principal_inactive", service.LastResolutionFailure, "principal failure is named");
                principals.SetUserActive("ten_one", "usr_one", true);
                AssertTrue(await service.RevokeAsync("hbr_one", administrator).ConfigureAwait(false), "administrator revokes active enrollment");
                AssertFalse(service.TryGetOwner("hbr_one", out AuthContext? revokedEnrollment), "revoked enrollment rejected");
                AssertNull(revokedEnrollment, "revoked enrollment owner");
                AssertEqual("runner_enrollment_revoked", service.LastResolutionFailure, "revocation failure is named");

                HarborRunnerEnrollment replacement = await service.CreateAsync("hbr_one", owner, administrator).ConfigureAwait(false);
                AssertEqual(firstGeneration + 2, replacement.Generation, "revocation and re-enrollment advance generation");
                AssertTrue(service.TryGetOwner("hbr_one", out AuthContext? current), "new generation resolves");
                AssertEqual("usr_one", current!.UserId, "new generation owner");
            });

            await RunTest("Create_RejectsUnverifiedOwnerAndNonAdministrator", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_one");
                HarborRunnerEnrollmentService service = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                AuthContext tenantUser = Authenticated("ten_one", "usr_one", null);
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("hbr_one", new AuthContext(), tenantUser), "unverified owner rejected").ConfigureAwait(false);
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("hbr_one", tenantUser, tenantUser), "non-administrator rejected").ConfigureAwait(false);
                AuthContext apiKeyWithoutCredential = AuthContext.Authenticated("ten_one", "usr_one", false, false, "ApiKey");
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("hbr_one", apiKeyWithoutCredential, Authenticated("ten_one", "admin", null, false, true)), "API key without credential rejected").ConfigureAwait(false);
            });

            await RunTest("RegistryRevalidatesPendingWorkAcrossRevokeAndReenroll", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_one");
                credentials.Add("crd_one", "ten_one", "usr_one");
                HarborRunnerEnrollmentService service = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                AuthContext owner = Authenticated("ten_one", "usr_one", "crd_one");
                AuthContext administrator = Authenticated("ten_one", "admin", null, false, true);
                await service.CreateAsync("hbr_one", owner, administrator).ConfigureAwait(false);
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, service);
                AssertTrue(registry.TryRegister("hbr_one", owner, out HarborRunnerSession? session, out string registerReason), registerReason);
                AssertTrue(registry.TryRegisterPending(session!, out HarborPendingRequest<string>? pending, out string pendingReason), pendingReason);
                AssertTrue(await service.RevokeAsync("hbr_one", administrator).ConfigureAwait(false), "revoke invalidates durable generation");
                AssertFalse(registry.TryCompletePending(session!, pending!.RequestId, "late"), "revoked session response rejected");
                AssertTrue(pending.Completion.IsCanceled, "revoked pending work is canceled");
                AssertFalse(registry.TryRegisterPending(session!, out HarborPendingRequest<string>? rejected, out string rejectedReason), "revoked session cannot create new work");
                AssertNull(rejected, "revoked session pending request");
                await service.CreateAsync("hbr_one", owner, administrator).ConfigureAwait(false);
                AssertTrue(registry.TryRegister("hbr_one", owner, out HarborRunnerSession? replacement, out string replacementReason), replacementReason);
                AssertTrue(registry.TryRegisterPending(replacement!, out HarborPendingRequest<string>? fresh, out string freshReason), freshReason);
                AssertTrue(registry.TryCompletePending(replacement!, fresh!.RequestId, "new generation"), "new generation response accepted");
            });

            await RunTest("RegistryAcceptsNewGenerationFromSeparateResolverInstance", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_one");
                credentials.Add("crd_one", "ten_one", "usr_one");
                HarborRunnerEnrollmentService firstService = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                HarborRunnerEnrollmentService secondService = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                AuthContext owner = Authenticated("ten_one", "usr_one", "crd_one");
                AuthContext administrator = Authenticated("ten_one", "admin", null, false, true);
                await firstService.CreateAsync("hbr_shared", owner, administrator).ConfigureAwait(false);
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, firstService);
                AssertTrue(registry.TryRegister("hbr_shared", owner, out HarborRunnerSession? oldSession, out string registerReason), registerReason);
                AssertTrue(registry.TryRegisterPending(oldSession!, out HarborPendingRequest<string>? pending, out string pendingReason), pendingReason);

                AssertTrue(await secondService.RevokeAsync("hbr_shared", administrator).ConfigureAwait(false), "second service revokes old generation");
                await secondService.CreateAsync("hbr_shared", owner, administrator).ConfigureAwait(false);
                AssertFalse(registry.TryCompletePending(oldSession!, pending!.RequestId, "old generation"), "old generation response is rejected after external change");
                AssertTrue(pending.Completion.IsCanceled, "external generation change cancels old pending work");
                AssertTrue(registry.TryRegister("hbr_shared", owner, out HarborRunnerSession? newSession, out string newReason), newReason);
                AssertTrue(newSession!.EnrollmentGeneration > oldSession!.EnrollmentGeneration, "registry accepts newer durable generation");
            });

            await RunTest("SqliteEnrollment_PersistsAcrossReopenAndCASRace", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborRunnerEnrollment enrollment = new HarborRunnerEnrollment
                    {
                        RunnerId = "hbr_persist",
                        TenantId = "ten_persist",
                        UserId = "usr_persist",
                        AuthMethod = "Bearer",
                        CredentialId = "crd_persist",
                        Generation = 1,
                        Active = true,
                        CreatedUtc = DateTime.UtcNow,
                        LastUpdateUtc = DateTime.UtcNow
                    };
                    AssertTrue(await testDb.Driver.HarborRunnerEnrollments.TryEnrollAsync(enrollment, 0).ConfigureAwait(false), "first durable insert");
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    using (SqliteDatabaseDriver reopened = new SqliteDatabaseDriver(testDb.ConnectionString, logging))
                    {
                        HarborRunnerEnrollment? persisted = await reopened.HarborRunnerEnrollments.ReadAsync("hbr_persist").ConfigureAwait(false);
                        AssertNotNull(persisted, "enrollment survives driver reopen");
                        AssertEqual(1L, persisted!.Generation, "reopened generation");
                    }

                    List<Task<bool>> attempts = new List<Task<bool>>();
                    for (int index = 0; index < 8; index++)
                        attempts.Add(testDb.Driver.HarborRunnerEnrollments.TryEnrollAsync(enrollment, 0));
                    bool[] results = await Task.WhenAll(attempts).ConfigureAwait(false);
                    int winners = 0;
                    foreach (bool result in results) if (result) winners++;
                    AssertEqual(0, winners, "CAS rejects duplicate active enrollment race");
                }
            });
        }

        private static AuthContext Authenticated(string tenantId, string userId, string? credentialId, bool isAdmin = false, bool isTenantAdmin = false)
        {
            return AuthContext.Authenticated(tenantId, userId, isAdmin, isTenantAdmin, credentialId == null ? "Session" : "Bearer", credentialId, userId);
        }

        private sealed class EnrollmentStore : IHarborRunnerEnrollmentMethods
        {
            private readonly Dictionary<string, HarborRunnerEnrollment> _Rows = new Dictionary<string, HarborRunnerEnrollment>(StringComparer.Ordinal);

            public Task<HarborRunnerEnrollment?> ReadAsync(string runnerId, CancellationToken token = default)
            {
                lock (_Rows)
                {
                    _Rows.TryGetValue(runnerId, out HarborRunnerEnrollment? value);
                    return Task.FromResult(value);
                }
            }

            public Task<bool> TryEnrollAsync(HarborRunnerEnrollment enrollment, long expectedGeneration, CancellationToken token = default)
            {
                lock (_Rows)
                {
                    if (_Rows.TryGetValue(enrollment.RunnerId, out HarborRunnerEnrollment? current))
                    {
                        if (current.Active || current.Generation != expectedGeneration) return Task.FromResult(false);
                    }
                    else if (expectedGeneration != 0) return Task.FromResult(false);
                    if (enrollment.Generation != expectedGeneration + 1 || enrollment.Generation <= 0)
                        return Task.FromResult(false);
                    _Rows[enrollment.RunnerId] = enrollment;
                    return Task.FromResult(true);
                }
            }

            public Task<bool> TryRevokeAsync(string runnerId, long expectedGeneration, string revokedByUserId, DateTime revokedUtc, CancellationToken token = default)
            {
                lock (_Rows)
                {
                    if (!_Rows.TryGetValue(runnerId, out HarborRunnerEnrollment? current) || !current.Active || current.Generation != expectedGeneration)
                        return Task.FromResult(false);
                    current.Active = false;
                    current.Generation++;
                    current.RevokedUtc = revokedUtc;
                    current.RevokedByUserId = revokedByUserId;
                    current.LastUpdateUtc = revokedUtc;
                    return Task.FromResult(true);
                }
            }
        }

        private sealed class CredentialStore : ICredentialMethods
        {
            private readonly Dictionary<string, Credential> _Rows = new Dictionary<string, Credential>(StringComparer.Ordinal);

            public void Add(string id, string tenantId, string userId)
            {
                _Rows[id] = new Credential { Id = id, TenantId = tenantId, UserId = userId };
            }

            public void SetActive(string id, bool active) { _Rows[id].Active = active; }
            public Task<Credential?> ReadByIdAsync(string id, CancellationToken token = default) => Task.FromResult(_Rows.TryGetValue(id, out Credential? credential) ? credential : null);
            public Task<Credential> CreateAsync(Credential credential, CancellationToken token = default) => throw new NotSupportedException();
            public Task<Credential?> ReadAsync(string tenantId, string id, CancellationToken token = default) => throw new NotSupportedException();
            public Task<Credential?> ReadByBearerTokenAsync(string bearerToken, CancellationToken token = default) => throw new NotSupportedException();
            public Task<Credential> UpdateAsync(Credential credential, CancellationToken token = default) => throw new NotSupportedException();
            public Task DeleteAsync(string tenantId, string id, CancellationToken token = default) => throw new NotSupportedException();
            public Task<List<Credential>> EnumerateAsync(string tenantId, CancellationToken token = default) => throw new NotSupportedException();
            public Task<EnumerationResult<Credential>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default) => throw new NotSupportedException();
            public Task<EnumerationResult<Credential>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default) => throw new NotSupportedException();
            public Task<List<Credential>> EnumerateByUserAsync(string tenantId, string userId, CancellationToken token = default) => throw new NotSupportedException();
            public Task<EnumerationResult<Credential>> EnumerateByUserAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default) => throw new NotSupportedException();
        }

        private sealed class PrincipalStore : IUserMethods, ITenantMethods
        {
            private readonly Dictionary<string, TenantMetadata> _Tenants = new Dictionary<string, TenantMetadata>(StringComparer.Ordinal);
            private readonly Dictionary<string, UserMaster> _Users = new Dictionary<string, UserMaster>(StringComparer.Ordinal);

            public void Add(string tenantId, string userId)
            {
                _Tenants[tenantId] = new TenantMetadata { Id = tenantId, Name = tenantId };
                _Users[tenantId + ":" + userId] = new UserMaster { Id = userId, TenantId = tenantId, Email = userId + "@example.invalid" };
            }

            public void SetUserActive(string tenantId, string userId, bool active)
            {
                _Users[tenantId + ":" + userId].Active = active;
            }

            public Task<TenantMetadata?> ReadAsync(string id, CancellationToken token = default) => Task.FromResult(_Tenants.TryGetValue(id, out TenantMetadata? tenant) ? tenant : null);
            public Task<UserMaster?> ReadAsync(string tenantId, string id, CancellationToken token = default) => Task.FromResult(_Users.TryGetValue(tenantId + ":" + id, out UserMaster? user) ? user : null);
            public Task<TenantMetadata> CreateAsync(TenantMetadata tenant, CancellationToken token = default) => throw new NotSupportedException();
            public Task<TenantMetadata?> ReadByNameAsync(string name, CancellationToken token = default) => throw new NotSupportedException();
            public Task<TenantMetadata> UpdateAsync(TenantMetadata tenant, CancellationToken token = default) => throw new NotSupportedException();
            public Task DeleteAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
            public Task<List<TenantMetadata>> EnumerateAsync(CancellationToken token = default) => throw new NotSupportedException();
            public Task<EnumerationResult<TenantMetadata>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default) => throw new NotSupportedException();
            public Task<bool> ExistsAsync(string id, CancellationToken token = default) => Task.FromResult(_Tenants.ContainsKey(id));
            public Task<bool> ExistsAnyAsync(CancellationToken token = default) => Task.FromResult(_Tenants.Count > 0);
            public Task<UserMaster> CreateAsync(UserMaster user, CancellationToken token = default) => throw new NotSupportedException();
            public Task<UserMaster?> ReadByIdAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
            public Task<UserMaster?> ReadByEmailAsync(string tenantId, string email, CancellationToken token = default) => throw new NotSupportedException();
            public Task<List<UserMaster>> ReadByEmailAnyTenantAsync(string email, CancellationToken token = default) => throw new NotSupportedException();
            public Task<UserMaster> UpdateAsync(UserMaster user, CancellationToken token = default) => throw new NotSupportedException();
            public Task DeleteAsync(string tenantId, string id, CancellationToken token = default) => throw new NotSupportedException();
            public Task<List<UserMaster>> EnumerateAsync(string tenantId, CancellationToken token = default) => throw new NotSupportedException();
            public Task<EnumerationResult<UserMaster>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default) => throw new NotSupportedException();
            Task<EnumerationResult<UserMaster>> IUserMethods.EnumerateAsync(EnumerationQuery query, CancellationToken token) => throw new NotSupportedException();
            public Task<bool> ExistsAsync(string tenantId, string id, CancellationToken token = default) => Task.FromResult(_Users.ContainsKey(tenantId + ":" + id));
        }
    }
}
