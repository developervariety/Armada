namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests Harbor runner identity, connection generations, and pending response ownership.
    /// </summary>
    public sealed class HarborRunnerSessionRegistryTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Harbor Runner Session Registry";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Registration_DefaultDisabledAndRejectsUnverifiedIdentity", async () =>
            {
                HarborRunnerSessionRegistry disabled = new HarborRunnerSessionRegistry();
                HarborRunnerRegistration disabledSessionRegistration = await disabled.RegisterAsync("hbr_disabled", VerifiedAuth("ten_one", "usr_one", "cred_one")).ConfigureAwait(false);
                bool disabledAccepted = disabledSessionRegistration.Accepted;
                HarborRunnerSession? disabledSession = disabledSessionRegistration.Session;
                string disabledReason = disabledSessionRegistration.FailureReason;
                AssertFalse(disabledAccepted, "default registration disabled");
                AssertNull(disabledSession, "disabled session");
                AssertEqual("runner_registration_disabled", disabledReason, "disabled reason");

                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                HarborRunnerRegistration unauthenticatedSessionRegistration = await registry.RegisterAsync("hbr_unverified", new AuthContext()).ConfigureAwait(false);
                bool unauthenticatedAccepted = unauthenticatedSessionRegistration.Accepted;
                HarborRunnerSession? unauthenticatedSession = unauthenticatedSessionRegistration.Session;
                string unauthenticatedReason = unauthenticatedSessionRegistration.FailureReason;
                AssertFalse(unauthenticatedAccepted, "unverified identity rejected");
                AssertNull(unauthenticatedSession, "unverified session");
                AssertEqual("runner_identity_unverified", unauthenticatedReason, "unverified reason");

                HarborRunnerRegistration nullAuthSessionRegistration = await registry.RegisterAsync("hbr_null_auth", null!).ConfigureAwait(false);

                bool nullAuthAccepted = nullAuthSessionRegistration.Accepted;

                HarborRunnerSession? nullAuthSession = nullAuthSessionRegistration.Session;

                string nullAuthReason = nullAuthSessionRegistration.FailureReason;
                AssertFalse(nullAuthAccepted, "null identity rejected");
                AssertNull(nullAuthSession, "null auth session");
                AssertEqual("runner_identity_unverified", nullAuthReason, "null auth reason");
            });

            await RunTest("Registration_TwoRunnersAndPrincipalConflict", async () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                HarborRunnerRegistration firstRegistration = await registry.RegisterAsync("hbr_one", VerifiedAuth("ten_one", "usr_one", "cred_one")).ConfigureAwait(false);
                HarborRunnerSession? first = firstRegistration.Session;
                string firstReason = firstRegistration.FailureReason;
                AssertTrue(firstRegistration.Accepted, firstReason);
                HarborRunnerRegistration secondRegistration = await registry.RegisterAsync("hbr_two", VerifiedAuth("ten_two", "usr_two", "cred_two")).ConfigureAwait(false);
                HarborRunnerSession? second = secondRegistration.Session;
                string secondReason = secondRegistration.FailureReason;
                AssertTrue(secondRegistration.Accepted, secondReason);
                AssertNotNull(first, "first runner");
                AssertNotNull(second, "second runner");
                AssertFalse(first!.Generation == second!.Generation, "independent generations");
                HarborRunnerRegistration conflictSessionRegistration = await registry.RegisterAsync("hbr_one", VerifiedAuth("ten_other", "usr_other", "cred_other")).ConfigureAwait(false);
                bool conflict = conflictSessionRegistration.Accepted;
                HarborRunnerSession? conflictSession = conflictSessionRegistration.Session;
                string conflictReason = conflictSessionRegistration.FailureReason;
                AssertFalse(conflict, "principal change rejected");
                AssertNull(conflictSession, "conflict session");
                AssertEqual("runner_owner_mismatch", conflictReason, "conflict reason");
                AssertEqual(2, registry.SessionCount, "two current runners");
            });

            await RunTest("Registration_AuthoritativeOwnerSurvivesDisconnect", async () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                HarborRunnerRegistration sessionRegistration = await registry.RegisterAsync("hbr_one", VerifiedAuth("ten_one", "usr_one", "cred_one")).ConfigureAwait(false);
                HarborRunnerSession? session = sessionRegistration.Session;
                string reason = sessionRegistration.FailureReason;
                AssertTrue(sessionRegistration.Accepted, reason);
                AssertTrue(registry.TryDisconnect(session!), "owner session disconnects");
                HarborRunnerRegistration replacementRegistration = await registry.RegisterAsync("hbr_one", VerifiedAuth("ten_other", "usr_other", "cred_other")).ConfigureAwait(false);
                HarborRunnerSession? replacement = replacementRegistration.Session;
                string mismatchReason = replacementRegistration.FailureReason;
                AssertFalse(replacementRegistration.Accepted, "different principal cannot claim runner");
                AssertNull(replacement, "different principal session");
                AssertEqual("runner_owner_mismatch", mismatchReason, "different principal owner reason");
                HarborRunnerRegistration unknownRegistration = await registry.RegisterAsync("hbr_unknown", VerifiedAuth("ten_one", "usr_one", "cred_one")).ConfigureAwait(false);
                HarborRunnerSession? unknown = unknownRegistration.Session;
                string unknownReason = unknownRegistration.FailureReason;
                AssertFalse(unknownRegistration.Accepted, "unknown runner denied");
                AssertNull(unknown, "unknown runner session");
                AssertEqual("runner_owner_unknown", unknownReason, "unknown runner reason");
            });

            await RunTest("Reconnect_StaleDisconnectCannotRemoveReplacement", async () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                AuthContext auth = VerifiedAuth("ten_one", "usr_one", "cred_one");
                HarborRunnerRegistration originalRegistration = await registry.RegisterAsync("hbr_one", auth).ConfigureAwait(false);
                HarborRunnerSession? original = originalRegistration.Session;
                string originalReason = originalRegistration.FailureReason;
                AssertTrue(originalRegistration.Accepted, originalReason);
                HarborRunnerRegistration replacementRegistration = await registry.RegisterAsync("hbr_one", auth).ConfigureAwait(false);
                HarborRunnerSession? replacement = replacementRegistration.Session;
                string replacementReason = replacementRegistration.FailureReason;
                AssertTrue(replacementRegistration.Accepted, replacementReason);
                AssertNotNull(original, "original session");
                AssertNotNull(replacement, "replacement session");
                AssertFalse(original!.Generation == replacement!.Generation, "reconnect generation increments");
                AssertFalse(registry.TryDisconnect(original), "stale disconnect denied");
                AssertTrue(registry.IsCurrent(replacement), "replacement remains current");
                AssertTrue(registry.TryDisconnect(replacement), "current disconnect accepted");
                AssertEqual(0, registry.SessionCount, "replacement disconnected");
            });

            await RunTest("PendingResponse_RequiresRunnerAndGenerationAndRejectsReplay", async () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                HarborRunnerRegistration oneRegistration = await registry.RegisterAsync("hbr_one", VerifiedAuth("ten_one", "usr_one", "cred_one")).ConfigureAwait(false);
                HarborRunnerSession? one = oneRegistration.Session;
                string oneReason = oneRegistration.FailureReason;
                AssertTrue(oneRegistration.Accepted, oneReason);
                HarborRunnerRegistration twoRegistration = await registry.RegisterAsync("hbr_two", VerifiedAuth("ten_two", "usr_two", "cred_two")).ConfigureAwait(false);
                HarborRunnerSession? two = twoRegistration.Session;
                string twoReason = twoRegistration.FailureReason;
                AssertTrue(twoRegistration.Accepted, twoReason);
                HarborPendingRegistration<string> pendingRegistration = await registry.RegisterPendingAsync<string>(one!).ConfigureAwait(false);
                HarborPendingRequest<string>? pending = pendingRegistration.Pending;
                string pendingReason = pendingRegistration.FailureReason;
                AssertTrue(pendingRegistration.Accepted, pendingReason);
                HarborPendingRegistration<string> duplicateRegistration = await registry.RegisterPendingAsync<string>(one!).ConfigureAwait(false);
                HarborPendingRequest<string>? duplicate = duplicateRegistration.Pending;
                string duplicateReason = duplicateRegistration.FailureReason;
                AssertTrue(duplicateRegistration.Accepted, duplicateReason);
                AssertFalse(String.Equals(pending!.RequestId, duplicate!.RequestId, StringComparison.Ordinal), "server request ids are unique");
                AssertFalse(await registry.CompletePendingAsync(two!, pending.RequestId, "wrong runner").ConfigureAwait(false), "wrong runner response rejected");
                AssertFalse(await registry.CompletePendingAsync(one!, pending.RequestId, 42).ConfigureAwait(false), "wrong response type rejected");
                AssertEqual(2, registry.PendingCount, "wrong response type does not consume request");
                AssertTrue(await registry.CompletePendingAsync(one!, pending.RequestId, "accepted").ConfigureAwait(false), "matching response accepted");
                AssertEqual("accepted", pending!.Completion.Result, "typed response");
                AssertFalse(await registry.CompletePendingAsync(one!, pending.RequestId, "replayed").ConfigureAwait(false), "duplicate response rejected");
                AssertTrue(await registry.CompletePendingAsync(one!, duplicate.RequestId, "second").ConfigureAwait(false), "second unique request accepted");
                AssertEqual(0, registry.PendingCount, "pending response removed");
            });

            await RunTest("Reconnect_CancelsOldPendingAndRejectsStaleResponse", async () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                AuthContext auth = VerifiedAuth("ten_one", "usr_one", "cred_one");
                HarborRunnerRegistration originalRegistration = await registry.RegisterAsync("hbr_one", auth).ConfigureAwait(false);
                HarborRunnerSession? original = originalRegistration.Session;
                string originalReason = originalRegistration.FailureReason;
                AssertTrue(originalRegistration.Accepted, originalReason);
                HarborPendingRegistration<int> pendingRegistration = await registry.RegisterPendingAsync<int>(original!).ConfigureAwait(false);
                HarborPendingRequest<int>? pending = pendingRegistration.Pending;
                string pendingReason = pendingRegistration.FailureReason;
                AssertTrue(pendingRegistration.Accepted, pendingReason);
                HarborRunnerRegistration replacementRegistration = await registry.RegisterAsync("hbr_one", auth).ConfigureAwait(false);
                HarborRunnerSession? replacement = replacementRegistration.Session;
                string replacementReason = replacementRegistration.FailureReason;
                AssertTrue(replacementRegistration.Accepted, replacementReason);
                AssertTrue(pending!.Completion.IsCanceled, "old pending canceled on reconnect");
                AssertFalse(await registry.CompletePendingAsync(original!, pending.RequestId, 1).ConfigureAwait(false), "stale response rejected");
                HarborPendingRegistration<int> freshRegistration = await registry.RegisterPendingAsync<int>(replacement!).ConfigureAwait(false);
                HarborPendingRequest<int>? fresh = freshRegistration.Pending;
                string freshReason = freshRegistration.FailureReason;
                AssertTrue(freshRegistration.Accepted, freshReason);
                AssertFalse(String.Equals(pending.RequestId, fresh!.RequestId, StringComparison.Ordinal), "reconnect request id changes");
                AssertTrue(await registry.CompletePendingAsync(replacement!, fresh.RequestId, 7).ConfigureAwait(false), "replacement response accepted");
                AssertEqual(7, fresh!.Completion.Result, "replacement response value");
            });

            await RunTest("DurableOwnerResolution_RunsOutsideRegistryLock", async () =>
            {
                LockProbeResolver resolver = new LockProbeResolver();
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, resolver);
                resolver.Registry = registry;
                AuthContext auth = VerifiedAuth("ten_one", "usr_one", "cred_one");
                HarborRunnerRegistration sessionRegistration = await registry.RegisterAsync("hbr_one", auth).ConfigureAwait(false);
                HarborRunnerSession? session = sessionRegistration.Session;
                string registerReason = sessionRegistration.FailureReason;
                AssertTrue(sessionRegistration.Accepted, registerReason);
                HarborPendingRegistration<int> pendingRegistration = await registry.RegisterPendingAsync<int>(session!).ConfigureAwait(false);
                HarborPendingRequest<int>? pending = pendingRegistration.Pending;
                string pendingReason = pendingRegistration.FailureReason;
                AssertTrue(pendingRegistration.Accepted, pendingReason);
                AssertTrue(await registry.CompletePendingAsync(session!, pending!.RequestId, 1).ConfigureAwait(false), "response accepted while resolution probes the lock");
                AssertTrue(resolver.Calls >= 3, "registration, request and response each resolve the durable owner");
                AssertEqual(0, resolver.LockHeldObservations, "no durable resolution ran while the registry lock was held");
            });

            await RunTest("PendingResponse_ServerIssuedIdsCannotBeReusedAfterReplayCacheEviction", async () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                HarborRunnerRegistration sessionRegistration = await registry.RegisterAsync("hbr_one", VerifiedAuth("ten_one", "usr_one", "cred_one")).ConfigureAwait(false);
                HarborRunnerSession? session = sessionRegistration.Session;
                string registrationReason = sessionRegistration.FailureReason;
                AssertTrue(sessionRegistration.Accepted, registrationReason);
                string firstRequestId = String.Empty;
                for (int index = 0; index < 4100; index++)
                {
                    HarborPendingRegistration<int> pendingRegistration = await registry.RegisterPendingAsync<int>(session!).ConfigureAwait(false);
                    HarborPendingRequest<int>? pending = pendingRegistration.Pending;
                    string pendingReason = pendingRegistration.FailureReason;
                    AssertTrue(pendingRegistration.Accepted, pendingReason);
                    if (index == 0) firstRequestId = pending!.RequestId;
                    AssertTrue(await registry.CompletePendingAsync(session!, pending!.RequestId, index).ConfigureAwait(false), "server-issued request completes");
                }

                AssertFalse(await registry.CompletePendingAsync(session!, firstRequestId, 9999).ConfigureAwait(false), "late response remains rejected after bounded cache eviction");
                HarborPendingRegistration<int> freshRegistration = await registry.RegisterPendingAsync<int>(session!).ConfigureAwait(false);
                HarborPendingRequest<int>? fresh = freshRegistration.Pending;
                string freshReason = freshRegistration.FailureReason;
                AssertTrue(freshRegistration.Accepted, freshReason);
                AssertFalse(String.Equals(firstRequestId, fresh!.RequestId, StringComparison.Ordinal), "server-issued request id is never reused");
            });
        }

        private static AuthContext VerifiedAuth(string tenantId, string userId, string credentialId)
        {
            return AuthContext.Authenticated(tenantId, userId, false, false, "Bearer", credentialId, userId);
        }

        private sealed class LockProbeResolver : IHarborRunnerOwnerResolver
        {
            private int _Calls;
            private int _LockHeldObservations;

            public HarborRunnerSessionRegistry? Registry { get; set; }

            public int Calls => Volatile.Read(ref _Calls);

            public int LockHeldObservations => Volatile.Read(ref _LockHeldObservations);

            public async Task<HarborRunnerOwnerResolution> ResolveOwnerAsync(string runnerId, CancellationToken token = default)
            {
                Interlocked.Increment(ref _Calls);
                HarborRunnerSessionRegistry? registry = Registry;
                if (registry != null)
                {
                    Task<int> probe = Task.Run(() => registry.SessionCount);
                    if (await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != probe)
                        Interlocked.Increment(ref _LockHeldObservations);
                }
                return String.Equals(runnerId, "hbr_one", StringComparison.Ordinal)
                    ? HarborRunnerOwnerResolution.Success(VerifiedAuth("ten_one", "usr_one", "cred_one"), 0)
                    : HarborRunnerOwnerResolution.Failure("runner_owner_unknown");
            }
        }

        private sealed class TestRunnerOwnerResolver : IHarborRunnerOwnerResolver
        {
            private readonly Dictionary<string, AuthContext> _Owners = new Dictionary<string, AuthContext>(StringComparer.Ordinal)
            {
                ["hbr_one"] = VerifiedAuth("ten_one", "usr_one", "cred_one"),
                ["hbr_two"] = VerifiedAuth("ten_two", "usr_two", "cred_two")
            };

            public Task<HarborRunnerOwnerResolution> ResolveOwnerAsync(string runnerId, CancellationToken token = default)
            {
                return Task.FromResult(_Owners.TryGetValue(runnerId, out AuthContext? owner)
                    ? HarborRunnerOwnerResolution.Success(owner, 0)
                    : HarborRunnerOwnerResolution.Failure("runner_owner_unknown"));
            }
        }
    }
}
