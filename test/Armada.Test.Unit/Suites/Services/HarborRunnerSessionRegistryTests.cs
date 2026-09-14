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
            await RunTest("Registration_DefaultDisabledAndRejectsUnverifiedIdentity", () =>
            {
                HarborRunnerSessionRegistry disabled = new HarborRunnerSessionRegistry();
                bool disabledAccepted = disabled.TryRegister(
                    "hbr_disabled",
                    VerifiedAuth("ten_one", "usr_one", "cred_one"),
                    out HarborRunnerSession? disabledSession,
                    out string disabledReason);
                AssertFalse(disabledAccepted, "default registration disabled");
                AssertNull(disabledSession, "disabled session");
                AssertEqual("runner_registration_disabled", disabledReason, "disabled reason");

                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                bool unauthenticatedAccepted = registry.TryRegister(
                    "hbr_unverified",
                    new AuthContext(),
                    out HarborRunnerSession? unauthenticatedSession,
                    out string unauthenticatedReason);
                AssertFalse(unauthenticatedAccepted, "unverified identity rejected");
                AssertNull(unauthenticatedSession, "unverified session");
                AssertEqual("runner_identity_unverified", unauthenticatedReason, "unverified reason");

                bool nullAuthAccepted = registry.TryRegister(
                    "hbr_null_auth",
                    null!,
                    out HarborRunnerSession? nullAuthSession,
                    out string nullAuthReason);
                AssertFalse(nullAuthAccepted, "null identity rejected");
                AssertNull(nullAuthSession, "null auth session");
                AssertEqual("runner_identity_unverified", nullAuthReason, "null auth reason");
            });

            await RunTest("Registration_TwoRunnersAndPrincipalConflict", () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                AssertTrue(registry.TryRegister("hbr_one", VerifiedAuth("ten_one", "usr_one", "cred_one"), out HarborRunnerSession? first, out string firstReason), firstReason);
                AssertTrue(registry.TryRegister("hbr_two", VerifiedAuth("ten_two", "usr_two", "cred_two"), out HarborRunnerSession? second, out string secondReason), secondReason);
                AssertNotNull(first, "first runner");
                AssertNotNull(second, "second runner");
                AssertFalse(first!.Generation == second!.Generation, "independent generations");
                bool conflict = registry.TryRegister("hbr_one", VerifiedAuth("ten_other", "usr_other", "cred_other"), out HarborRunnerSession? conflictSession, out string conflictReason);
                AssertFalse(conflict, "principal change rejected");
                AssertNull(conflictSession, "conflict session");
                AssertEqual("runner_owner_mismatch", conflictReason, "conflict reason");
                AssertEqual(2, registry.SessionCount, "two current runners");
            });

            await RunTest("Registration_AuthoritativeOwnerSurvivesDisconnect", () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                AssertTrue(registry.TryRegister("hbr_one", VerifiedAuth("ten_one", "usr_one", "cred_one"), out HarborRunnerSession? session, out string reason), reason);
                AssertTrue(registry.TryDisconnect(session!), "owner session disconnects");
                AssertFalse(registry.TryRegister("hbr_one", VerifiedAuth("ten_other", "usr_other", "cred_other"), out HarborRunnerSession? replacement, out string mismatchReason), "different principal cannot claim runner");
                AssertNull(replacement, "different principal session");
                AssertEqual("runner_owner_mismatch", mismatchReason, "different principal owner reason");
                AssertFalse(registry.TryRegister("hbr_unknown", VerifiedAuth("ten_one", "usr_one", "cred_one"), out HarborRunnerSession? unknown, out string unknownReason), "unknown runner denied");
                AssertNull(unknown, "unknown runner session");
                AssertEqual("runner_owner_unknown", unknownReason, "unknown runner reason");
            });

            await RunTest("Reconnect_StaleDisconnectCannotRemoveReplacement", () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                AuthContext auth = VerifiedAuth("ten_one", "usr_one", "cred_one");
                AssertTrue(registry.TryRegister("hbr_one", auth, out HarborRunnerSession? original, out string originalReason), originalReason);
                AssertTrue(registry.TryRegister("hbr_one", auth, out HarborRunnerSession? replacement, out string replacementReason), replacementReason);
                AssertNotNull(original, "original session");
                AssertNotNull(replacement, "replacement session");
                AssertFalse(original!.Generation == replacement!.Generation, "reconnect generation increments");
                AssertFalse(registry.TryDisconnect(original), "stale disconnect denied");
                AssertTrue(registry.IsCurrent(replacement), "replacement remains current");
                AssertTrue(registry.TryDisconnect(replacement), "current disconnect accepted");
                AssertEqual(0, registry.SessionCount, "replacement disconnected");
            });

            await RunTest("PendingResponse_RequiresRunnerAndGenerationAndRejectsReplay", () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                AssertTrue(registry.TryRegister("hbr_one", VerifiedAuth("ten_one", "usr_one", "cred_one"), out HarborRunnerSession? one, out string oneReason), oneReason);
                AssertTrue(registry.TryRegister("hbr_two", VerifiedAuth("ten_two", "usr_two", "cred_two"), out HarborRunnerSession? two, out string twoReason), twoReason);
                AssertTrue(registry.TryRegisterPending(one!, out HarborPendingRequest<string>? pending, out string pendingReason), pendingReason);
                AssertTrue(registry.TryRegisterPending(one!, out HarborPendingRequest<string>? duplicate, out string duplicateReason), duplicateReason);
                AssertFalse(String.Equals(pending!.RequestId, duplicate!.RequestId, StringComparison.Ordinal), "server request ids are unique");
                AssertFalse(registry.TryCompletePending(two!, pending.RequestId, "wrong runner"), "wrong runner response rejected");
                AssertFalse(registry.TryCompletePending(one!, pending.RequestId, 42), "wrong response type rejected");
                AssertEqual(2, registry.PendingCount, "wrong response type does not consume request");
                AssertTrue(registry.TryCompletePending(one!, pending.RequestId, "accepted"), "matching response accepted");
                AssertEqual("accepted", pending!.Completion.Result, "typed response");
                AssertFalse(registry.TryCompletePending(one!, pending.RequestId, "replayed"), "duplicate response rejected");
                AssertTrue(registry.TryCompletePending(one!, duplicate.RequestId, "second"), "second unique request accepted");
                AssertEqual(0, registry.PendingCount, "pending response removed");
            });

            await RunTest("Reconnect_CancelsOldPendingAndRejectsStaleResponse", () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                AuthContext auth = VerifiedAuth("ten_one", "usr_one", "cred_one");
                AssertTrue(registry.TryRegister("hbr_one", auth, out HarborRunnerSession? original, out string originalReason), originalReason);
                AssertTrue(registry.TryRegisterPending(original!, out HarborPendingRequest<int>? pending, out string pendingReason), pendingReason);
                AssertTrue(registry.TryRegister("hbr_one", auth, out HarborRunnerSession? replacement, out string replacementReason), replacementReason);
                AssertTrue(pending!.Completion.IsCanceled, "old pending canceled on reconnect");
                AssertFalse(registry.TryCompletePending(original!, pending.RequestId, 1), "stale response rejected");
                AssertTrue(registry.TryRegisterPending(replacement!, out HarborPendingRequest<int>? fresh, out string freshReason), freshReason);
                AssertFalse(String.Equals(pending.RequestId, fresh!.RequestId, StringComparison.Ordinal), "reconnect request id changes");
                AssertTrue(registry.TryCompletePending(replacement!, fresh.RequestId, 7), "replacement response accepted");
                AssertEqual(7, fresh!.Completion.Result, "replacement response value");
            });

            await RunTest("DurableOwnerResolution_RunsOutsideRegistryLock", () =>
            {
                LockProbeResolver resolver = new LockProbeResolver();
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, resolver);
                resolver.Registry = registry;
                AuthContext auth = VerifiedAuth("ten_one", "usr_one", "cred_one");
                AssertTrue(registry.TryRegister("hbr_one", auth, out HarborRunnerSession? session, out string registerReason), registerReason);
                AssertTrue(registry.TryRegisterPending(session!, out HarborPendingRequest<int>? pending, out string pendingReason), pendingReason);
                AssertTrue(registry.TryCompletePending(session!, pending!.RequestId, 1), "response accepted while resolution probes the lock");
                AssertTrue(resolver.Calls >= 3, "registration, request and response each resolve the durable owner");
                AssertEqual(0, resolver.LockHeldObservations, "no durable resolution ran while the registry lock was held");
            });

            await RunTest("PendingResponse_ServerIssuedIdsCannotBeReusedAfterReplayCacheEviction", () =>
            {
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, new TestRunnerOwnerResolver());
                AssertTrue(registry.TryRegister("hbr_one", VerifiedAuth("ten_one", "usr_one", "cred_one"), out HarborRunnerSession? session, out string registrationReason), registrationReason);
                string firstRequestId = String.Empty;
                for (int index = 0; index < 4100; index++)
                {
                    AssertTrue(registry.TryRegisterPending(session!, out HarborPendingRequest<int>? pending, out string pendingReason), pendingReason);
                    if (index == 0) firstRequestId = pending!.RequestId;
                    AssertTrue(registry.TryCompletePending(session!, pending!.RequestId, index), "server-issued request completes");
                }

                AssertFalse(registry.TryCompletePending(session!, firstRequestId, 9999), "late response remains rejected after bounded cache eviction");
                AssertTrue(registry.TryRegisterPending(session!, out HarborPendingRequest<int>? fresh, out string freshReason), freshReason);
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

            public bool TryGetOwner(string runnerId, out AuthContext? owner)
            {
                Interlocked.Increment(ref _Calls);
                HarborRunnerSessionRegistry? registry = Registry;
                if (registry != null)
                {
                    Task<int> probe = Task.Run(() => registry.SessionCount);
                    if (!probe.Wait(TimeSpan.FromSeconds(2))) Interlocked.Increment(ref _LockHeldObservations);
                }
                owner = VerifiedAuth("ten_one", "usr_one", "cred_one");
                return String.Equals(runnerId, "hbr_one", StringComparison.Ordinal);
            }
        }

        private sealed class TestRunnerOwnerResolver : IHarborRunnerOwnerResolver
        {
            private readonly Dictionary<string, AuthContext> _Owners = new Dictionary<string, AuthContext>(StringComparer.Ordinal)
            {
                ["hbr_one"] = VerifiedAuth("ten_one", "usr_one", "cred_one"),
                ["hbr_two"] = VerifiedAuth("ten_two", "usr_two", "cred_two")
            };

            public bool TryGetOwner(string runnerId, out AuthContext? owner)
            {
                return _Owners.TryGetValue(runnerId, out owner);
            }
        }
    }
}
