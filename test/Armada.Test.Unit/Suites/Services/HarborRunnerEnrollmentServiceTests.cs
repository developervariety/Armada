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
                HarborRunnerOwnerResolution resolvedResolution = await service.ResolveOwnerAsync("hbr_one").ConfigureAwait(false);
                AuthContext? resolved = resolvedResolution.Owner;
                AssertTrue(resolvedResolution.Resolved, "active enrollment resolves");
                AssertEqual("ten_one", resolved!.TenantId, "resolved tenant");
                AssertEqual("usr_one", resolved.UserId, "resolved user");
                HarborRunnerOwnerResolution unknownResolution = await service.ResolveOwnerAsync("hbr_unknown").ConfigureAwait(false);
                AuthContext? unknown = unknownResolution.Owner;
                AssertFalse(unknownResolution.Resolved, "unknown runner rejected");
                AssertNull(unknown, "unknown owner");
                await AssertThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
                    "hbr_one",
                    Authenticated("ten_one", "usr_two", "crd_two"),
                    administrator), "active owner cannot be substituted").ConfigureAwait(false);

                AssertFalse(await service.RevokeAsync("hbr_one", Authenticated("ten_other", "admin", null, false, true)).ConfigureAwait(false),
                    "a cross-tenant revoke reads exactly like revoking a runner that is not enrolled");
                AssertTrue((await service.ResolveOwnerAsync("hbr_one").ConfigureAwait(false)).Resolved, "the refused cross-tenant revoke changes nothing");

                AssertTrue(await service.RevokeAsync("hbr_one", administrator).ConfigureAwait(false), "original tenant administrator revokes before rebind");
                principals.Add("ten_two", "usr_three");
                credentials.Add("crd_three", "ten_two", "usr_three");
                string claimRefusal = await RefusalOfAsync(() => service.CreateAsync(
                    "hbr_one",
                    Authenticated("ten_two", "usr_three", "crd_three"),
                    Authenticated("ten_two", "admin_two", null, false, true))).ConfigureAwait(false);
                AssertEqual("InvalidOperationException:runner_already_enrolled", claimRefusal,
                    "another tenant's revoked runner reads only as taken, exactly like an active one");
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
                HarborRunnerOwnerResolution revokedCredentialResolution = await service.ResolveOwnerAsync("hbr_one").ConfigureAwait(false);
                AuthContext? revokedCredential = revokedCredentialResolution.Owner;
                AssertFalse(revokedCredentialResolution.Resolved, "inactive credential rejected");
                AssertNull(revokedCredential, "inactive credential owner");
                AssertEqual("credential_revoked_or_mismatched", service.LastResolutionFailure, "credential failure is named");
                credentials.SetActive("crd_one", true);
                principals.SetUserActive("ten_one", "usr_one", false);
                HarborRunnerOwnerResolution inactiveUserResolution = await service.ResolveOwnerAsync("hbr_one").ConfigureAwait(false);
                AuthContext? inactiveUser = inactiveUserResolution.Owner;
                AssertFalse(inactiveUserResolution.Resolved, "inactive user rejected");
                AssertNull(inactiveUser, "inactive user owner");
                AssertEqual("runner_principal_inactive", service.LastResolutionFailure, "principal failure is named");
                principals.SetUserActive("ten_one", "usr_one", true);
                AssertTrue(await service.RevokeAsync("hbr_one", administrator).ConfigureAwait(false), "administrator revokes active enrollment");
                HarborRunnerOwnerResolution revokedEnrollmentResolution = await service.ResolveOwnerAsync("hbr_one").ConfigureAwait(false);
                AuthContext? revokedEnrollment = revokedEnrollmentResolution.Owner;
                AssertFalse(revokedEnrollmentResolution.Resolved, "revoked enrollment rejected");
                AssertNull(revokedEnrollment, "revoked enrollment owner");
                AssertEqual("runner_enrollment_revoked", service.LastResolutionFailure, "revocation failure is named");

                HarborRunnerEnrollment replacement = await service.CreateAsync("hbr_one", owner, administrator).ConfigureAwait(false);
                AssertEqual(firstGeneration + 2, replacement.Generation, "revocation and re-enrollment advance generation");
                HarborRunnerOwnerResolution currentResolution = await service.ResolveOwnerAsync("hbr_one").ConfigureAwait(false);
                AuthContext? current = currentResolution.Owner;
                AssertTrue(currentResolution.Resolved, "new generation resolves");
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
                HarborRunnerRegistration sessionRegistration = await registry.RegisterAsync("hbr_one", owner).ConfigureAwait(false);
                HarborRunnerSession? session = sessionRegistration.Session;
                string registerReason = sessionRegistration.FailureReason;
                AssertTrue(sessionRegistration.Accepted, registerReason);
                HarborPendingRegistration<string> pendingRegistration = await registry.RegisterPendingAsync<string>(session!).ConfigureAwait(false);
                HarborPendingRequest<string>? pending = pendingRegistration.Pending;
                string pendingReason = pendingRegistration.FailureReason;
                AssertTrue(pendingRegistration.Accepted, pendingReason);
                AssertTrue(await service.RevokeAsync("hbr_one", administrator).ConfigureAwait(false), "revoke invalidates durable generation");
                AssertFalse(await registry.CompletePendingAsync(session!, pending!.RequestId, "late").ConfigureAwait(false), "revoked session response rejected");
                AssertTrue(pending.Completion.IsCanceled, "revoked pending work is canceled");
                HarborPendingRegistration<string> rejectedRegistration = await registry.RegisterPendingAsync<string>(session!).ConfigureAwait(false);
                HarborPendingRequest<string>? rejected = rejectedRegistration.Pending;
                string rejectedReason = rejectedRegistration.FailureReason;
                AssertFalse(rejectedRegistration.Accepted, "revoked session cannot create new work");
                AssertNull(rejected, "revoked session pending request");
                await service.CreateAsync("hbr_one", owner, administrator).ConfigureAwait(false);
                HarborRunnerRegistration replacementRegistration = await registry.RegisterAsync("hbr_one", owner).ConfigureAwait(false);
                HarborRunnerSession? replacement = replacementRegistration.Session;
                string replacementReason = replacementRegistration.FailureReason;
                AssertTrue(replacementRegistration.Accepted, replacementReason);
                HarborPendingRegistration<string> freshRegistration = await registry.RegisterPendingAsync<string>(replacement!).ConfigureAwait(false);
                HarborPendingRequest<string>? fresh = freshRegistration.Pending;
                string freshReason = freshRegistration.FailureReason;
                AssertTrue(freshRegistration.Accepted, freshReason);
                AssertTrue(await registry.CompletePendingAsync(replacement!, fresh!.RequestId, "new generation").ConfigureAwait(false), "new generation response accepted");
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
                HarborRunnerRegistration oldSessionRegistration = await registry.RegisterAsync("hbr_shared", owner).ConfigureAwait(false);
                HarborRunnerSession? oldSession = oldSessionRegistration.Session;
                string registerReason = oldSessionRegistration.FailureReason;
                AssertTrue(oldSessionRegistration.Accepted, registerReason);
                HarborPendingRegistration<string> pendingRegistration = await registry.RegisterPendingAsync<string>(oldSession!).ConfigureAwait(false);
                HarborPendingRequest<string>? pending = pendingRegistration.Pending;
                string pendingReason = pendingRegistration.FailureReason;
                AssertTrue(pendingRegistration.Accepted, pendingReason);

                AssertTrue(await secondService.RevokeAsync("hbr_shared", administrator).ConfigureAwait(false), "second service revokes old generation");
                await secondService.CreateAsync("hbr_shared", owner, administrator).ConfigureAwait(false);
                AssertFalse(await registry.CompletePendingAsync(oldSession!, pending!.RequestId, "old generation").ConfigureAwait(false), "old generation response is rejected after external change");
                AssertTrue(pending.Completion.IsCanceled, "external generation change cancels old pending work");
                HarborRunnerRegistration newSessionRegistration = await registry.RegisterAsync("hbr_shared", owner).ConfigureAwait(false);
                HarborRunnerSession? newSession = newSessionRegistration.Session;
                string newReason = newSessionRegistration.FailureReason;
                AssertTrue(newSessionRegistration.Accepted, newReason);
                AssertTrue(newSession!.EnrollmentGeneration > oldSession!.EnrollmentGeneration, "registry accepts newer durable generation");
            });

            await RunTest("ForeignTenantIds_ReadExactlyLikeMissingOnes", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_one");
                principals.Add("ten_two", "usr_two");
                credentials.Add("crd_one", "ten_one", "usr_one");
                credentials.Add("crd_two", "ten_two", "usr_two");
                HarborRunnerEnrollmentService service = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                AuthContext tenantOneAdministrator = Authenticated("ten_one", "admin_one", null, false, true);
                AuthContext tenantTwoAdministrator = Authenticated("ten_two", "admin_two", null, false, true);

                string missingCredential = await RefusalOfAsync(() => service.CreateForCredentialAsync("hbr_probe", "crd_missing", tenantTwoAdministrator)).ConfigureAwait(false);
                string foreignCredential = await RefusalOfAsync(() => service.CreateForCredentialAsync("hbr_probe", "crd_one", tenantTwoAdministrator)).ConfigureAwait(false);
                AssertEqual("UnauthorizedAccessException:credential_revoked_or_mismatched", missingCredential, "a missing credential is refused");
                AssertEqual(missingCredential, foreignCredential, "another tenant's credential is refused exactly like a missing one");

                await service.CreateForCredentialAsync("hbr_one", "crd_one", tenantOneAdministrator).ConfigureAwait(false);
                AssertFalse(await service.RevokeAsync("hbr_missing", tenantTwoAdministrator).ConfigureAwait(false), "a missing runner is not revoked");
                AssertFalse(await service.RevokeAsync("hbr_one", tenantTwoAdministrator).ConfigureAwait(false), "another tenant's runner reads like a missing one");
                AssertTrue((await service.ResolveOwnerAsync("hbr_one").ConfigureAwait(false)).Resolved, "the refused revoke changes nothing");

                HarborRunnerEnrollment own = await service.CreateForCredentialAsync("hbr_two", "crd_two", tenantTwoAdministrator).ConfigureAwait(false);
                AssertEqual("usr_two", own.UserId, "a tenant administrator still enrolls its own tenant's credential");
                AssertTrue(await service.RevokeAsync("hbr_one", Authenticated("ten_two", "global_admin", null, true)).ConfigureAwait(false),
                    "a global administrator still revokes any tenant's runner");
            });

            await RunTest("RevokedRunnerReuse_RequiresAuthorityOverPreviousOwner", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_global");
                principals.SetUserAdmin("ten_one", "usr_global", true);
                principals.Add("ten_one", "usr_member");
                credentials.Add("crd_global", "ten_one", "usr_global");
                credentials.Add("crd_member", "ten_one", "usr_member");
                HarborRunnerEnrollmentService service = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                AuthContext globalAdministrator = Authenticated("ten_one", "global_admin", null, true);
                AuthContext tenantAdministrator = Authenticated("ten_one", "tenant_admin", null, false, true);
                AuthContext globalOwner = Authenticated("ten_one", "usr_global", "crd_global");
                AuthContext memberOwner = Authenticated("ten_one", "usr_member", "crd_member");

                await service.CreateAsync("hbr_global", globalOwner, globalAdministrator).ConfigureAwait(false);
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.RevokeAsync("hbr_global", tenantAdministrator),
                    "tenant administrator cannot revoke a global administrator's runner").ConfigureAwait(false);
                AssertTrue(await service.RevokeAsync("hbr_global", globalAdministrator).ConfigureAwait(false), "global administrator revokes");
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("hbr_global", memberOwner, tenantAdministrator),
                    "tenant administrator cannot reuse a runner revoked from a global administrator").ConfigureAwait(false);
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("hbr_new", globalOwner, tenantAdministrator),
                    "tenant administrator cannot bind a runner to a global administrator").ConfigureAwait(false);

                await service.CreateAsync("hbr_member", memberOwner, tenantAdministrator).ConfigureAwait(false);
                AssertTrue(await service.RevokeAsync("hbr_member", tenantAdministrator).ConfigureAwait(false), "tenant administrator revokes a member runner");
                HarborRunnerEnrollment reused = await service.CreateAsync("hbr_member", memberOwner, tenantAdministrator).ConfigureAwait(false);
                AssertEqual(3L, reused.Generation, "tenant administrator reuses a runner previously owned by a tenant member");
            });

            await RunTest("RegistryAcceptsExternallyReboundOwnerWhileStaleSessionConnected", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_one");
                principals.Add("ten_one", "usr_two");
                credentials.Add("crd_one", "ten_one", "usr_one");
                credentials.Add("crd_two", "ten_one", "usr_two");
                HarborRunnerEnrollmentService firstService = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                HarborRunnerEnrollmentService secondService = new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals);
                AuthContext oldOwner = Authenticated("ten_one", "usr_one", "crd_one");
                AuthContext newOwner = Authenticated("ten_one", "usr_two", "crd_two");
                AuthContext administrator = Authenticated("ten_one", "admin", null, false, true);
                await firstService.CreateAsync("hbr_moved", oldOwner, administrator).ConfigureAwait(false);
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, firstService);
                HarborRunnerRegistration oldSessionRegistration = await registry.RegisterAsync("hbr_moved", oldOwner).ConfigureAwait(false);
                HarborRunnerSession? oldSession = oldSessionRegistration.Session;
                string oldReason = oldSessionRegistration.FailureReason;
                AssertTrue(oldSessionRegistration.Accepted, oldReason);
                HarborPendingRegistration<string> oldPendingRegistration = await registry.RegisterPendingAsync<string>(oldSession!).ConfigureAwait(false);
                HarborPendingRequest<string>? oldPending = oldPendingRegistration.Pending;
                string pendingReason = oldPendingRegistration.FailureReason;
                AssertTrue(oldPendingRegistration.Accepted, pendingReason);

                AssertTrue(await secondService.RevokeAsync("hbr_moved", administrator).ConfigureAwait(false), "second instance revokes");
                await secondService.CreateAsync("hbr_moved", newOwner, administrator).ConfigureAwait(false);

                HarborRunnerRegistration newSessionRegistration = await registry.RegisterAsync("hbr_moved", newOwner).ConfigureAwait(false);

                HarborRunnerSession? newSession = newSessionRegistration.Session;

                string newReason = newSessionRegistration.FailureReason;

                AssertTrue(newSessionRegistration.Accepted, "new owner accepted after external rebind: " + newReason);
                AssertTrue(oldPending!.Completion.IsCanceled, "stale owner pending work is canceled");
                HarborPendingRegistration<string> staleRegistration = await registry.RegisterPendingAsync<string>(oldSession!).ConfigureAwait(false);
                HarborPendingRequest<string>? stale = staleRegistration.Pending;
                AssertFalse(staleRegistration.Accepted, "stale owner cannot create work");
                AssertNull(stale, "stale owner pending request");
                AssertFalse(registry.TryDisconnect(oldSession!), "stale owner cannot disconnect the new owner");
                AssertTrue(registry.IsCurrent(newSession!), "new owner remains current");
            });

            await RunTest("RevalidateEvictsConnectedSessionAfterExternalRevocation", async () =>
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
                await firstService.CreateAsync("hbr_live", owner, administrator).ConfigureAwait(false);
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, firstService);
                HarborRunnerRegistration sessionRegistration = await registry.RegisterAsync("hbr_live", owner).ConfigureAwait(false);
                HarborRunnerSession? session = sessionRegistration.Session;
                string registerReason = sessionRegistration.FailureReason;
                AssertTrue(sessionRegistration.Accepted, registerReason);
                HarborPendingRegistration<string> pendingRegistration = await registry.RegisterPendingAsync<string>(session!).ConfigureAwait(false);
                HarborPendingRequest<string>? pending = pendingRegistration.Pending;
                string pendingReason = pendingRegistration.FailureReason;
                AssertTrue(pendingRegistration.Accepted, pendingReason);
                HarborRunnerCheck liveReasonCheck = await registry.RevalidateAsync(session!).ConfigureAwait(false);
                string liveReason = liveReasonCheck.FailureReason;
                AssertTrue(liveReasonCheck.Accepted, "active session revalidates: " + liveReason);

                AssertTrue(await secondService.RevokeAsync("hbr_live", administrator).ConfigureAwait(false), "second instance revokes");
                HarborRunnerCheck revokedReasonCheck = await registry.RevalidateAsync(session!).ConfigureAwait(false);
                string revokedReason = revokedReasonCheck.FailureReason;
                AssertFalse(revokedReasonCheck.Accepted, "revoked session fails revalidation");
                AssertFalse(String.IsNullOrEmpty(revokedReason), "revocation failure is named");
                AssertFalse(registry.IsCurrent(session!), "revoked session is removed from the registry");
                AssertTrue(pending!.Completion.IsCanceled, "revoked session pending work is canceled");
                AssertEqual(0, registry.SessionCount, "no live session remains");

                HarborRunnerEnrollment reenrolled = await secondService.CreateAsync("hbr_live", owner, administrator).ConfigureAwait(false);
                HarborRunnerCheck revalidation1Check = await registry.RevalidateAsync(session!).ConfigureAwait(false);
                string revalidation1 = revalidation1Check.FailureReason;
                AssertFalse(revalidation1Check.Accepted, "re-enrollment does not revive the old session");
                HarborPendingRegistration<string> revivedRegistration = await registry.RegisterPendingAsync<string>(session!).ConfigureAwait(false);
                HarborPendingRequest<string>? revived = revivedRegistration.Pending;
                AssertFalse(revivedRegistration.Accepted, "old session cannot create work after re-enrollment");
                AssertNull(revived, "old session pending request");
                HarborRunnerRegistration freshRegistration = await registry.RegisterAsync("hbr_live", owner).ConfigureAwait(false);
                HarborRunnerSession? fresh = freshRegistration.Session;
                string freshReason = freshRegistration.FailureReason;
                AssertTrue(freshRegistration.Accepted, freshReason);
                AssertEqual(reenrolled.Generation, fresh!.EnrollmentGeneration, "new session binds the re-enrolled generation");
            });

            await RunTest("SlowOwnerLookup_WithinBound_AcceptsRunnerWithoutBlockingCaller", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_one");
                credentials.Add("crd_one", "ten_one", "usr_one");
                HarborRunnerEnrollmentService service = new HarborRunnerEnrollmentService(
                    enrollments, credentials, principals, principals, TimeSpan.FromSeconds(10));
                AuthContext owner = Authenticated("ten_one", "usr_one", "crd_one");
                await service.CreateAsync("hbr_slow", owner, Authenticated("ten_one", "admin", null, false, true)).ConfigureAwait(false);
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, service);
                enrollments.ReadDelay = TimeSpan.FromMilliseconds(2500);

                System.Diagnostics.Stopwatch blocked = System.Diagnostics.Stopwatch.StartNew();
                Task<HarborRunnerRegistration> pending = registry.RegisterAsync("hbr_slow", owner);
                blocked.Stop();
                AssertFalse(pending.IsCompleted, "the registration is still waiting on the slow lookup when the call returns");
                AssertTrue(blocked.ElapsedMilliseconds < 1000, "the caller thread is not blocked by the lookup; blocked " + blocked.ElapsedMilliseconds + " ms");
                HarborRunnerRegistration registration = await pending.ConfigureAwait(false);
                AssertTrue(registration.Accepted, "a valid runner whose owner lookup completes within the bound is accepted: " + registration.FailureReason);
            });

            await RunTest("SlowOwnerLookup_BeyondBound_IsRefusedAsLookupTimeout", async () =>
            {
                EnrollmentStore enrollments = new EnrollmentStore();
                CredentialStore credentials = new CredentialStore();
                PrincipalStore principals = new PrincipalStore();
                principals.Add("ten_one", "usr_one");
                credentials.Add("crd_one", "ten_one", "usr_one");
                HarborRunnerEnrollmentService service = new HarborRunnerEnrollmentService(
                    enrollments, credentials, principals, principals, TimeSpan.FromMilliseconds(100));
                AssertEqual(TimeSpan.FromMilliseconds(100), service.OwnerLookupTimeout, "the configured bound is the one in force");
                AuthContext owner = Authenticated("ten_one", "usr_one", "crd_one");
                await service.CreateAsync("hbr_slow", owner, Authenticated("ten_one", "admin", null, false, true)).ConfigureAwait(false);
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, service);
                enrollments.ReadDelay = TimeSpan.FromSeconds(5);

                System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                HarborRunnerRegistration registration = await registry.RegisterAsync("hbr_slow", owner).ConfigureAwait(false);
                elapsed.Stop();
                AssertFalse(registration.Accepted, "a lookup beyond the bound refuses the runner");
                AssertEqual(HarborRunnerEnrollmentService.OwnerLookupTimeoutReason, registration.FailureReason, "the refusal names the lookup timeout");
                AssertTrue(elapsed.ElapsedMilliseconds < 3000, "the refusal arrives at the bound, not when the slow read ends; took " + elapsed.ElapsedMilliseconds + " ms");
                AssertEqual(0, registry.SessionCount, "no session is registered");
                AssertEqual(TimeSpan.FromSeconds(5), new HarborRunnerEnrollmentService(enrollments, credentials, principals, principals).OwnerLookupTimeout,
                    "the default bound is five seconds");
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

        private static async Task<string> RefusalOfAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return "accepted";
            }
            catch (Exception exception)
            {
                return exception.GetType().Name + ":" + exception.Message;
            }
        }

        private static AuthContext Authenticated(string tenantId, string userId, string? credentialId, bool isAdmin = false, bool isTenantAdmin = false)
        {
            return AuthContext.Authenticated(tenantId, userId, isAdmin, isTenantAdmin, credentialId == null ? "Session" : "Bearer", credentialId, userId);
        }

        private sealed class EnrollmentStore : IHarborRunnerEnrollmentMethods
        {
            private readonly Dictionary<string, HarborRunnerEnrollment> _Rows = new Dictionary<string, HarborRunnerEnrollment>(StringComparer.Ordinal);

            /// <summary>How long each read waits before it answers, as a slow database would.</summary>
            public TimeSpan ReadDelay { get; set; } = TimeSpan.Zero;

            public async Task<HarborRunnerEnrollment?> ReadAsync(string runnerId, CancellationToken token = default)
            {
                if (ReadDelay > TimeSpan.Zero) await Task.Delay(ReadDelay, token).ConfigureAwait(false);
                lock (_Rows)
                {
                    _Rows.TryGetValue(runnerId, out HarborRunnerEnrollment? value);
                    return value;
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

            public void SetUserAdmin(string tenantId, string userId, bool isAdmin)
            {
                _Users[tenantId + ":" + userId].IsAdmin = isAdmin;
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
            public Task<UserMaster?> ReadByIdAsync(string id, CancellationToken token = default)
            {
                foreach (UserMaster user in _Users.Values)
                    if (String.Equals(user.Id, id, StringComparison.Ordinal)) return Task.FromResult<UserMaster?>(user);
                return Task.FromResult<UserMaster?>(null);
            }
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
