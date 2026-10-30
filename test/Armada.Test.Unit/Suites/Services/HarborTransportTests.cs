namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Net.Sockets;
    using System.Net.WebSockets;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using SyslogLogging;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using System.IO;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Runtimes.Harbor;
    using Armada.Server;
    using Armada.Server.Harbor;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestAgentRuntimeFactory = global::Test.Shared.Infrastructure.TestAgentRuntimeFactory;

    /// <summary>
    /// Isolated Harbor tests over the real WebSocket transport: an in-process Admiral endpoint backed by a SQLite
    /// enrollment store and the real authentication service, driven by fake runners with real credentials.
    /// </summary>
    public sealed class HarborTransportTests : TestSuite
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        /// <inheritdoc />
        public override string Name => "Harbor Transport";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Harbor_DisabledByDefault", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertFalse(settings.Harbor.Enabled, "Harbor settings default to disabled");
                AssertFalse(new HarborRunnerSessionRegistry().Enabled, "session registry defaults to disabled");
                HarborSettings copied = JsonSerializer.Deserialize<HarborSettings>("{}")!;
                AssertFalse(copied.Enabled, "absent setting stays disabled");
            });

            await RunTest("HarborWorkspace_ChannelAuthorityGenerationAndTypedResults", HarborWorkspace_ChannelAuthorityGenerationAndTypedResults);
            await RunTest("HarborWorkspace_RejectsInvalidShapesAndUnavailableRunner", HarborWorkspace_RejectsInvalidShapesAndUnavailableRunner);
            await RunTest("HarborMissionLaunch_UsesBoundRunnerAndVerifiedCheckoutAddress", HarborMissionLaunch_UsesBoundRunnerAndVerifiedCheckoutAddress);
            await RunTest("HarborMissionLaunch_BoundCheckoutFailsClosedWhenDisabledOrOffline", HarborMissionLaunch_BoundCheckoutFailsClosedWhenDisabledOrOffline);
            await RunTest("HarborDiscardUncommittedDock_RetainsBindingWhenReclaimRpcFails", HarborDiscardUncommittedDock_RetainsBindingWhenReclaimRpcFails);

            await RunTest("Handshake_DeniesInvalidCredentialsAndIgnoresIdentityHeaders", async () =>
            {
                await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, await harness.EnrollAsync(harness.Administrator, "hbr_a", harness.OwnerA).ConfigureAwait(false), "tenant administrator enrolls runner");
                AssertEqual(HttpStatusCode.Forbidden, await harness.EnrollAsync(harness.OwnerB, "hbr_forbidden", harness.OwnerB).ConfigureAwait(false), "non-administrator cannot enroll");

                await using (FakeRunner wrongKey = await harness.ConnectAsync(new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer not-a-real-token",
                    ["x-tenant-guid"] = harness.OwnerA.TenantId,
                    ["x-access-key"] = "anything",
                    ["X-User-Id"] = harness.OwnerA.UserId
                }).ConfigureAwait(false))
                {
                    HarborHandshakeAck ack = await wrongKey.NextAsync<HarborHandshakeAck>().ConfigureAwait(false);
                    AssertFalse(ack.Accepted, "invalid bearer rejected");
                    AssertEqual("harbor_authentication_failed", ack.Reason, "invalid bearer reason");
                    AssertTrue(await wrongKey.WaitClosedAsync().ConfigureAwait(false), "invalid bearer link closed");
                }

                await using (FakeRunner headersOnly = await harness.ConnectAsync(new Dictionary<string, string>
                {
                    ["x-tenant-guid"] = harness.OwnerA.TenantId,
                    ["x-access-key"] = "non-empty"
                }).ConfigureAwait(false))
                {
                    HarborHandshakeAck ack = await headersOnly.NextAsync<HarborHandshakeAck>().ConfigureAwait(false);
                    AssertEqual("harbor_authentication_failed", ack.Reason, "identity headers without a credential grant nothing");
                    AssertTrue(await headersOnly.WaitClosedAsync().ConfigureAwait(false), "header-only link closed");
                }

                await using (FakeRunner impostor = await harness.ConnectAsync(harness.OwnerB).ConfigureAwait(false))
                {
                    HarborHandshakeAck ack = await impostor.HandshakeAsync("hbr_a").ConfigureAwait(false);
                    AssertFalse(ack.Accepted, "another verified principal cannot claim the runner");
                    AssertEqual("runner_owner_mismatch", ack.Reason, "impostor reason");
                    AssertTrue(await impostor.WaitClosedAsync().ConfigureAwait(false), "impostor link closed");
                }

                await using (FakeRunner unknown = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                {
                    HarborHandshakeAck ack = await unknown.HandshakeAsync("hbr_not_enrolled").ConfigureAwait(false);
                    AssertEqual("runner_owner_unknown", ack.Reason, "unenrolled runner rejected");
                }

                await using (FakeRunner noHandshake = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                {
                    await noHandshake.SendAsync(new HarborHeartbeat()).ConfigureAwait(false);
                    HarborHandshakeAck ack = await noHandshake.NextAsync<HarborHandshakeAck>().ConfigureAwait(false);
                    AssertEqual("harbor_handshake_required", ack.Reason, "work before handshake rejected");
                }
                AssertEqual(0, harness.Registry.SessionCount, "no rejected link created a session");

                await using FakeRunner owner = await harness.ConnectAsync(harness.OwnerA, new Dictionary<string, string>
                {
                    ["x-tenant-guid"] = "ten_spoofed",
                    ["x-user-guid"] = "usr_spoofed"
                }).ConfigureAwait(false);
                HarborHandshakeAck accepted = await owner.HandshakeAsync("hbr_a").ConfigureAwait(false);
                AssertTrue(accepted.Accepted, "enrolled owner accepted: " + accepted.Reason);
                AssertTrue(harness.Registry.TryGetCurrent("hbr_a", out HarborRunnerSession? session), "session registered");
                AssertEqual(harness.OwnerA.TenantId, session!.Identity.TenantId, "tenant comes from the credential");
                AssertEqual(harness.OwnerA.UserId, session.Identity.UserId, "user comes from the credential");
                AssertEqual(harness.OwnerA.CredentialId, session.Identity.CredentialId, "connection bound to the credential");
            });

            await RunTest("TwoRunners_AffinityOutputOrderAndProcessOwnership", async () =>
            {
                await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
                await harness.EnrollBothAsync().ConfigureAwait(false);
                await using FakeRunner runnerA = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
                await using FakeRunner runnerB = await harness.ConnectAsync(harness.OwnerB).ConfigureAwait(false);
                AssertTrue((await runnerA.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner A connected");
                AssertTrue((await runnerB.HandshakeAsync("hbr_b").ConfigureAwait(false)).Accepted, "runner B connected");

                HarborLaunchResult denied = await harness.Coordinator.LaunchAsync(harness.OwnerB.Auth, "hbr_a", "mission-1", Plan()).ConfigureAwait(false);
                AssertFalse(denied.Accepted, "owner of B cannot command A");
                AssertEqual("harbor_command_unauthorized", denied.Reason, "cross-owner launch reason");

                HarborLaunchResult launch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-1", Plan()).ConfigureAwait(false);
                AssertTrue(launch.Accepted, "launch on A accepted: " + launch.Reason);
                HarborLaunchRequest received = await runnerA.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                AssertEqual(launch.JobId, received.JobId, "launch reaches the selected runner with the server job id");
                HarborJobRecord? serverIssuedJob = await harness.Driver.HarborJobs.ReadAsync(launch.JobId!).ConfigureAwait(false);
                AssertTrue(serverIssuedJob != null && !String.IsNullOrWhiteSpace(serverIssuedJob.LaunchKey), "the server persists its launch key");
                AssertEqual(serverIssuedJob!.LaunchKey, received.LaunchKey, "the deserialized wire launch carries the exact server-issued key");
                AssertTrue(await runnerB.NoMessageAsync().ConfigureAwait(false), "other runner receives nothing");
                string jobId = launch.JobId!;

                await runnerB.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 999 }).ConfigureAwait(false);
                AssertEqual("harbor_job_not_owned", (await runnerB.NextAsync<HarborError>().ConfigureAwait(false)).Message, "other runner cannot claim the process");

                await runnerA.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 4242 }).ConfigureAwait(false);
                await runnerA.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 4343 }).ConfigureAwait(false);
                AssertEqual("harbor_job_started_duplicate", (await runnerA.NextAsync<HarborError>().ConfigureAwait(false)).Message, "second start rejected");

                await runnerA.SendAsync(Output(jobId, 0, "one")).ConfigureAwait(false);
                await runnerA.SendAsync(Output(jobId, 1, "two")).ConfigureAwait(false);
                await runnerA.SendAsync(Output(jobId, 1, "replay")).ConfigureAwait(false);
                AssertEqual("harbor_output_replayed", (await runnerA.NextAsync<HarborError>().ConfigureAwait(false)).Message, "replayed chunk rejected");
                await runnerA.SendAsync(Output(jobId, 5, "gap")).ConfigureAwait(false);
                AssertEqual("harbor_output_gap", (await runnerA.NextAsync<HarborError>().ConfigureAwait(false)).Message, "skipped chunk rejected");
                await runnerB.SendAsync(Output(jobId, 2, "foreign")).ConfigureAwait(false);
                AssertEqual("harbor_job_not_owned", (await runnerB.NextAsync<HarborError>().ConfigureAwait(false)).Message, "other runner cannot write output");
                await runnerA.SendAsync(Output(jobId, 2, "three")).ConfigureAwait(false);

                await runnerB.SendAsync(new HarborExited { JobId = jobId, ExitCode = 0 }).ConfigureAwait(false);
                AssertEqual("harbor_job_not_owned", (await runnerB.NextAsync<HarborError>().ConfigureAwait(false)).Message, "other runner cannot report exit");
                await runnerA.SendAsync(new HarborExited { JobId = jobId, ExitCode = 3 }).ConfigureAwait(false);

                HarborJobSnapshot done = await launch.Completion!.WaitAsync(Wait).ConfigureAwait(false);
                AssertEqual(HarborJobStateEnum.Exited, done.State, "job exited");
                AssertEqual(3, done.ExitCode, "exit code from the owning runner");
                AssertEqual(4242, done.ProcessId, "process id from the owning runner's first start");
                AssertEqual("hbr_a", done.RunnerId, "job stays on its runner");
                AssertEqual("one|two|three", String.Join("|", Data(done)), "output kept in sequence order");

                await runnerA.SendAsync(new HarborExited { JobId = jobId, ExitCode = 0 }).ConfigureAwait(false);
                AssertEqual("harbor_job_exit_duplicate", (await runnerA.NextAsync<HarborError>().ConfigureAwait(false)).Message, "duplicate exit rejected");
            });

            await RunTest("DuplicateLaunchCapacityAndStopRouting", async () =>
            {
                await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
                await harness.EnrollBothAsync().ConfigureAwait(false);
                await using FakeRunner runnerA = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
                await using FakeRunner runnerB = await harness.ConnectAsync(harness.OwnerB).ConfigureAwait(false);
                AssertTrue((await runnerA.HandshakeAsync("hbr_a", 2).ConfigureAwait(false)).Accepted, "runner A connected");
                AssertTrue((await runnerB.HandshakeAsync("hbr_b").ConfigureAwait(false)).Accepted, "runner B connected");

                HarborLaunchResult first = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-dup", Plan()).ConfigureAwait(false);
                AssertTrue(first.Accepted, first.Reason);
                await runnerA.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                HarborLaunchResult duplicate = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-dup", Plan()).ConfigureAwait(false);
                AssertEqual("harbor_job_duplicate", duplicate.Reason, "same launch key rejected while live");
                AssertTrue(await runnerA.NoMessageAsync().ConfigureAwait(false), "duplicate launch never reaches the runner");

                HarborLaunchResult second = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-other", Plan()).ConfigureAwait(false);
                AssertTrue(second.Accepted, second.Reason);
                await runnerA.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                HarborLaunchResult overCapacity = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-third", Plan()).ConfigureAwait(false);
                AssertEqual("harbor_runner_capacity_exhausted", overCapacity.Reason, "advertised capacity enforced");
                AssertTrue(await harness.Coordinator.IsRunnerAtCapacityAsync(harness.OwnerA.Auth, "hbr_a").ConfigureAwait(false),
                    "runner owner sees its full runner as at capacity");
                AssertFalse(await harness.Coordinator.IsRunnerAtCapacityAsync(harness.OwnerB.Auth, "hbr_a").ConfigureAwait(false),
                    "another owner cannot use the capacity probe as an admission signal");
                AssertFalse(await harness.Coordinator.IsRunnerAtCapacityAsync(harness.Administrator.Auth, "hbr_a").ConfigureAwait(false),
                    "administrator authority does not make the capacity probe report an owner's runner");
                AssertFalse(await harness.Coordinator.IsRunnerAtCapacityAsync(harness.OwnerA.Auth, "hbr_missing").ConfigureAwait(false),
                    "a missing runner is not reported as at capacity");

                await runnerA.SendAsync(new HarborStarted { JobId = first.JobId!, ProcessId = 100 }).ConfigureAwait(false);
                await runnerA.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string> { first.JobId!, second.JobId! } }).ConfigureAwait(false);
                HarborCommandResult unauthorizedStop = await harness.Coordinator.StopAsync(harness.OwnerB.Auth, first.JobId!).ConfigureAwait(false);
                AssertEqual("harbor_command_unauthorized", unauthorizedStop.Reason, "other owner cannot stop the job");
                AssertTrue(await runnerA.NoMessageAsync().ConfigureAwait(false), "unauthorized stop sends nothing");

                HarborCommandResult stop = await harness.Coordinator.StopAsync(harness.OwnerA.Auth, first.JobId!, 250).ConfigureAwait(false);
                AssertTrue(stop.Accepted, "owner stop accepted: " + stop.Reason);
                HarborKillRequest kill = await runnerA.NextAsync<HarborKillRequest>().ConfigureAwait(false);
                AssertEqual(first.JobId, kill.JobId, "kill targets the job on its owning runner");
                AssertEqual(250, kill.GracefulTimeoutMs, "kill grace period");
                AssertTrue(await runnerB.NoMessageAsync().ConfigureAwait(false), "stop never reaches another runner");
                AssertTrue(harness.Coordinator.TryGetJob(first.JobId!, out HarborJobSnapshot? stopping), "job known");
                AssertEqual(HarborJobStateEnum.Stopping, stopping!.State, "job is stopping");

                await runnerA.SendAsync(new HarborExited { JobId = first.JobId!, ExitCode = 137 }).ConfigureAwait(false);
                HarborJobSnapshot stopped = await first.Completion!.WaitAsync(Wait).ConfigureAwait(false);
                AssertEqual(137, stopped.ExitCode, "stopped exit code");
                AssertEqual("harbor_job_not_running", (await harness.Coordinator.StopAsync(harness.OwnerA.Auth, first.JobId!).ConfigureAwait(false)).Reason, "stopping a finished job is rejected");

                HarborLaunchResult relaunch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-dup", Plan()).ConfigureAwait(false);
                AssertTrue(relaunch.Accepted, "launch key reusable after the job ended: " + relaunch.Reason);
                AssertFalse(String.Equals(first.JobId, relaunch.JobId, StringComparison.Ordinal), "server job ids are never reused");
                await runnerA.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                await runnerA.SendAsync(new HarborStarted { JobId = second.JobId!, ProcessId = 101 }).ConfigureAwait(false);
                await runnerA.SendAsync(new HarborError { JobId = relaunch.JobId!, Message = "working_directory_outside_roots" }).ConfigureAwait(false);
                AssertTrue(await runnerA.NoMessageAsync().ConfigureAwait(false), "named refusal has been processed");
                HarborJobRecord? refused = await harness.Driver.HarborJobs.ReadAsync(relaunch.JobId!).ConfigureAwait(false);
                AssertEqual(HarborJobStateEnum.Uncertain, refused!.State, "refusal is durable");
                AssertEqual("harbor_runner_error: working_directory_outside_roots", refused.FailureReason, "specific refusal persisted");

                for (int heartbeat = 0; heartbeat < 2; heartbeat++)
                {
                    await runnerA.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string>() }).ConfigureAwait(false);
                    AssertTrue(await runnerA.NoMessageAsync().ConfigureAwait(false), "empty heartbeat has been processed");
                    refused = await harness.Driver.HarborJobs.ReadAsync(relaunch.JobId!).ConfigureAwait(false);
                    AssertEqual("harbor_runner_error: working_directory_outside_roots", refused!.FailureReason,
                        "heartbeat preserves the durable refusal cause");
                    AssertEqual(HarborJobStateEnum.Uncertain, refused.State, "refusal remains fenced");
                    AssertFalse(refused.StopConfirmed, "a missing heartbeat does not prove a stop");
                    AssertFalse(relaunch.Completion!.IsCompleted, "refusal cannot deliver completion");
                }
                HarborJobRecord? absent = await harness.Driver.HarborJobs.ReadAsync(second.JobId!).ConfigureAwait(false);
                AssertEqual(HarborJobStateEnum.Uncertain, absent!.State, "a missing running job becomes uncertain");
                AssertEqual("harbor_job_not_reported", absent.FailureReason, "a missing running job is still detected");
                AssertFalse(second.Completion!.IsCompleted, "missing job has no terminal proof");

            });

            await RunTest("HarborCapacity_AssignmentHoldsWithoutProvisioningAndRetriesOnTheSameRunner", async () =>
            {
                await RunHarborCapacityAssignmentAsync(false).ConfigureAwait(false);
            });

            await RunTest("HarborCapacity_LateRunnerCapacityRaceRollsBackAndRetries", async () =>
            {
                await RunHarborCapacityAssignmentAsync(true).ConfigureAwait(false);
            });

            await RunTest("CommandAuthority_MatchesEnrollmentAuthority", async () =>
            {
                await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
                Principal globalOwner = await harness.AddPrincipalAsync(harness.OwnerA.TenantId, "global-owner", false, true).ConfigureAwait(false);
                Principal globalAdministrator = await harness.AddPrincipalAsync(harness.OwnerA.TenantId, "global-admin", false, true).ConfigureAwait(false);
                string otherTenantId = await harness.AddTenantAsync().ConfigureAwait(false);
                Principal otherTenantAdministrator = await harness.AddPrincipalAsync(otherTenantId, "other-tenant-admin", true, false).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, await harness.EnrollAsync(globalAdministrator, "hbr_global", globalOwner).ConfigureAwait(false), "global administrator enrolls a global-admin-owned runner");
                await using FakeRunner runner = await harness.ConnectAsync(globalOwner).ConfigureAwait(false);
                AssertTrue((await runner.HandshakeAsync("hbr_global").ConfigureAwait(false)).Accepted, "global-admin-owned runner connected");

                HarborLaunchResult tenantAdministratorLaunch = await harness.Coordinator.LaunchAsync(harness.Administrator.Auth, "hbr_global", "mission-authority-tenant-admin", Plan()).ConfigureAwait(false);
                AssertEqual("harbor_command_unauthorized", tenantAdministratorLaunch.Reason, "tenant administrator cannot launch on a global administrator's runner");
                HarborLaunchResult otherTenantLaunch = await harness.Coordinator.LaunchAsync(otherTenantAdministrator.Auth, "hbr_global", "mission-authority-other-tenant", Plan()).ConfigureAwait(false);
                AssertEqual("harbor_command_unauthorized", otherTenantLaunch.Reason, "another tenant's administrator cannot launch");
                AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "refused launches never reach the runner");

                HarborLaunchResult ownerLaunch = await harness.Coordinator.LaunchAsync(globalOwner.Auth, "hbr_global", "mission-authority-owner", Plan()).ConfigureAwait(false);
                AssertTrue(ownerLaunch.Accepted, "runner owner may launch: " + ownerLaunch.Reason);
                AssertEqual(ownerLaunch.JobId, (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId, "owner launch reaches the runner");
                HarborLaunchResult globalLaunch = await harness.Coordinator.LaunchAsync(globalAdministrator.Auth, "hbr_global", "mission-authority-global", Plan()).ConfigureAwait(false);
                AssertTrue(globalLaunch.Accepted, "global administrator may launch: " + globalLaunch.Reason);
                await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);

                AssertEqual("harbor_command_unauthorized", (await harness.Coordinator.StopAsync(harness.Administrator.Auth, ownerLaunch.JobId!).ConfigureAwait(false)).Reason, "tenant administrator cannot stop a global administrator's job");
                AssertEqual("harbor_command_unauthorized", (await harness.Coordinator.StopAsync(otherTenantAdministrator.Auth, ownerLaunch.JobId!).ConfigureAwait(false)).Reason, "another tenant's administrator cannot stop");
                AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "refused stops never reach the runner");
                HarborCommandResult ownerStop = await harness.Coordinator.StopAsync(globalOwner.Auth, ownerLaunch.JobId!).ConfigureAwait(false);
                AssertTrue(ownerStop.Accepted, "runner owner may stop: " + ownerStop.Reason);
                AssertEqual(ownerLaunch.JobId, (await runner.NextAsync<HarborKillRequest>().ConfigureAwait(false)).JobId, "owner stop reaches the runner");
                HarborCommandResult globalStop = await harness.Coordinator.StopAsync(globalAdministrator.Auth, globalLaunch.JobId!).ConfigureAwait(false);
                AssertTrue(globalStop.Accepted, "global administrator may stop: " + globalStop.Reason);
                AssertEqual(globalLaunch.JobId, (await runner.NextAsync<HarborKillRequest>().ConfigureAwait(false)).JobId, "global administrator stop reaches the runner");
            });

            string[] revokedFrameKinds = new string[] { "started", "output", "exited" };
            foreach (string frameKind in revokedFrameKinds)
            {
                await RunTest("JobFrame_AfterDurableRevocation_IsRefusedByName_" + frameKind, async () =>
                {
                    await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    await using FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
                    AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                    HarborLaunchResult launch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-revoked-frame", Plan()).ConfigureAwait(false);
                    AssertTrue(launch.Accepted, launch.Reason);
                    string jobId = (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                    if (frameKind != "started")
                    {
                        await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 81 }).ConfigureAwait(false);
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "start accepted before revocation");
                    }

                    // Revoked directly in the durable store, as another Admiral instance would: no in-process notice.
                    AssertTrue(await harness.Driver.HarborRunnerEnrollments.TryRevokeAsync("hbr_a", 1, harness.Administrator.UserId, DateTime.UtcNow).ConfigureAwait(false), "durable revocation");

                    HarborMessage frame = frameKind switch
                    {
                        "started" => new HarborStarted { JobId = jobId, ProcessId = 81 },
                        "output" => Output(jobId, 0, "after revocation"),
                        _ => new HarborExited { JobId = jobId, ExitCode = 0 }
                    };
                    await runner.SendAsync(frame).ConfigureAwait(false);
                    HarborError refused = await runner.NextAsync<HarborError>().ConfigureAwait(false);
                    AssertEqual("runner_enrollment_revoked", refused.Message, frameKind + " frame from a revoked runner is refused by name");
                    AssertTrue(await runner.WaitClosedAsync().ConfigureAwait(false), "revoked runner's link closed");
                    AssertTrue(harness.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? uncertain), "revoked job remains known");
                    AssertEqual(HarborJobStateEnum.Uncertain, uncertain!.State, "revocation does not confirm that the runner process stopped");
                    AssertEqual("runner_enrollment_revoked", uncertain.FailureReason, "uncertain job carries the refusal reason");
                    AssertEqual(0, uncertain.Output.Count, "no output accepted after revocation");
                    AssertFalse(launch.Completion!.IsCompleted, "an unconfirmed process does not deliver a lifecycle completion");
                });
            }

            await RunTest("JobFrame_AfterDurableGenerationChange_IsRefusedAsStale", async () =>
            {
                await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
                await harness.EnrollBothAsync().ConfigureAwait(false);
                await using FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
                AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                HarborLaunchResult launch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-stale-frame", Plan()).ConfigureAwait(false);
                AssertTrue(launch.Accepted, launch.Reason);
                string jobId = (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 82 }).ConfigureAwait(false);
                AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "start accepted");

                // Revoke and re-enroll durably, as another instance would. The link still holds generation 1.
                AssertTrue(await harness.Driver.HarborRunnerEnrollments.TryRevokeAsync("hbr_a", 1, harness.Administrator.UserId, DateTime.UtcNow).ConfigureAwait(false), "durable revocation");
                HarborRunnerEnrollment reenrolled = new HarborRunnerEnrollment
                {
                    RunnerId = "hbr_a",
                    TenantId = harness.OwnerA.TenantId,
                    UserId = harness.OwnerA.UserId,
                    AuthMethod = "Bearer",
                    CredentialId = harness.OwnerA.CredentialId,
                    Generation = 3,
                    Active = true,
                    CreatedUtc = DateTime.UtcNow,
                    LastUpdateUtc = DateTime.UtcNow
                };
                AssertTrue(await harness.Driver.HarborRunnerEnrollments.TryEnrollAsync(reenrolled, 2).ConfigureAwait(false), "durable re-enrollment");

                await runner.SendAsync(Output(jobId, 0, "stale generation")).ConfigureAwait(false);
                HarborError refused = await runner.NextAsync<HarborError>().ConfigureAwait(false);
                AssertEqual("runner_enrollment_generation_stale", refused.Message, "output from a stale enrollment generation is refused by name");
                AssertTrue(await runner.WaitClosedAsync().ConfigureAwait(false), "stale link closed");
                AssertTrue(harness.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? uncertain), "stale job remains known");
                AssertEqual(HarborJobStateEnum.Uncertain, uncertain!.State, "stale generation does not confirm that the runner process stopped");
                AssertFalse(launch.Completion!.IsCompleted, "stale runner cannot deliver a terminal lifecycle result");
            });

            await RunTest("Heartbeat_AfterDurableRevocation_NamesTheRevocation", async () =>
            {
                await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
                await harness.EnrollBothAsync().ConfigureAwait(false);
                await using FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
                AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                AssertTrue(await harness.Driver.HarborRunnerEnrollments.TryRevokeAsync("hbr_a", 1, harness.Administrator.UserId, DateTime.UtcNow).ConfigureAwait(false), "durable revocation");
                await runner.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string>() }).ConfigureAwait(false);
                AssertEqual("runner_enrollment_revoked", (await runner.NextAsync<HarborError>().ConfigureAwait(false)).Message, "revoked heartbeat names the revocation");
                AssertTrue(await runner.WaitClosedAsync().ConfigureAwait(false), "revoked link closed");
            });

            await RunTest("Reconnect_RebindsJobsAndStaleLinkCannotTakeOver", async () =>
            {
                await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
                await harness.EnrollBothAsync().ConfigureAwait(false);
                await using FakeRunner runnerB = await harness.ConnectAsync(harness.OwnerB).ConfigureAwait(false);
                AssertTrue((await runnerB.HandshakeAsync("hbr_b").ConfigureAwait(false)).Accepted, "runner B connected");
                FakeRunner original = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
                AssertTrue((await original.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "original link connected");

                HarborLaunchResult launch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-reconnect", Plan()).ConfigureAwait(false);
                AssertTrue(launch.Accepted, launch.Reason);
                string jobId = (await original.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                DateTime runnerStartedUtc = DateTime.UtcNow;
                await original.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 700, ProcessStartedUtc = runnerStartedUtc }).ConfigureAwait(false);
                await original.SendAsync(Output(jobId, 0, "before")).ConfigureAwait(false);
                await original.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string> { jobId } }).ConfigureAwait(false);
                AssertTrue(await original.NoMessageAsync().ConfigureAwait(false), "original link events accepted");

                await using FakeRunner replacement = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
                AssertTrue((await replacement.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "replacement link connected");

                await original.SendAsync(Output(jobId, 1, "stale")).ConfigureAwait(false);
                HarborError staleError = await original.NextAsync<HarborError>().ConfigureAwait(false);
                AssertEqual("harbor_session_stale", staleError.Message, "stale link event rejected");
                AssertTrue(await original.WaitClosedAsync().ConfigureAwait(false), "stale link closed by the Admiral");
                await original.DisposeAsync().ConfigureAwait(false);

                AssertTrue(harness.Registry.TryGetCurrent("hbr_a", out HarborRunnerSession? currentA), "runner A still has a current session after the stale link closed");
                AssertEqual(2, harness.Registry.SessionCount, "stale disconnect removed neither runner A's replacement nor runner B");
                AssertTrue(currentA!.Generation > 0 && harness.Registry.IsCurrent(currentA), "replacement session remains current");
                await replacement.SendAsync(Output(jobId, 1, "unbound")).ConfigureAwait(false);
                AssertEqual("harbor_job_not_bound", (await replacement.NextAsync<HarborError>().ConfigureAwait(false)).Message, "replacement cannot report before it claims the job");
                HarborJobRecord? durable = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                HarborResumeJobInfo resumeProof = new HarborResumeJobInfo
                {
                    JobId = jobId,
                    LaunchKey = durable!.LaunchKey,
                    ProcessId = 700,
                    ProcessStartedUtc = durable.RunnerProcessStartedUtc,
                    IsRunning = true,
                    FirstAvailableOutputSequence = 0,
                    NextOutputSequence = 1
                };
                await replacement.SendAsync(new HarborResumeRequest { Jobs = new List<HarborResumeJobInfo> { resumeProof } }).ConfigureAwait(false);
                HarborResumeJobResult resume = (await replacement.NextAsync<HarborResumeResponse>().ConfigureAwait(false)).Jobs[0];
                AssertEqual(HarborResumeDecisionEnum.Replay, resume.Decision, "replacement adopts only after process identity proof");
                AssertEqual(1L, resume.ReplayFromSequence, "replacement replays from the durable cursor");
                await replacement.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string> { jobId, "hjob_forged" } }).ConfigureAwait(false);
                HarborError forged = await replacement.NextAsync<HarborError>().ConfigureAwait(false);
                AssertEqual("hjob_forged", forged.JobId, "unknown reported job named");
                AssertEqual("harbor_job_unknown", forged.Message, "unknown reported job rejected");
                await runnerB.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string> { jobId } }).ConfigureAwait(false);
                AssertEqual("harbor_job_not_owned", (await runnerB.NextAsync<HarborError>().ConfigureAwait(false)).Message, "another runner cannot claim the job on heartbeat");

                await replacement.SendAsync(Output(jobId, 1, "after")).ConfigureAwait(false);
                await replacement.SendAsync(new HarborExited { JobId = jobId, ExitCode = 0 }).ConfigureAwait(false);
                HarborJobSnapshot done = await launch.Completion!.WaitAsync(Wait).ConfigureAwait(false);
                AssertEqual("before|after", String.Join("|", Data(done)), "output continues in order across reconnect");
                AssertEqual(700, done.ProcessId, "process ownership survives reconnect");
                AssertEqual("hbr_a", done.RunnerId, "job stays on its runner");

                HarborLaunchResult next = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-after", Plan()).ConfigureAwait(false);
                AssertTrue(next.Accepted, next.Reason);
                AssertEqual(next.JobId, (await replacement.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId, "new work reaches the replacement link");

                await replacement.CloseAsync().ConfigureAwait(false);
                AssertTrue(await WaitUntilAsync(() => harness.Registry.SessionCount == 1).ConfigureAwait(false), "closed link unregistered");
                HarborLaunchResult unavailable = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-offline", Plan()).ConfigureAwait(false);
                AssertEqual("harbor_runner_unavailable", unavailable.Reason, "disconnected runner refuses work");
                AssertTrue(await runnerB.NoMessageAsync().ConfigureAwait(false), "no fallback to another runner");
            });

            await RunTest("Revocation_ClosesLinkAndReenrollmentCannotRebindOldJobs", async () =>
            {
                await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
                await harness.EnrollBothAsync().ConfigureAwait(false);
                FakeRunner revoked = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
                AssertTrue((await revoked.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                HarborLaunchResult launch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-revoke", Plan()).ConfigureAwait(false);
                AssertTrue(launch.Accepted, launch.Reason);
                string jobId = (await revoked.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                await revoked.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 55 }).ConfigureAwait(false);

                AssertEqual(HttpStatusCode.OK, await harness.RevokeAsync(harness.Administrator, "hbr_a").ConfigureAwait(false), "administrator revokes");
                await revoked.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string> { jobId } }).ConfigureAwait(false);
                HarborError closed = await revoked.NextAsync<HarborError>().ConfigureAwait(false);
                AssertFalse(String.IsNullOrEmpty(closed.Message), "revoked heartbeat names its reason");
                AssertTrue(await revoked.WaitClosedAsync().ConfigureAwait(false), "revoked link closed");
                await revoked.DisposeAsync().ConfigureAwait(false);

                AssertTrue(harness.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? uncertain), "revoked job remains known");
                AssertEqual(HarborJobStateEnum.Uncertain, uncertain!.State, "revocation leaves the runner process uncertain");
                AssertFalse(launch.Completion!.IsCompleted, "revocation does not invoke the terminal observer");
                AssertEqual("harbor_runner_unavailable", (await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-revoked", Plan()).ConfigureAwait(false)).Reason, "revoked runner refuses work");

                await using (FakeRunner beforeReenroll = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                {
                    HarborHandshakeAck ack = await beforeReenroll.HandshakeAsync("hbr_a").ConfigureAwait(false);
                    AssertFalse(ack.Accepted, "revoked runner cannot reconnect");
                }

                AssertEqual(HttpStatusCode.OK, await harness.EnrollAsync(harness.Administrator, "hbr_a", harness.OwnerA).ConfigureAwait(false), "administrator re-enrolls");
                await using FakeRunner reenrolled = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
                AssertTrue((await reenrolled.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "re-enrolled runner connects");
                AssertTrue(harness.Registry.TryGetCurrent("hbr_a", out HarborRunnerSession? fresh), "fresh session");
                AssertEqual(3L, fresh!.EnrollmentGeneration, "fresh session binds the new enrollment generation");
                await reenrolled.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string> { jobId } }).ConfigureAwait(false);
                AssertEqual("harbor_job_not_rebindable", (await reenrolled.NextAsync<HarborError>().ConfigureAwait(false)).Message, "old job cannot be rebound after re-enrollment");
                await reenrolled.SendAsync(new HarborExited { JobId = jobId, ExitCode = 0 }).ConfigureAwait(false);
                AssertEqual("harbor_job_not_owned", (await reenrolled.NextAsync<HarborError>().ConfigureAwait(false)).Message, "re-enrolled link cannot resolve old work");
                AssertTrue(harness.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? after), "job known");
                AssertEqual(HarborJobStateEnum.Uncertain, after!.State, "old job stays fenced after re-enrollment");
            });

            await RunTest("MissionRoute_DefaultsLocalAndOptsInExplicitly", () =>
            {
                Captain captain = new Captain("route-captain", AgentRuntimeEnum.ClaudeCode);
                Mission mission = new Mission("route mission") { VesselId = "vsl_route" };
                HarborSettings settings = new HarborSettings();
                settings.MissionRoutes.Add(new HarborMissionRoute { RunnerId = "hbr_vessel", VesselId = "vsl_route" });
                AssertNull(HarborMissionRouting.Resolve(settings, captain, mission), "routes do nothing while Harbor is disabled");
                settings.Enabled = true;
                AssertNull(HarborMissionRouting.Resolve(new HarborSettings { Enabled = true }, captain, mission), "no route means a local launch");
                AssertEqual("hbr_vessel", HarborMissionRouting.Resolve(settings, captain, mission)!.RunnerId, "a vessel route opts the vessel in");
                settings.MissionRoutes.Add(new HarborMissionRoute { RunnerId = "hbr_other", CaptainId = "cpt_someone_else" });
                settings.MissionRoutes.Add(new HarborMissionRoute { RunnerId = "hbr_captain", CaptainId = captain.Id });
                AssertEqual("hbr_captain", HarborMissionRouting.Resolve(settings, captain, mission)!.RunnerId, "a captain route takes precedence over a vessel route");
                AssertNull(HarborMissionRouting.Resolve(settings, new Captain("local-captain", AgentRuntimeEnum.ClaudeCode), new Mission("other") { VesselId = "vsl_other" }), "an unrouted captain and vessel stay local");
                AssertNull(HarborMissionRouting.Resolve(new HarborSettings { Enabled = true, MissionRoutes = new List<HarborMissionRoute> { new HarborMissionRoute { CaptainId = captain.Id } } }, captain, mission), "a route without a runner matches nothing");
            });

            await RunTest("WorkingDirectoryMap_TranslatesOrRefusesByName", () =>
            {
                string admiralRoot = Path.Combine(Path.GetTempPath(), "harbor-map-root");
                HarborMissionRoute shared = new HarborMissionRoute { RunnerId = "hbr_a" };
                AssertTrue(HarborMissionRouting.TryMapWorkingDirectory(shared, Path.Combine(admiralRoot, "dock"), out string sharedPath, out _), "an unmapped route keeps the path");
                AssertEqual(Path.Combine(admiralRoot, "dock"), sharedPath, "shared mount path unchanged");
                HarborMissionRoute mapped = new HarborMissionRoute { RunnerId = "hbr_a", AdmiralWorkingDirectoryRoot = admiralRoot, RunnerWorkingDirectoryRoot = "/runner/docks/" };
                AssertTrue(HarborMissionRouting.TryMapWorkingDirectory(mapped, Path.Combine(admiralRoot, "vessel", "mission"), out string runnerPath, out _), "a dock under the root maps");
                AssertEqual("/runner/docks/vessel/mission", runnerPath, "runner path mirrors the dock path");
                AssertFalse(HarborMissionRouting.TryMapWorkingDirectory(mapped, Path.Combine(Path.GetTempPath(), "elsewhere"), out _, out string outside), "a dock outside the root is refused");
                AssertEqual(HarborMissionRouting.ReasonWorkingDirectoryUnmapped, outside, "outside reason");
                AssertFalse(HarborMissionRouting.TryMapWorkingDirectory(mapped, Path.Combine(admiralRoot + "-backup", "mission"), out _, out _), "a sibling sharing the root's name prefix is refused");
                AssertFalse(HarborMissionRouting.TryMapWorkingDirectory(new HarborMissionRoute { RunnerId = "hbr_a", RunnerWorkingDirectoryRoot = "/runner" }, admiralRoot, out _, out string incomplete), "a half map is refused");
                AssertEqual(HarborMissionRouting.ReasonWorkingDirectoryMapIncomplete, incomplete, "incomplete reason");
            });

            await RunTest("LaunchEnvironment_ForwardsOnlyNamedVariables", () =>
            {
                Dictionary<string, string> allowed = HarborLaunchEnvironment.Select(new Dictionary<string, string> { ["MSBUILDDISABLENODEREUSE"] = "1", ["MAX_THINKING_TOKENS"] = "4096" });
                AssertEqual(2, allowed.Count, "named variables forwarded");
                string refusal = String.Empty;
                try
                {
                    HarborLaunchEnvironment.Select(new Dictionary<string, string> { ["MSBUILDDISABLENODEREUSE"] = "1", ["ANTHROPIC_API_KEY"] = "secret-value", [McpLaunchCredential.EnvironmentVariable] = "launch-token" });
                }
                catch (HarborLaunchException exception)
                {
                    AssertEqual(HarborLaunchEnvironment.ReasonEnvironmentUnsupported, exception.Reason, "refusal reason");
                    refusal = exception.Message;
                }
                AssertTrue(refusal.Contains("ANTHROPIC_API_KEY") && refusal.Contains(McpLaunchCredential.EnvironmentVariable), "refusal names every refused variable: " + refusal);
                AssertFalse(refusal.Contains("secret-value") || refusal.Contains("launch-token"), "refusal never carries a value");
            });

            await RunTest("MissionLaunch_RunsOnItsRunnerThroughTheSharedLifecycle", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        int processId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                        AssertTrue(processId > ProcessSupervisor.SyntheticProcessIdFloor, "a Harbor job runs under a synthetic process id");

                        HarborLaunchRequest launch = await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                        string jobId = launch.JobId;
                        AssertEqual(scope.Dock.WorktreePath, launch.WorkingDirectory, "the runner works in the Admiral's dock");
                        AssertFalse(launch.Environment.ContainsKey(McpLaunchCredential.EnvironmentVariable), "the Admiral launch credential never travels");
                        foreach (string key in launch.Environment.Keys)
                            AssertTrue(HarborLaunchEnvironment.ForwardedVariables.Contains(key), "only named variables travel: " + key);
                        AssertFalse(String.Join(" ", launch.Arguments).Contains("armada-launch-", StringComparison.Ordinal), "the retired global-token format is not in the arguments");
                        AssertTrue(launch.Arguments.Count > 0, "the runtime's own arguments travel");

                        HarborJobRecord? pending = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertNotNull(pending, "the job is durable before the runner reports anything");
                        AssertEqual(scope.Mission.Id, pending!.MissionId, "durable job names its mission");
                        AssertEqual(harness.OwnerA.TenantId, pending.TenantId, "durable job runs for the enrolled tenant");
                        AssertEqual(harness.OwnerA.UserId, pending.UserId, "durable job runs for the enrolled user");
                        AssertEqual(1L, pending.EnrollmentGeneration, "durable job records the enrollment generation");
                        AssertTrue(await scope.Handler.IsMissionProcessActiveAsync(scope.Mission).ConfigureAwait(false), "a pending Harbor job owns the mission process");
                        Captain? launched = await harness.Driver.Captains.ReadAsync(scope.Captain.Id).ConfigureAwait(false);
                        AssertEqual(processId, launched!.ProcessId, "the captain records the synthetic process id like a local process id");

                        await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 9001 }).ConfigureAwait(false);
                        await runner.SendAsync(Output(jobId, 0, "hello from ")).ConfigureAwait(false);
                        await runner.SendAsync(Output(jobId, 1, "the runner\nsecond line\n")).ConfigureAwait(false);
                        await runner.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string> { jobId } }).ConfigureAwait(false);
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "owning runner's frames accepted");
                        HarborJobRecord? running = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Running, running!.State, "durable state follows the runner");
                        AssertEqual(2L, running.NextOutputSequence, "the heartbeat persists the last output sequence");
                        AssertEqual(9001, running.ProcessId, "durable host process id");

                        await runner.SendAsync(new HarborExited { JobId = jobId, ExitCode = 0 }).ConfigureAwait(false);
                        RecordedProcessExit exit = await scope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                        AssertEqual(processId, exit.ProcessId, "the exit reaches the Admiral under the synthetic process id");
                        AssertEqual(0, exit.ExitCode, "exit code from the runner");
                        AssertEqual(scope.Mission.Id, exit.MissionId, "exit names the mission");
                        AssertEqual(scope.Captain.Id, exit.CaptainId, "exit names the captain");

                        string log = await ReadWhenContainsAsync(scope.LogFilePath, "Agent exited with code 0").ConfigureAwait(false);
                        AssertTrue(log.Contains("hello from the runner"), "chunks are joined into lines in the mission log");
                        AssertTrue(log.Contains("second line"), "every line reaches the mission log");
                        AssertTrue(log.Contains("Harbor runner hbr_a"), "the mission log names the runner");
                        AssertFalse(ProcessSupervisor.IsTrackedProcessAlive(processId), "the synthetic process ends with the job");
                        AssertFalse(await scope.Handler.IsMissionProcessActiveAsync(scope.Mission).ConfigureAwait(false), "the mission no longer owns a live process");
                        HarborJobRecord? exited = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Exited, exited!.State, "durable terminal state");
                        AssertEqual(0, exited.ExitCode, "durable exit code");
                        AssertNotNull(exited.CompletedUtc, "durable completion time");
                    }
                }
            });

            await RunTest("MissionLaunch_RegistersSiblingAndEvidenceRootsAtTheRunnersPaths", async () =>
            {
                string outsideEvidence = Path.Combine(Path.GetTempPath(), "armada_harbor_outside_evidence_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(outsideEvidence);
                try
                {
                    await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                    {
                        await harness.EnrollBothAsync().ConfigureAwait(false);
                        await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                        using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a", prepare: async (root, settings, mission) =>
                        {
                            Directory.CreateDirectory(Path.Combine(root, "sibling"));
                            Directory.CreateDirectory(Path.Combine(root, "evidence"));
                            Vessel vessel = new Vessel("HarborRootsVessel", "https://example.invalid/harbor-roots.git");
                            vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo> { new SiblingRepo { RelativePath = "../sibling" } });
                            vessel = await harness.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                            mission.VesselId = vessel.Id;
                            settings.CaptainEvidenceRoots.Add(new CaptainEvidenceRoot { Path = Path.Combine(root, "evidence") });
                            settings.CaptainEvidenceRoots.Add(new CaptainEvidenceRoot { Path = outsideEvidence });
                            settings.Harbor.MissionRoutes[0].AdmiralWorkingDirectoryRoot = root;
                            settings.Harbor.MissionRoutes[0].RunnerWorkingDirectoryRoot = "/runner/work";
                        }).ConfigureAwait(false))
                        {
                            AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                            await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                            HarborLaunchRequest launch = await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);

                            AssertEqual("/runner/work/dock", launch.WorkingDirectory, "the runner works in its own path for the dock");
                            List<string> roots = new List<string>();
                            for (int i = 0; i + 1 < launch.Arguments.Count; i++)
                                if (launch.Arguments[i] == "--add-dir") roots.Add(launch.Arguments[i + 1]);
                            AssertTrue(roots.Contains("/runner/work/sibling"), "the provisioned sibling travels as a runner-side workspace root: " + String.Join(", ", roots));
                            AssertTrue(roots.Contains("/runner/work/evidence"), "the evidence root travels as a runner-side workspace root: " + String.Join(", ", roots));
                            AssertEqual(2, roots.Count, "a root the route cannot map is left out: " + String.Join(", ", roots));
                            AssertFalse(String.Join(" ", launch.Arguments).Contains(outsideEvidence), "an unmapped Admiral path never reaches the runner");

                            await runner.SendAsync(new HarborStarted { JobId = launch.JobId, ProcessId = 9002 }).ConfigureAwait(false);
                            await runner.SendAsync(new HarborExited { JobId = launch.JobId, ExitCode = 0 }).ConfigureAwait(false);
                            await scope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    try { Directory.Delete(outsideEvidence, true); } catch (IOException) { }
                }
            });

            await RunTest("MissionStop_ReachesItsRunnerAndTheExitFlowsBack", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        int processId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                        string jobId = (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                        await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 9002 }).ConfigureAwait(false);
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "start accepted");

                        Captain? captain = await harness.Driver.Captains.ReadAsync(scope.Captain.Id).ConfigureAwait(false);
                        Task stopTask = scope.Handler.HandleStopAgentAsync(captain!);
                        HarborKillRequest kill = await runner.NextAsync<HarborKillRequest>().ConfigureAwait(false);
                        AssertEqual(jobId, kill.JobId, "the captain stop reaches the job on its runner");

                        await runner.SendAsync(new HarborExited { JobId = jobId, ExitCode = 137 }).ConfigureAwait(false);
                        await stopTask.ConfigureAwait(false);
                        AssertTrue(scope.HasLifecycleEvent("captain.stopped"), "confirmed runner exit completes the captain stop");
                        AssertFalse(scope.HasLifecycleEvent("captain.stop_refused"), "confirmed runner exit is not reported as a refused stop");
                        RecordedProcessExit exit = await scope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                        AssertEqual(processId, exit.ProcessId, "stopped job exits through the lifecycle");
                        AssertEqual(137, exit.ExitCode, "stopped exit code");
                    }
                }
            });

            await RunTest("MissionStop_AcceptedKillWithoutConfirmedExitRemainsUncertain", async () =>
            {
                await using (Harness harness = await Harness.StartAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        int processId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                        HarborLaunchRequest launch = await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                        await runner.SendAsync(new HarborStarted { JobId = launch.JobId, ProcessId = 9005 }).ConfigureAwait(false);

                        Captain? captain = await harness.Driver.Captains.ReadAsync(scope.Captain.Id).ConfigureAwait(false);
                        Task stopTask = scope.Handler.HandleStopAgentAsync(captain!);
                        HarborKillRequest kill = await runner.NextAsync<HarborKillRequest>().ConfigureAwait(false);
                        AssertEqual(launch.JobId, kill.JobId, "the runner accepts a kill request for this job");
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "the runner sends no exit before the confirmation wait completes");
                        AssertFalse(stopTask.IsCompleted, "the lifecycle waits for a confirmed runner exit");

                        await stopTask.ConfigureAwait(false);

                        HarborJobRecord? uncertain = await harness.Driver.HarborJobs.ReadAsync(launch.JobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Uncertain, uncertain!.State, "a kill request without an exit becomes an uncertainty hold");
                        AssertFalse(uncertain.StopConfirmed, "sending the kill does not confirm that the remote process stopped");
                        AssertFalse(uncertain.CompletionDelivered, "no completion is delivered without a confirmed exit");
                        AssertTrue(ProcessSupervisor.IsSyntheticProcessAlive(processId), "the synthetic process stays registered while the runner state is uncertain");
                        Mission? heldMission = await harness.Driver.Missions.ReadAsync(scope.Mission.Id).ConfigureAwait(false);
                        AssertEqual(launch.JobId, heldMission!.HarborJobId, "the mission stays bound to the uncertain job");
                        AssertNull(await scope.Admiral.TryNextExitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false), "no terminal lifecycle callback is delivered");
                        AssertFalse(scope.HasLifecycleEvent("captain.stopped"), "the lifecycle does not report an unconfirmed remote stop as stopped");
                        AssertTrue(scope.HasLifecycleEvent("captain.stop_refused"), "the lifecycle reports the unconfirmed stop as refused");
                        ProcessSupervisor.UnregisterSyntheticProcess(processId);
                    }
                }
            });

            await RunTest("MissionStop_WhileRunnerDisconnected_KeepsTheJobAndMissionFenced", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        string jobId;
                        int processId;
                        await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                        {
                            AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                            processId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                            jobId = (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                            await runner.CloseAsync().ConfigureAwait(false);
                        }
                        AssertTrue(await WaitUntilAsync(() => harness.Registry.SessionCount == 0).ConfigureAwait(false), "runner disconnected");

                        Captain? captain = await harness.Driver.Captains.ReadAsync(scope.Captain.Id).ConfigureAwait(false);
                        await scope.Handler.HandleStopAgentAsync(captain!).ConfigureAwait(false);
                        HarborJobRecord? uncertain = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Uncertain, uncertain!.State, "a disconnected stop does not claim that the runner process stopped");
                        AssertFalse(uncertain.StopConfirmed, "disconnected stop leaves stop confirmation unset");
                        AssertEqual(HarborJobCoordinator.ReasonReleasedRunnerUnavailable, uncertain.FailureReason, "uncertain stop reason is durable");
                        AssertTrue(ProcessSupervisor.IsSyntheticProcessAlive(processId), "the mission process identity stays registered while stop is unconfirmed");
                        Mission? held = await harness.Driver.Missions.ReadAsync(scope.Mission.Id).ConfigureAwait(false);
                        Captain? heldCaptain = await harness.Driver.Captains.ReadAsync(scope.Captain.Id).ConfigureAwait(false);
                        AssertEqual(jobId, held!.HarborJobId, "mission remains bound to the uncertain job");
                        AssertEqual(processId, held.ProcessId ?? 0, "mission process identity stays fenced");
                        AssertEqual(CaptainStateEnum.Working, heldCaptain!.State, "captain stays working while the remote process may still run");
                        AssertNull(await scope.Admiral.TryNextExitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false), "no terminal callback is delivered for an unconfirmed stop");
                        AssertFalse(scope.HasLifecycleEvent("captain.stopped"), "the lifecycle does not report an unconfirmed remote stop as stopped");
                        ProcessSupervisor.UnregisterSyntheticProcess(processId);
                    }
                }
            });

            await RunTest("MissionLaunch_OnAnotherOwnersRunner_IsRefusedWithoutLocalFallback", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerB, "hbr_a").ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        string reason = await LaunchRefusalAsync(scope).ConfigureAwait(false);
                        AssertEqual(HarborJobCoordinator.ReasonRunnerOwnerMismatch, reason, "a runner runs work only for its enrolled tenant and user");
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "the refused launch never reaches the runner");
                        AssertEqual(0, (await harness.Driver.HarborJobs.EnumerateAsync(new HarborJobQuery { RunnerId = "hbr_a" }).ConfigureAwait(false)).Count, "no job record for a refused launch");
                    }
                }
            });

            await RunTest("MissionLaunch_ThroughTestHostRuntimeFactory_StillRunsTheRealPlanOnTheRunner", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a", null,
                        (logging, settings) => new TestAgentRuntimeFactory(logging, settings)).ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        int processId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                        AssertTrue(processId > ProcessSupervisor.SyntheticProcessIdFloor, "the routed launch runs as a Harbor job, not on the non-launching runtime");
                        HarborLaunchRequest launch = await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                        AssertTrue(launch.Runtime.Contains("claude"), "the runner receives the real adapter's command: " + launch.Runtime);
                        AssertTrue(launch.Arguments.Count > 0, "the runner receives the real adapter's arguments");

                        await runner.SendAsync(new HarborStarted { JobId = launch.JobId, ProcessId = 9010 }).ConfigureAwait(false);
                        await runner.SendAsync(new HarborExited { JobId = launch.JobId, ExitCode = 0 }).ConfigureAwait(false);
                        RecordedProcessExit exit = await scope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                        AssertEqual(processId, exit.ProcessId, "the runner's exit flows through the lifecycle");
                        AssertEqual(0, exit.ExitCode, "exit code from the runner");
                    }
                }
            });

            await RunTest("MissionLaunch_WithCaptainOfAnotherTenant_IsRefusedBeforeReachingTheRunner", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    string otherTenantId = await harness.AddTenantAsync().ConfigureAwait(false);
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a", captain => captain.TenantId = otherTenantId).ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        string reason = await LaunchRefusalAsync(scope).ConfigureAwait(false);
                        AssertEqual("harbor_captain_tenant_mismatch", reason, "a Harbor-routed mission keeps the mission tenant rule for its captain");
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "the refused launch never reaches the runner");
                    }
                }
            });

            await RunTest("MissionLaunch_WithProviderCredentials_IsRefusedBeforeReachingTheRunner", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a", captain =>
                    {
                        // A captain model with its own base URL and key routes to that endpoint, so the launch sets
                        // provider variables that may not leave the Admiral.
                        captain.Model = "harbor-provider-model";
                        captain.ApiKey = "provider-key-not-for-runners";
                        captain.ApiBaseUrl = "https://provider.example.invalid";
                    }).ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        string reason = await LaunchRefusalAsync(scope).ConfigureAwait(false);
                        AssertEqual(HarborLaunchEnvironment.ReasonEnvironmentUnsupported, reason, "provider credentials never travel to a runner");
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "nothing reaches the runner");
                    }
                }
            });

            await RunTest("MissionRevocationMidJob_KeepsTheMissionFencedUntilStopConfirmation", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        int processId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                        string jobId = (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                        await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 9003 }).ConfigureAwait(false);
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "start accepted");

                        AssertTrue(await harness.Driver.HarborRunnerEnrollments.TryRevokeAsync("hbr_a", 1, harness.Administrator.UserId, DateTime.UtcNow).ConfigureAwait(false), "durable revocation mid-job");
                        await runner.SendAsync(Output(jobId, 0, "after revocation\n")).ConfigureAwait(false);
                        AssertEqual("runner_enrollment_revoked", (await runner.NextAsync<HarborError>().ConfigureAwait(false)).Message, "revoked runner refused by name");

                        HarborJobRecord? uncertain = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Uncertain, uncertain!.State, "durable job remains uncertain after revocation");
                        AssertFalse(uncertain.StopConfirmed, "revocation does not confirm that the remote process stopped");
                        AssertEqual("runner_enrollment_revoked", uncertain.FailureReason, "durable refusal reason");
                        AssertTrue(ProcessSupervisor.IsSyntheticProcessAlive(processId), "mission process remains registered while its runner state is uncertain");
                        Mission? heldMission = await harness.Driver.Missions.ReadAsync(scope.Mission.Id).ConfigureAwait(false);
                        AssertEqual(jobId, heldMission!.HarborJobId, "mission remains bound to its uncertain job");
                        AssertTrue(await scope.Handler.IsMissionProcessActiveAsync(heldMission).ConfigureAwait(false), "mission remains active and cannot be reassigned");
                        AssertNull(await scope.Admiral.TryNextExitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false), "revocation does not invoke terminal lifecycle handling");
                        string log = File.Exists(scope.LogFilePath) ? File.ReadAllText(scope.LogFilePath) : String.Empty;
                        AssertFalse(log.Contains("after revocation"), "no output accepted after revocation");
                        ProcessSupervisor.UnregisterSyntheticProcess(processId);
                    }
                }
            });

            await RunTest("DisconnectedRunner_JobsExpireAsUncertainAfterTheGrace", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        string jobId;
                        int processId;
                        await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                        {
                            AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                            processId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                            jobId = (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                            await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 9004 }).ConfigureAwait(false);
                            AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "start accepted");
                            await runner.CloseAsync().ConfigureAwait(false);
                        }
                        AssertTrue(await WaitUntilAsync(() => harness.Registry.SessionCount == 0).ConfigureAwait(false), "runner disconnected");
                        AssertEqual(0, await harness.Coordinator.ExpireDetachedRunnersAsync(TimeSpan.FromMinutes(5), DateTime.UtcNow).ConfigureAwait(false), "a job inside the grace period stays live");
                        AssertTrue(ProcessSupervisor.IsTrackedProcessAlive(processId), "the mission process stays live inside the grace period");

                        AssertEqual(1, await harness.Coordinator.ExpireDetachedRunnersAsync(TimeSpan.FromMinutes(5), DateTime.UtcNow.AddMinutes(6)).ConfigureAwait(false), "the grace expiry changes the active row to an uncertainty hold");
                        HarborJobRecord? uncertain = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Uncertain, uncertain!.State, "expired disconnect remains uncertain until the runner proves it stopped");
                        AssertEqual(HarborJobCoordinator.ReasonRunnerDisconnected, uncertain.FailureReason, "expiry reason is durable");
                        AssertFalse(uncertain.StopConfirmed, "disconnect timeout does not confirm process exit");
                        AssertTrue(ProcessSupervisor.IsSyntheticProcessAlive(processId), "remote process identity remains registered after the grace period");
                        Mission? heldMission = await harness.Driver.Missions.ReadAsync(scope.Mission.Id).ConfigureAwait(false);
                        AssertEqual(jobId, heldMission!.HarborJobId, "mission remains bound to the uncertain runner job");
                        AssertTrue(await scope.Handler.IsMissionProcessActiveAsync(heldMission).ConfigureAwait(false), "the mission stays active while the runner's process may still run");
                        AssertNull(await scope.Admiral.TryNextExitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false), "grace expiry does not deliver a terminal callback");
                        ProcessSupervisor.UnregisterSyntheticProcess(processId);
                    }
                }
            });

            await RunTest("ShippingHarborConsole_RestartsWithoutLosingItsWriterOrOfflineOutput", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_shipping_harbor_" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                    {
                        await harness.EnrollBothAsync().ConfigureAwait(false);
                        await using (ShippingHarborConsole console = new ShippingHarborConsole(root,
                            "ws://127.0.0.1:" + harness.Primary.Port + "/harbor/link", harness.OwnerA.BearerToken))
                        {
                            AssertTrue(await WaitUntilAsync(() => harness.Registry.SessionCount == 1 || console.HasExited).ConfigureAwait(false), "shipping console connects");
                            AssertFalse(console.HasExited, console.Diagnostics());
                            string release = Path.Combine(root, "work", "release");
                            string counter = Path.Combine(root, "work", "launches");
                            HarborLaunchRequest request = ShippingRunnerPlan(root, "release", release, counter);
                            HarborLaunchResult launched = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth,
                                "hbr_a", "shipping-console-restart", request).ConfigureAwait(false);
                            AssertTrue(launched.Accepted, launched.Reason);
                            string jobId = launched.JobId!;
                            AssertTrue(await WaitUntilAsync(() => harness.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? current)
                                && current.ProcessId.HasValue && String.Concat(Data(current)).Contains("before offline")).ConfigureAwait(false),
                                "real child process starts and reports its first output: " + ShippingRunnerDiagnostics(console, harness.Coordinator, jobId));
                            HarborLocalJobRecord original = console.Journal.Read(jobId);
                            AssertTrue(original.ProcessId.HasValue && original.ProcessStartedUtc.HasValue, "the runner retained exact child identity");
                            AssertTrue(HarborJobJournal.IsExactProcessAlive(original.SupervisorProcessId, original.SupervisorStartedUtc,
                                original.SupervisorLinuxStartTicks, original.SupervisorLinuxBootId), "the independent supervisor is alive: " + DescribeShippingProcess(original.SupervisorProcessId, original.SupervisorStartedUtc));
                            await console.StopConsoleOnlyAsync().ConfigureAwait(false);
                            AssertTrue(await WaitUntilAsync(() => harness.Registry.SessionCount == 0).ConfigureAwait(false), "only the link disconnects");
                            AssertTrue(HarborJobJournal.IsExactProcessAlive(original.ProcessId!.Value, original.ProcessStartedUtc,
                                original.ProcessLinuxStartTicks, original.ProcessLinuxBootId), "the same agent survives console death");
                            AssertTrue(HarborJobJournal.IsExactProcessAlive(original.SupervisorProcessId, original.SupervisorStartedUtc,
                                original.SupervisorLinuxStartTicks, original.SupervisorLinuxBootId), "the output supervisor survives console death");
                            File.WriteAllText(release, "finish");
                            AssertTrue(await WaitUntilAsync(() => console.Journal.Read(jobId).StopConfirmed).ConfigureAwait(false), "the supervisor records the offline exit");
                            HarborLocalJobRecord stopped = console.Journal.Read(jobId);
                            AssertEqual(0, stopped.ExitCode, "the real child records its known exit code");
                            AdmiralInstance restarted = await harness.RestartAdmiralAsync(new NoMissionRecoveryHost(), _ => { }).ConfigureAwait(false);
                            await using (ShippingHarborConsole reconnected = new ShippingHarborConsole(root,
                                "ws://127.0.0.1:" + restarted.Port + "/harbor/link", harness.OwnerA.BearerToken))
                            {
                                AssertTrue(await WaitUntilAsync(() => restarted.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? current)
                                    && current.State == HarborJobStateEnum.Exited && current.CompletionDelivered).ConfigureAwait(false),
                                    "the real console resumes, replays the offline output and completes the durable job");
                                AssertTrue(restarted.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? completed), "the restored job is present");
                                AssertEqual(original.ProcessId, completed!.ProcessId, "recovery keeps the original process identity");
                                string replayed = String.Concat(Data(completed));
                                AssertContains("offline output", replayed, "output captured after console death reaches the restarted Admiral");
                                AssertFalse(replayed.Contains("before offline"), "already accepted output is not replayed");
                                AssertFalse(String.Concat(console.Journal.ReadOutput(jobId, 0).ConvertAll(item => item.Data)).Contains("link-secret-present=True"), "the link credential is absent from the child environment");
                                AssertEqual(1, File.ReadAllLines(counter).Length, "console and Admiral restarts never start a second child");
                                AssertEqual(1, (await harness.Driver.HarborJobs.EnumerateAsync(new HarborJobQuery { RunnerId = "hbr_a", Limit = 10 }).ConfigureAwait(false)).Count, "one durable job owns the attempt");
                                string metadata = File.ReadAllText(Path.Combine(root, "state", jobId, "job.json"));
                                AssertFalse(metadata.Contains(harness.OwnerA.BearerToken), "durable metadata contains no link credential");
                                AssertFalse(metadata.Contains(request.Prompt!), "durable metadata contains no launch prompt");
                            }
                        }
                    }
                }
                finally
                {
                    DeleteShippingRunnerFixture(root);
                }
            });

            await RunTest("ShippingHarborConsole_ExclusiveOwnerAndVerifiedStopUseTheActualProcess", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_shipping_harbor_stop_" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                    {
                        await harness.EnrollBothAsync().ConfigureAwait(false);
                        await using (ShippingHarborConsole console = new ShippingHarborConsole(root,
                            "ws://127.0.0.1:" + harness.Primary.Port + "/harbor/link", harness.OwnerA.BearerToken))
                        {
                            AssertTrue(await WaitUntilAsync(() => harness.Registry.SessionCount == 1 || console.HasExited).ConfigureAwait(false), "shipping console connects");
                            AssertFalse(console.HasExited, console.Diagnostics());
                            HarborLaunchResult launch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth,
                                "hbr_a", "shipping-stop", ShippingRunnerPlan(root, "hold")).ConfigureAwait(false);
                            AssertTrue(launch.Accepted, launch.Reason);
                            AssertTrue(await WaitUntilAsync(() => harness.Coordinator.TryGetJob(launch.JobId!, out HarborJobSnapshot? current)
                                && current.ProcessId.HasValue && String.Concat(Data(current)).Contains("child-ready")).ConfigureAwait(false), "the actual fixture child starts before stop");
                            await using (ShippingHarborConsole duplicate = new ShippingHarborConsole(root,
                                "ws://127.0.0.1:" + harness.Primary.Port + "/harbor/link", harness.OwnerA.BearerToken, ownsJournalJobs: false))
                            {
                                AssertTrue(await WaitUntilAsync(() => duplicate.HasExited).ConfigureAwait(false), "a second console cannot take the same state directory");
                                AssertTrue(duplicate.ExitCode != 0, "the duplicate console reports the ownership refusal");
                                AssertContains("runner_already_running", duplicate.Diagnostics(), "the refusal has a stable name");
                                AssertEqual(1, harness.Registry.SessionCount, "the first authenticated link remains the owner");
                            }
                            AssertTrue(harness.Coordinator.TryGetJob(launch.JobId!, out HarborJobSnapshot? stillRunning)
                                && stillRunning.State == HarborJobStateEnum.Running, "disposing the rejected console leaves the owner job running");
                            HarborCommandResult requested = await harness.Coordinator.StopAsync(harness.OwnerA.Auth, launch.JobId!, 0).ConfigureAwait(false);
                            AssertTrue(requested.Accepted, requested.Reason);
                            HarborJobSnapshot completed = await launch.Completion!.WaitAsync(Wait).ConfigureAwait(false);
                            AssertEqual(HarborJobStateEnum.Exited, completed.State, "the real stop returns a confirmed terminal result");
                            AssertTrue(completed.StopConfirmed && completed.CompletionDelivered && completed.ExitCode.HasValue, "the process exited and the server handled it");
                            HarborLocalJobRecord local = console.Journal.Read(launch.JobId!);
                            AssertFalse(HarborJobJournal.IsExactProcessAlive(local.ProcessId!.Value, local.ProcessStartedUtc,
                                local.ProcessLinuxStartTicks, local.ProcessLinuxBootId), "the exact child is no longer running");
                            AssertEqual(local.ExitCode, completed.ExitCode, "the Admiral receives the OS exit result");
                        }
                    }
                }
                finally
                {
                    DeleteShippingRunnerFixture(root);
                }
            });

            await RunTest("HarborOwnedMission_ProvisionBriefCommitRestartDiffAndLandingStayOnTheRunner", HarborOwnedMission_ProvisionBriefCommitRestartDiffAndLandingStayOnTheRunner);

            await RunTest("HarborRestart_AdoptsExactMissionAttemptAfterRunnerProof", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        DateTime runnerStartedUtc = DateTime.UtcNow;
                        int admiralProcessId;
                        string jobId;
                        await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                        {
                            AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                            admiralProcessId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                            HarborLaunchRequest launch = await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                            jobId = launch.JobId;
                            await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 9005, ProcessStartedUtc = runnerStartedUtc }).ConfigureAwait(false);
                            await runner.SendAsync(Output(jobId, 0, "before restart")).ConfigureAwait(false);
                            AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "initial launch events accepted");
                        }

                        ProcessSupervisor.UnregisterSyntheticProcess(admiralProcessId);
                        MissionScope recoveryScope = MissionScope.CreateRecovery(harness, "hbr_a", scope);
                        using (recoveryScope)
                        {
                            TaskCompletionSource<bool> releaseLifecycle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            recoveryScope.Admiral.ExitHandler = _ => releaseLifecycle.Task;
                            AssertTrue(harness.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? pendingRestore), "the prior job is retained before restart");
                            await VerifyCancelledOwnershipRestoreAsync(recoveryScope.Handler, pendingRestore!).ConfigureAwait(false);
                            AdmiralInstance restarted = await harness.RestartAdmiralAsync(recoveryScope.Handler, recoveryScope.UseCoordinator).ConfigureAwait(false);
                            HarborJobRecord? uncertain = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                            AssertEqual(HarborJobStateEnum.Uncertain, uncertain!.State, "the unfinished mission job stays fenced during restart");
                            AssertEqual(HarborJobCoordinator.ReasonResumeUnconfirmed, uncertain.FailureReason, "the restart records an unconfirmed resume");
                            AssertEqual(scope.Mission.Id, uncertain.MissionId, "the durable job remains bound to its mission");
                            AssertTrue(uncertain.RunnerProcessStartedUtc.HasValue, "the durable runner process start time survives restart");
                            AssertEqual(1L, uncertain.NextOutputSequence, "the durable output cursor survives restart");

                            await using (FakeRunner reconnected = await harness.ConnectAsync(restarted, harness.OwnerA).ConfigureAwait(false))
                            {
                                AssertTrue((await reconnected.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner reconnects to the restarted Admiral");
                                HarborResumeJobInfo wrongProof = new HarborResumeJobInfo
                                {
                                    JobId = jobId,
                                    LaunchKey = uncertain.LaunchKey,
                                    ProcessId = 9005,
                                    ProcessStartedUtc = uncertain.RunnerProcessStartedUtc!.Value.AddSeconds(1),
                                    IsRunning = true,
                                    NextOutputSequence = 1
                                };
                                await reconnected.SendAsync(new HarborResumeRequest { Jobs = new List<HarborResumeJobInfo> { wrongProof } }).ConfigureAwait(false);
                                HarborResumeJobResult refused = (await reconnected.NextAsync<HarborResumeResponse>().ConfigureAwait(false)).Jobs[0];
                                AssertEqual(HarborResumeDecisionEnum.Reject, refused.Decision, "a different runner process start time is not adopted");

                                HarborResumeJobInfo unknownExitProof = new HarborResumeJobInfo
                                {
                                    JobId = jobId,
                                    LaunchKey = uncertain.LaunchKey,
                                    ProcessId = 9005,
                                    ProcessStartedUtc = uncertain.RunnerProcessStartedUtc!.Value,
                                    IsRunning = false,
                                    StopConfirmed = true,
                                    ExitCode = null,
                                    NextOutputSequence = 1
                                };
                                await reconnected.SendAsync(new HarborResumeRequest { Jobs = new List<HarborResumeJobInfo> { unknownExitProof } }).ConfigureAwait(false);
                                HarborResumeJobResult unknownExit = (await reconnected.NextAsync<HarborResumeResponse>().ConfigureAwait(false)).Jobs[0];
                                AssertEqual(HarborResumeDecisionEnum.Reject, unknownExit.Decision, "terminal proof without an exit code is not adopted");
                                AssertEqual("harbor_resume_exit_code_unknown", unknownExit.Reason, "unknown terminal exit code has a stable refusal reason");

                                HarborResumeJobInfo proof = new HarborResumeJobInfo
                                {
                                    JobId = jobId,
                                    LaunchKey = uncertain.LaunchKey,
                                    ProcessId = 9005,
                                    ProcessStartedUtc = uncertain.RunnerProcessStartedUtc!.Value,
                                    IsRunning = true,
                                    NextOutputSequence = 1
                                };
                                await reconnected.SendAsync(new HarborResumeRequest { Jobs = new List<HarborResumeJobInfo> { proof } }).ConfigureAwait(false);
                                HarborResumeJobResult adopted = (await reconnected.NextAsync<HarborResumeResponse>().ConfigureAwait(false)).Jobs[0];
                                AssertEqual(HarborResumeDecisionEnum.Replay, adopted.Decision, "the exact live runner attempt is adopted");
                                AssertEqual(1L, adopted.ReplayFromSequence, "resume starts at the durable output sequence");
                                HarborRunnerSession? currentSession;
                                AssertTrue(restarted.Registry.TryGetCurrent("hbr_a", out currentSession) && currentSession != null, "reconnected runner owns a current session");
                                HarborJobRecord? rebound = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                                AssertEqual(HarborJobStateEnum.Running, rebound!.State, "the adopted job returns to running state");
                                AssertEqual(currentSession!.Generation, rebound.SessionGeneration, "the durable job is bound to the current link generation");
                                AssertTrue(await reconnected.NoMessageAsync().ConfigureAwait(false), "restart does not send a duplicate launch");

                                RuntimeProcessExitEvidence? ownedEvidence = ProcessSupervisor.GetRuntimeExitEvidence(admiralProcessId);
                                AssertNotNull(ownedEvidence, "the admitted launch retains its trusted exit owner");
                                AssertFalse(ownedEvidence!.TryGetOutcome(out _, out _), "the live admitted launch is not finalized");
                                AssertTrue(restarted.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? ownedSnapshot), "the owned job snapshot exists");
                                HarborJobRecoveryResult duplicate = await recoveryScope.Handler.RestoreAsync(ownedSnapshot!).ConfigureAwait(false);
                                AssertEqual(HarborJobRecoveryDispositionEnum.Uncertain, duplicate.Disposition, "a duplicate observer cannot adopt the same process");
                                AssertTrue(Object.ReferenceEquals(ownedEvidence, ProcessSupervisor.GetRuntimeExitEvidence(admiralProcessId)),
                                    "rejected restoration cannot replace the admitted owner's evidence");
                                AssertFalse(ownedEvidence.TryGetOutcome(out _, out _), "rejected restoration cannot finalize the active owner's exit");

                                await reconnected.SendAsync(Output(jobId, 1, "after restart")).ConfigureAwait(false);
                                await reconnected.SendAsync(new HarborExited { JobId = jobId, ExitCode = 0 }).ConfigureAwait(false);
                                RecordedProcessExit exit = await recoveryScope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                                AssertEqual(admiralProcessId, exit.ProcessId, "confirmed completion reaches the restored lifecycle");
                                HarborJobRecord? completed = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                                AssertEqual(HarborJobStateEnum.Exited, completed!.State, "the durable job records the runner's confirmed result");
                                AssertFalse(completed!.CompletionDelivered, "the durable delivery acknowledgement waits for lifecycle handling");
                                releaseLifecycle.TrySetResult(true);
                                DateTime completionDeadline = DateTime.UtcNow.Add(Wait);
                                while (completed != null && !completed.CompletionDelivered && DateTime.UtcNow < completionDeadline)
                                {
                                    await Task.Delay(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
                                    completed = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                                }
                                AssertTrue(completed != null && completed.CompletionDelivered, "the lifecycle handles the result before the durable delivery acknowledgement");
                                AssertTrue(await reconnected.NoMessageAsync().ConfigureAwait(false), "resumed output and terminal result are accepted without another launch");
                                List<HarborJobRecord> missionJobs = await harness.Driver.HarborJobs.EnumerateAsync(new HarborJobQuery { MissionId = scope.Mission.Id, Limit = 10 }).ConfigureAwait(false);
                                AssertEqual(1, missionJobs.Count, "restart adoption keeps one durable chain for the mission");
                                AssertEqual(jobId, missionJobs[0].JobId, "the original durable job remains the mission attempt");
                            }
                        }
                    }
                }
            });

            await RunTest("HarborRestart_StopRequestedRunningProofGetsStopDecision", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        DateTime runnerStartedUtc = DateTime.UtcNow;
                        int admiralProcessId;
                        string jobId;
                        await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                        {
                            AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                            admiralProcessId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                            jobId = (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                            await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 9010, ProcessStartedUtc = runnerStartedUtc }).ConfigureAwait(false);
                            AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "runner start accepted");

                            HarborCommandResult stop = await harness.Coordinator.StopAsync(harness.OwnerA.Auth, jobId, 250).ConfigureAwait(false);
                            AssertTrue(stop.Accepted, stop.Reason);
                            AssertEqual(jobId, (await runner.NextAsync<HarborKillRequest>().ConfigureAwait(false)).JobId, "stop request reaches the runner");
                            HarborJobRecord? stopping = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                            AssertTrue(stopping!.StopRequested, "the stop intent is durable before disconnect");
                            await runner.CloseAsync().ConfigureAwait(false);
                        }

                        AssertTrue(await WaitUntilAsync(() => harness.Registry.SessionCount == 0).ConfigureAwait(false), "runner disconnected after stop request");
                        ProcessSupervisor.UnregisterSyntheticProcess(admiralProcessId);
                        MissionScope recoveryScope = MissionScope.CreateRecovery(harness, "hbr_a", scope);
                        using (recoveryScope)
                        {
                            AdmiralInstance restarted = await harness.RestartAdmiralAsync(recoveryScope.Handler, recoveryScope.UseCoordinator).ConfigureAwait(false);
                            HarborJobRecord? uncertain = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                            AssertTrue(uncertain != null && uncertain.StopRequested, "restart preserves the outstanding stop intent");
                            AssertEqual(HarborJobStateEnum.Uncertain, uncertain!.State, "the unproven process remains fenced until its runner reconnects");

                            await using (FakeRunner reconnected = await harness.ConnectAsync(restarted, harness.OwnerA).ConfigureAwait(false))
                            {
                                AssertTrue((await reconnected.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner reconnects to the restarted Admiral");
                                HarborResumeJobInfo proof = new HarborResumeJobInfo
                                {
                                    JobId = jobId,
                                    LaunchKey = uncertain.LaunchKey,
                                    ProcessId = 9010,
                                    ProcessStartedUtc = uncertain.RunnerProcessStartedUtc!.Value,
                                    IsRunning = true,
                                    NextOutputSequence = 0
                                };
                                await reconnected.SendAsync(new HarborResumeRequest { Jobs = new List<HarborResumeJobInfo> { proof } }).ConfigureAwait(false);
                                HarborResumeJobResult decision = (await reconnected.NextAsync<HarborResumeResponse>().ConfigureAwait(false)).Jobs[0];
                                AssertEqual(HarborResumeDecisionEnum.Stop, decision.Decision, "a running process receives Stop when its durable stop intent survived restart");
                                AssertEqual("harbor_stop_requested", decision.Reason, "the resume response explains the stop decision");
                                HarborJobRecord? resumed = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                                AssertEqual(HarborJobStateEnum.Stopping, resumed!.State, "the adopted job remains in stopping state");
                                AssertTrue(resumed.StopRequested, "the durable stop intent remains set after adoption");
                                AssertFalse(resumed.StopConfirmed, "a running proof does not claim that the process stopped");
                                AssertTrue(await reconnected.NoMessageAsync().ConfigureAwait(false), "recovery does not send a duplicate launch");
                            }
                        }
                    }
                }
            });

            await RunTest("HarborRestart_LifecycleFailureDoesNotAcknowledgeCompletion", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        DateTime runnerStartedUtc = DateTime.UtcNow;
                        int admiralProcessId;
                        string jobId;
                        await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                        {
                            AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                            admiralProcessId = await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
                            jobId = (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                            await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 9006, ProcessStartedUtc = runnerStartedUtc }).ConfigureAwait(false);
                            AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "initial launch accepted");
                        }

                        ProcessSupervisor.UnregisterSyntheticProcess(admiralProcessId);
                        MissionScope recoveryScope = MissionScope.CreateRecovery(harness, "hbr_a", scope);
                        using (recoveryScope)
                        {
                            TaskCompletionSource<bool> exitHandlerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            recoveryScope.Admiral.ExitHandler = _ =>
                            {
                                exitHandlerEntered.TrySetResult(true);
                                return Task.FromException(new InvalidOperationException("fixture_lifecycle_failure"));
                            };
                            AdmiralInstance restarted = await harness.RestartAdmiralAsync(recoveryScope.Handler, recoveryScope.UseCoordinator).ConfigureAwait(false);
                            HarborJobRecord? uncertain = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                            AssertTrue(uncertain != null && uncertain.RunnerProcessStartedUtc.HasValue, "runner attempt identity survives restart");

                            await using (FakeRunner reconnected = await harness.ConnectAsync(restarted, harness.OwnerA).ConfigureAwait(false))
                            {
                                AssertTrue((await reconnected.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner reconnects");
                                HarborResumeJobInfo proof = new HarborResumeJobInfo
                                {
                                    JobId = jobId,
                                    LaunchKey = uncertain!.LaunchKey,
                                    ProcessId = 9006,
                                    ProcessStartedUtc = uncertain.RunnerProcessStartedUtc!.Value,
                                    IsRunning = true,
                                    NextOutputSequence = 0
                                };
                                await reconnected.SendAsync(new HarborResumeRequest { Jobs = new List<HarborResumeJobInfo> { proof } }).ConfigureAwait(false);
                                HarborResumeJobResult adopted = (await reconnected.NextAsync<HarborResumeResponse>().ConfigureAwait(false)).Jobs[0];
                                AssertEqual(HarborResumeDecisionEnum.Replay, adopted.Decision, "the exact live attempt is adopted before its terminal result");
                                await reconnected.SendAsync(new HarborExited { JobId = jobId, ExitCode = 1 }).ConfigureAwait(false);
                                AssertTrue(await reconnected.NoMessageAsync().ConfigureAwait(false), "runner terminal report is accepted");
                            }

                            RecordedProcessExit exit = await recoveryScope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                            AssertEqual(admiralProcessId, exit.ProcessId, "restored lifecycle invokes the Admiral handler");
                            AssertTrue(await exitHandlerEntered.Task.WaitAsync(Wait).ConfigureAwait(false), "the lifecycle failure reaches the completion boundary");
                            AssertTrue(await WaitUntilAsync(() => !ProcessSupervisor.IsSyntheticProcessAlive(admiralProcessId)).ConfigureAwait(false),
                                "restored observer exits after the lifecycle handler fails");
                            AssertTrue(await WaitUntilAsync(() => restarted.Coordinator.ObserverFailureCount > 0).ConfigureAwait(false),
                                "coordinator observes the failed lifecycle completion before the assertion");
                            HarborJobRecord? terminal = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                            AssertEqual(HarborJobStateEnum.Exited, terminal!.State, "runner's confirmed exit remains durable");
                            AssertTrue(terminal.StopConfirmed, "runner proof confirms the process stopped");
                            AssertFalse(terminal.CompletionDelivered, "a failed lifecycle handler leaves completion unacknowledged");
                        }
                    }
                }
            });

            await RunTest("HarborRestart_StopConfirmationRequiresGlobalAdministratorAndExactEvidence", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    Principal globalAdministrator = await harness.AddPrincipalAsync(harness.OwnerA.TenantId, "global-admin", false, true).ConfigureAwait(false);
                    string jobId;
                    DateTime runnerStartedUtc = DateTime.UtcNow;
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        HarborLaunchResult launch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "operator-stop-confirmation", Plan()).ConfigureAwait(false);
                        AssertTrue(launch.Accepted, launch.Reason);
                        jobId = launch.JobId;
                        await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                        await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 9007, ProcessStartedUtc = runnerStartedUtc }).ConfigureAwait(false);
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "runner start accepted");
                    }

                    using (MissionScope recoveryScope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        AdmiralInstance restarted = await harness.RestartAdmiralAsync(recoveryScope.Handler, recoveryScope.UseCoordinator).ConfigureAwait(false);
                        HarborJobRecord? uncertain = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertTrue(uncertain != null && uncertain.RunnerProcessStartedUtc.HasValue, "the uncertain runner process identity is durable");
                        HarborJobConfirmStoppedRequest wrongIdentity = new HarborJobConfirmStoppedRequest
                        {
                            ExpectedRevision = uncertain!.Revision,
                            EnrollmentGeneration = uncertain.EnrollmentGeneration,
                            RunnerProcessId = uncertain.ProcessId,
                            RunnerProcessStartedUtc = uncertain.RunnerProcessStartedUtc.Value.AddSeconds(1),
                            Evidence = "host_confirmed_process_absent"
                        };
                        HttpReply tenantAdminReply = await harness.RequestAsync(harness.Administrator, System.Net.Http.HttpMethod.Post,
                            "api/v1/harbor-runners/jobs/" + jobId + "/confirm-stopped", wrongIdentity, restarted).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Forbidden, tenantAdminReply.StatusCode, "a tenant administrator cannot confirm a stopped process");
                        HarborJobRecord? afterDenial = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Uncertain, afterDenial!.State, "a denied confirmation keeps the durable capacity hold");

                        HttpReply wrongIdentityReply = await harness.RequestAsync(globalAdministrator, System.Net.Http.HttpMethod.Post,
                            "api/v1/harbor-runners/jobs/" + jobId + "/confirm-stopped", wrongIdentity, restarted).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Conflict, wrongIdentityReply.StatusCode, "global admin must present the exact runner process identity");
                        HarborJobRecord? afterMismatch = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Uncertain, afterMismatch!.State, "a mismatched process proof keeps the job fenced");

                        HarborJobConfirmStoppedRequest exactIdentity = new HarborJobConfirmStoppedRequest
                        {
                            ExpectedRevision = afterMismatch.Revision,
                            EnrollmentGeneration = afterMismatch.EnrollmentGeneration,
                            RunnerProcessId = afterMismatch.ProcessId,
                            RunnerProcessStartedUtc = afterMismatch.RunnerProcessStartedUtc,
                            Evidence = "host_confirmed_process_absent"
                        };
                        HttpReply accepted = await harness.RequestAsync(globalAdministrator, System.Net.Http.HttpMethod.Post,
                            "api/v1/harbor-runners/jobs/" + jobId + "/confirm-stopped", exactIdentity, restarted).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, accepted.StatusCode, "global admin can confirm a stopped job with exact named evidence");
                        HarborJobConfirmStoppedResponse? confirmation = JsonSerializer.Deserialize<HarborJobConfirmStoppedResponse>(accepted.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        AssertTrue(confirmation != null && confirmation.Confirmed, "the route returns a confirmed result");
                        HarborJobRecord? confirmed = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Failed, confirmed!.State, "confirmed job becomes terminal");
                        AssertTrue(confirmed.StopConfirmed && confirmed.CompletionDelivered, "confirmed stop is durable and delivered");
                    }
                }
            });

            await RunTest("HarborDockReclaim_LegacyUnboundMissionJobStaysFencedUntilOperatorConfirmation", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    Principal globalAdministrator = await harness.AddPrincipalAsync(harness.OwnerA.TenantId, "legacy-stop-admin", false, true).ConfigureAwait(false);
                    using (MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false))
                    {
                        scope.Mission.Status = MissionStatusEnum.Pending;
                        scope.Mission.AssignmentState = MissionAssignmentStateEnum.Pending;
                        scope.Mission.CaptainId = null;
                        scope.Mission.DockId = null;
                        scope.Mission.HarborJobId = null;
                        scope.Mission.ProcessId = null;
                        scope.Mission.ProcessStartedUtc = null;
                        scope.Mission.StartedUtc = null;
                        scope.Mission.CompletedUtc = null;
                        await harness.Driver.Missions.UpdateAsync(scope.Mission).ConfigureAwait(false);
                        scope.Captain.State = CaptainStateEnum.Idle;
                        scope.Captain.CurrentMissionId = null;
                        scope.Captain.CurrentDockId = null;
                        scope.Captain.ProcessId = null;
                        scope.Captain.ProcessStartedUtc = null;
                        await harness.Driver.Captains.UpdateAsync(scope.Captain).ConfigureAwait(false);
                        scope.Dock.WorktreePath = null;
                        await harness.Driver.Docks.UpdateAsync(scope.Dock).ConfigureAwait(false);

                        DateTime nowUtc = DateTime.UtcNow;
                        HarborJobRecord legacyJob = new HarborJobRecord
                        {
                            JobId = "hjob_legacy_" + Guid.NewGuid().ToString("N"),
                            RunnerId = "hbr_a",
                            LaunchKey = "legacy-mission:" + scope.Mission.Id,
                            TenantId = harness.OwnerA.TenantId,
                            UserId = harness.OwnerA.UserId,
                            EnrollmentGeneration = 1,
                            SessionGeneration = 1,
                            State = HarborJobStateEnum.Uncertain,
                            ProcessId = 9018,
                            MissionId = scope.Mission.Id,
                            CaptainId = scope.Captain.Id,
                            DockId = null,
                            AdmiralProcessId = ProcessSupervisor.AllocateSyntheticProcessId(),
                            AdmiralProcessStartedUtc = nowUtc,
                            RunnerProcessStartedUtc = nowUtc.AddSeconds(-5),
                            StopConfirmed = false,
                            CompletionDelivered = false,
                            FailureReason = HarborJobCoordinator.ReasonResumeUnconfirmed,
                            Revision = 1,
                            CreatedUtc = nowUtc,
                            LastUpdateUtc = nowUtc
                        };
                        await harness.Driver.HarborJobs.CreateAsync(legacyJob).ConfigureAwait(false);

                        MissionScope recoveryScope = MissionScope.CreateRecovery(harness, "hbr_a", scope);
                        using (recoveryScope)
                        {
                            AdmiralInstance restarted = await harness.RestartAdmiralAsync(recoveryScope.Handler, recoveryScope.UseCoordinator).ConfigureAwait(false);
                            LoggingModule logging = new LoggingModule();
                            logging.Settings.EnableConsole = false;
                            ArmadaSettings settings = new ArmadaSettings();
                            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_harbor_legacy_reclaim_" + Guid.NewGuid().ToString("N"));
                            DockService docks = new DockService(logging, harness.Driver, settings, new GitService(logging, null, harness.Driver));

                            await docks.ReclaimAsync(scope.Dock.Id, scope.Dock.TenantId).ConfigureAwait(false);
                            Dock? heldDock = await harness.Driver.Docks.ReadAsync(scope.Dock.TenantId, scope.Dock.Id).ConfigureAwait(false);
                            AssertTrue(heldDock!.Active, "reclaim holds the actual dock when an unbound legacy mission job has no stop proof");

                            HarborJobRecord? uncertain = await harness.Driver.HarborJobs.ReadAsync(legacyJob.JobId).ConfigureAwait(false);
                            AssertTrue(uncertain != null && uncertain.ProcessId == 9018, "legacy runner process identity survives recovery");
                            HarborJobConfirmStoppedRequest confirmation = new HarborJobConfirmStoppedRequest
                            {
                                ExpectedRevision = uncertain!.Revision,
                                EnrollmentGeneration = uncertain.EnrollmentGeneration,
                                RunnerProcessId = uncertain.ProcessId,
                                RunnerProcessStartedUtc = uncertain.RunnerProcessStartedUtc,
                                Evidence = "host_confirmed_process_absent"
                            };
                            HttpReply confirmedReply = await harness.RequestAsync(globalAdministrator, System.Net.Http.HttpMethod.Post,
                                "api/v1/harbor-runners/jobs/" + legacyJob.JobId + "/confirm-stopped", confirmation, restarted).ConfigureAwait(false);
                            AssertEqual(HttpStatusCode.OK, confirmedReply.StatusCode, "global admin confirms the exact legacy runner process after mission and captain unbind");
                            HarborJobRecord? confirmed = await harness.Driver.HarborJobs.ReadAsync(legacyJob.JobId).ConfigureAwait(false);
                            AssertTrue(confirmed!.StopConfirmed && confirmed.CompletionDelivered, "operator confirmation accounts the legacy stop");

                            await docks.ReclaimAsync(scope.Dock.Id, scope.Dock.TenantId).ConfigureAwait(false);
                            Dock? reclaimedDock = await harness.Driver.Docks.ReadAsync(scope.Dock.TenantId, scope.Dock.Id).ConfigureAwait(false);
                            AssertFalse(reclaimedDock!.Active, "reclaim proceeds after the legacy process stop is confirmed");
                            HarborJobSnapshot? recoveredSnapshot;
                            AssertTrue(restarted.Coordinator.TryGetJob(legacyJob.JobId, out recoveredSnapshot), "the confirmed legacy job remains readable after recovery");
                        }
                    }
                }
            });

            await RunTest("OperatorJobSurface_AppliesTheRunnerAuthorityRule", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    string otherTenantId = await harness.AddTenantAsync().ConfigureAwait(false);
                    Principal otherTenantAdministrator = await harness.AddPrincipalAsync(otherTenantId, "other-tenant-admin", true, false).ConfigureAwait(false);
                    await using (FakeRunner runnerA = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    await using (FakeRunner runnerB = await harness.ConnectAsync(harness.OwnerB).ConfigureAwait(false))
                    {
                        AssertTrue((await runnerA.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner A connected");
                        AssertTrue((await runnerB.HandshakeAsync("hbr_b").ConfigureAwait(false)).Accepted, "runner B connected");
                        HarborLaunchResult jobA = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "surface-a", Plan()).ConfigureAwait(false);
                        HarborLaunchResult jobB = await harness.Coordinator.LaunchAsync(harness.OwnerB.Auth, "hbr_b", "surface-b", Plan()).ConfigureAwait(false);
                        AssertTrue(jobA.Accepted && jobB.Accepted, "both jobs launched");
                        await runnerA.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                        await runnerB.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);

                        AssertEqual(HttpStatusCode.Unauthorized, (await harness.RequestAsync(null, System.Net.Http.HttpMethod.Get, "api/v1/harbor-runners/jobs").ConfigureAwait(false)).StatusCode, "anonymous list refused");
                        AssertEqual("surface-a", String.Join(",", await harness.ListLaunchKeysAsync(harness.OwnerA).ConfigureAwait(false)), "an owner sees only its own jobs");
                        AssertEqual("surface-a,surface-b", String.Join(",", await harness.ListLaunchKeysAsync(harness.Administrator).ConfigureAwait(false)), "a tenant administrator sees the owners it administers");
                        AssertEqual(String.Empty, String.Join(",", await harness.ListLaunchKeysAsync(otherTenantAdministrator).ConfigureAwait(false)), "another tenant's administrator sees nothing");
                        AssertEqual(HttpStatusCode.NotFound, (await harness.RequestAsync(harness.OwnerB, System.Net.Http.HttpMethod.Get, "api/v1/harbor-runners/jobs/" + jobA.JobId).ConfigureAwait(false)).StatusCode, "another owner's job reads as not found");
                        AssertEqual(HttpStatusCode.OK, (await harness.RequestAsync(harness.Administrator, System.Net.Http.HttpMethod.Get, "api/v1/harbor-runners/jobs/" + jobA.JobId).ConfigureAwait(false)).StatusCode, "the tenant administrator reads the job");

                        AssertEqual(HttpStatusCode.Forbidden, (await harness.RequestAsync(harness.OwnerA, System.Net.Http.HttpMethod.Post, "api/v1/harbor-runners/jobs/" + jobA.JobId + "/stop").ConfigureAwait(false)).StatusCode, "stop needs the tenant administrator level, as enrollment does");
                        AssertEqual(HttpStatusCode.NotFound, (await harness.RequestAsync(otherTenantAdministrator, System.Net.Http.HttpMethod.Post, "api/v1/harbor-runners/jobs/" + jobA.JobId + "/stop").ConfigureAwait(false)).StatusCode, "another tenant's administrator cannot stop");
                        AssertTrue(await runnerA.NoMessageAsync().ConfigureAwait(false), "refused stops send nothing");
                        AssertEqual(HttpStatusCode.OK, (await harness.RequestAsync(harness.Administrator, System.Net.Http.HttpMethod.Post, "api/v1/harbor-runners/jobs/" + jobA.JobId + "/stop").ConfigureAwait(false)).StatusCode, "the tenant administrator stops the job");
                        AssertEqual(jobA.JobId, (await runnerA.NextAsync<HarborKillRequest>().ConfigureAwait(false)).JobId, "the REST stop reaches the owning runner");
                        AssertTrue(await runnerB.NoMessageAsync().ConfigureAwait(false), "the stop never reaches another runner");

                        Dictionary<string, Func<JsonElement?, Task<object>>> tools = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                        McpHarborJobTools.Register((name, description, schema, handler) => tools[name] = handler, harness.Jobs);
                        AssertTrue(McpToolAccessPolicy.IsAllowed(harness.OwnerA.Auth, McpHarborJobTools.ListToolName), "job tools are caller scoped");
                        string ownerBRead;
                        using (McpCallerContext.Begin(harness.OwnerB.Auth))
                            ownerBRead = JsonSerializer.Serialize(await tools[McpHarborJobTools.GetToolName](JsonSerializer.SerializeToElement(new { jobId = jobA.JobId })).ConfigureAwait(false));
                        AssertTrue(ownerBRead.Contains("harbor_job_unknown"), "MCP read of another owner's job reads as unknown: " + ownerBRead);
                        string ownerAStop;
                        using (McpCallerContext.Begin(harness.OwnerA.Auth))
                            ownerAStop = JsonSerializer.Serialize(await tools[McpHarborJobTools.StopToolName](JsonSerializer.SerializeToElement(new { jobId = jobB.JobId })).ConfigureAwait(false));
                        AssertTrue(ownerAStop.Contains(McpHarborJobTools.AdministratorRequiredReason), "MCP stop needs the tenant administrator level: " + ownerAStop);
                        string administratorList;
                        using (McpCallerContext.Begin(harness.Administrator.Auth))
                            administratorList = JsonSerializer.Serialize(await tools[McpHarborJobTools.ListToolName](null).ConfigureAwait(false));
                        AssertTrue(administratorList.Contains(jobA.JobId!) && administratorList.Contains(jobB.JobId!), "MCP list applies the same rule as REST");
                    }
                }
            });
        }

        private static async Task<string> LaunchRefusalAsync(MissionScope scope)
        {
            try
            {
                await scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock).ConfigureAwait(false);
            }
            catch (HarborLaunchException exception)
            {
                return exception.Reason;
            }
            throw new Exception("The routed launch was not refused.");
        }

        private static async Task<string> ReadWhenContainsAsync(string path, string expected)
        {
            DateTime deadline = DateTime.UtcNow + Wait;
            string text = String.Empty;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        text = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }
                    if (text.Contains(expected)) return text;
                }
                await Task.Delay(25).ConfigureAwait(false);
            }
            throw new Exception("The mission log never contained \"" + expected + "\". Log: " + text);
        }

        private async Task RunHarborCapacityAssignmentAsync(bool fillAtLaunch)
        {
            string fixtureRoot = Path.Combine(Path.GetTempPath(), "armada_harbor_capacity_" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, await harness.EnrollAsync(harness.Administrator, "hbr_a", harness.OwnerA).ConfigureAwait(false), "runner enrolled");
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a", 1).ConfigureAwait(false)).Accepted, "single-slot runner connected");
                        HarborLaunchResult? occupying = null;
                        if (!fillAtLaunch)
                        {
                            occupying = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "capacity-occupier", Plan()).ConfigureAwait(false);
                            AssertTrue(occupying.Accepted, "real coordinator admitted the occupying job: " + occupying.Reason);
                            await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                        }

                        ArmadaSettings settings = new ArmadaSettings();
                        settings.DocksDirectory = Path.Combine(fixtureRoot, "docks");
                        settings.ReposDirectory = Path.Combine(fixtureRoot, "repos");
                        settings.LogDirectory = Path.Combine(fixtureRoot, "logs");
                        settings.Harbor.Enabled = true;
                        StubGitService git = new StubGitService();
                        git.CreateWorktreeDirectories = true;
                        LoggingModule logging = new LoggingModule();
                        logging.Settings.EnableConsole = false;
                        DockService docks = new DockService(logging, harness.Driver, settings, git);
                        CaptainService captains = new CaptainService(logging, harness.Driver, settings, git, docks);
                        MissionService missions = new MissionService(logging, harness.Driver, settings, docks, captains,
                            git: git,
                            resourcePressureAdmission: global::Test.Shared.Infrastructure.TestResourcePressure.Unconstrained(settings));
                        RecordingAdmiral admiral = RecordingAdmiral.Create();
                        AgentLifecycleHandler lifecycle = new AgentLifecycleHandler(
                            logging,
                            harness.Driver,
                            settings,
                            new AgentRuntimeFactory(logging),
                            admiral.Service,
                            new MessageTemplateService(logging),
                            null,
                            null,
                            (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) => Task.CompletedTask);
                        lifecycle.SetHarborHost(new HarborMissionExecutor(harness.Coordinator, logging));
                        bool racingOccupierCreated = false;
                        captains.OnLaunchAgent = async (assignedCaptain, assignedMission, assignedDock) =>
                        {
                            if (fillAtLaunch && !racingOccupierCreated)
                            {
                                HarborLaunchResult racedOccupier = await harness.Coordinator.LaunchAsync(
                                    harness.OwnerA.Auth, "hbr_a", "capacity-race-occupier", Plan()).ConfigureAwait(false);
                                AssertTrue(racedOccupier.Accepted, "real coordinator admits the racing slot occupant: " + racedOccupier.Reason);
                                occupying = racedOccupier;
                                racingOccupierCreated = true;
                                await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                            }
                            return await lifecycle.HandleLaunchAgentAsync(assignedCaptain, assignedMission, assignedDock).ConfigureAwait(false);
                        };

                        Vessel vessel = new Vessel("harbor-capacity-vessel", "https://example.invalid/harbor-capacity.git")
                        {
                            TenantId = harness.OwnerA.TenantId,
                            UserId = harness.OwnerA.UserId,
                            DefaultBranch = "main",
                            AllowConcurrentMissions = true
                        };
                        vessel = await harness.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                        Captain captain = new Captain("harbor-capacity-captain")
                        {
                            State = CaptainStateEnum.Idle,
                            TenantId = harness.OwnerA.TenantId,
                            UserId = harness.OwnerA.UserId,
                            AllowedPersonas = "[\"Worker\"]"
                        };
                        captain = await harness.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                        settings.Harbor.MissionRoutes.Add(new HarborMissionRoute { RunnerId = "hbr_a", CaptainId = captain.Id });
                        Mission mission = new Mission("Wait for Harbor capacity", "Do not provision while the selected runner has no free job slot.")
                        {
                            TenantId = harness.OwnerA.TenantId,
                            UserId = harness.OwnerA.UserId,
                            VesselId = vessel.Id,
                            Persona = "Worker",
                            Status = MissionStatusEnum.Pending,
                            AssignmentState = MissionAssignmentStateEnum.Pending
                        };
                        mission = await harness.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        List<string> probedRunners = new List<string>();
                        List<bool> probeResults = new List<bool>();
                        missions.HarborCapacityProbe = async (caller, runnerId, token) =>
                        {
                            probedRunners.Add(runnerId);
                            bool full = await harness.Coordinator.IsRunnerAtCapacityAsync(caller, runnerId, token).ConfigureAwait(false);
                            probeResults.Add(full);
                            return full;
                        };

                        AssertFalse(await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "full Harbor defers assignment");
                        Mission? held = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNotNull(held, "held mission is durable");
                        AssertEqual(MissionStatusEnum.Pending, held!.Status, "mission remains pending");
                        AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, held.AssignmentState, "capacity uses the existing pending wait state");
                        AssertEqual(MissionAssignmentHold.HarborRunnerCapacity, held.AssignmentHoldReason, "hold names the capacity condition");
                        AssertEqual("hbr_a", held.AssignmentHoldEntityId, "hold names the configured runner");
                        AssertNull(held.CaptainId, "capacity wait does not claim a captain");
                        AssertNull(held.DockId, "capacity wait does not create a dock");
                        AssertEqual(CaptainStateEnum.Idle, (await harness.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!.State,
                            "capacity wait leaves the captain idle");
                        List<Dock> heldDocks = await harness.Driver.Docks.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                        if (fillAtLaunch)
                        {
                            AssertTrue(racingOccupierCreated, "launch callback filled the last runner slot after an available probe");
                            AssertFalse(probeResults[0], "real capacity probe saw the runner as available before the race");
                            AssertTrue(git.WorktreeCalls.Count > 0, "the late capacity race occurs after worktree provisioning");
                            AssertNull(held.DockId, "late capacity rollback clears the mission dock pointer");
                            AssertEqual(CaptainStateEnum.Idle, (await harness.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!.State,
                                "late capacity rollback releases the captain");
                            foreach (Dock heldDock in heldDocks)
                                AssertFalse(heldDock.Active, "late capacity rollback reclaims its dock");
                        }
                        else
                        {
                            AssertEqual(0, heldDocks.Count, "capacity wait does not provision a dock");
                            AssertEqual(0, git.WorktreeCalls.Count, "capacity wait does not create a worktree");
                        }
                        List<Signal> capacitySignals = await harness.Driver.Signals.EnumerateRecentAsync(50).ConfigureAwait(false);
                        AssertFalse(capacitySignals.Exists(item => item.Type == SignalTypeEnum.Error && item.FromCaptainId == captain.Id),
                            "capacity wait does not send a launch-error signal");
                        int provisionedWorktreeCalls = git.WorktreeCalls.Count;

                        List<ArmadaEvent> firstHeldEvents = await harness.Driver.Events
                            .EnumerateByTypeAsync(MissionAssignmentHold.HeldEventType).ConfigureAwait(false);
                        int firstMissionHoldCount = 0;
                        foreach (ArmadaEvent item in firstHeldEvents)
                            if (String.Equals(item.MissionId, mission.Id, StringComparison.Ordinal)) firstMissionHoldCount++;
                        AssertEqual(1, firstMissionHoldCount, "first capacity wait records one hold event");

                        AssertFalse(await missions.TryAssignAsync(held, vessel).ConfigureAwait(false), "repeated full-capacity pass still waits");
                        Mission? heldAgain = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionAssignmentHold.HarborRunnerCapacity, heldAgain!.AssignmentHoldReason, "repeat preserves the same hold");
                        List<ArmadaEvent> repeatedHeldEvents = await harness.Driver.Events
                            .EnumerateByTypeAsync(MissionAssignmentHold.HeldEventType).ConfigureAwait(false);
                        int repeatedMissionHoldCount = 0;
                        foreach (ArmadaEvent item in repeatedHeldEvents)
                            if (String.Equals(item.MissionId, mission.Id, StringComparison.Ordinal)) repeatedMissionHoldCount++;
                        AssertEqual(firstMissionHoldCount, repeatedMissionHoldCount, "repeat does not churn hold events");
                        AssertEqual(provisionedWorktreeCalls, git.WorktreeCalls.Count,
                            "repeat does not provision another worktree while capacity is full");
                        AssertEqual(0, (await harness.Driver.Events
                            .EnumerateByTypeAsync(MissionAssignmentHold.ReleasedEventType).ConfigureAwait(false))
                            .FindAll(item => String.Equals(item.MissionId, mission.Id, StringComparison.Ordinal)).Count,
                            "repeat does not release the hold while the runner is full");
                        AssertEqual("hbr_a", probedRunners[0], "assignment probes the captain's configured runner");
                        AssertEqual("hbr_a", probedRunners[1], "repeated assignment probes the same runner");

                        AssertNotNull(occupying, "a real job occupies the runner before retry");
                        await runner.SendAsync(new HarborExited { JobId = occupying!.JobId!, ExitCode = 0 }).ConfigureAwait(false);
                        await occupying.Completion!.WaitAsync(Wait).ConfigureAwait(false);
                        AssertFalse(await harness.Coordinator.IsRunnerAtCapacityAsync(harness.OwnerA.Auth, "hbr_a").ConfigureAwait(false),
                            "the real coordinator reports capacity after the occupied job exits");

                        AssertTrue(await missions.TryAssignAsync(heldAgain, vessel).ConfigureAwait(false),
                            "mission assignment resumes after the selected runner has a free slot");
                        Mission? resumedMission = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNotNull(resumedMission, "resumed mission is durable");
                        AssertEqual(MissionStatusEnum.InProgress, resumedMission!.Status, "the mission starts after capacity becomes available");
                        AssertNull(resumedMission.AssignmentHoldReason, "successful assignment clears the capacity hold");
                        AssertNull(resumedMission.AssignmentHoldEntityId, "successful assignment clears the runner identity from the hold");
                        AssertEqual("hbr_a", probedRunners[2], "resumed assignment still probes the configured runner");
                        HarborLaunchRequest missionLaunch = await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
                        AssertTrue(harness.Coordinator.TryGetJob(missionLaunch.JobId, out HarborJobSnapshot? missionJob),
                            "resumed mission creates a coordinator job");
                        AssertEqual(mission.Id, missionJob!.MissionId, "runner job is bound to the resumed mission");
                        AssertEqual(captain.Id, missionJob.CaptainId, "runner job keeps the assigned captain");
                        AssertEqual("hbr_a", missionJob.RunnerId, "resumed mission job is bound to the configured runner");
                        AssertEqual(harness.OwnerA.TenantId, missionJob.OwnerTenantId, "mission job keeps its owner tenant");
                        AssertEqual(harness.OwnerA.UserId, missionJob.OwnerUserId, "mission job keeps its owner user");
                        AssertTrue(harness.Registry.TryGetCurrent("hbr_a", out HarborRunnerSession? currentSession),
                            "configured runner still has a current session");
                        AssertEqual(currentSession!.Generation, missionJob.SessionGeneration,
                            "mission job is bound to the current runner generation");
                        AssertEqual(currentSession.EnrollmentGeneration, missionJob.EnrollmentGeneration,
                            "mission job keeps the enrollment generation for its current session");
                        List<ArmadaEvent> releasedEvents = await harness.Driver.Events
                            .EnumerateByTypeAsync(MissionAssignmentHold.ReleasedEventType).ConfigureAwait(false);
                        int missionReleaseCount = 0;
                        foreach (ArmadaEvent item in releasedEvents)
                            if (String.Equals(item.MissionId, mission.Id, StringComparison.Ordinal)) missionReleaseCount++;
                        AssertEqual(1, missionReleaseCount, "the capacity hold releases once when assignment resumes");

                        await runner.SendAsync(new HarborExited { JobId = missionLaunch.JobId!, ExitCode = 0 }).ConfigureAwait(false);
                        RecordedProcessExit missionExit = await admiral.NextExitAsync(Wait).ConfigureAwait(false);
                        AssertEqual(mission.Id, missionExit.MissionId, "runner exit completes the assigned mission lifecycle");
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true);
                }
                catch (IOException exception)
                {
                    Console.WriteLine("Harbor capacity fixture cleanup failed: " + exception.Message);
                }
                catch (UnauthorizedAccessException exception)
                {
                    Console.WriteLine("Harbor capacity fixture cleanup failed: " + exception.Message);
                }
            }
        }

        private static HarborLaunchRequest ShippingRunnerPlan(string root, string mode, params string[] arguments)
        {
            List<string> resolved = new List<string> { typeof(global::Armada.Test.Unit.Program).Assembly.Location, "--harbor-runner-child", mode };
            resolved.AddRange(arguments);
            return new HarborLaunchRequest
            {
                Runtime = "fixture", WorkingDirectory = Path.Combine(root, "work"), Arguments = resolved,
                Prompt = "private runner prompt " + Guid.NewGuid().ToString("N"), PromptViaStdin = true,
                Environment = new Dictionary<string, string> { ["CLAUDE_CODE_DISABLE_NONINTERACTIVE_HINT"] = "1" }
            };
        }

        private async Task HarborOwnedMission_ProvisionBriefCommitRestartDiffAndLandingStayOnTheRunner()
        {
            string root = Path.Combine(Path.GetTempPath(), "armada_harbor_owned_e2e_" + Guid.NewGuid().ToString("N"));
            string repository = Path.Combine(root, "repository.git");
            string seed = Path.Combine(root, "seed");
            string primary = Path.Combine(root, "primary");
            string checkoutRoot = Path.Combine(root, "checkouts");
            string state = Path.Combine(root, "state");
            string work = Path.Combine(root, "work");
            string release = Path.Combine(work, "release");
            string counter = Path.Combine(work, "launches");
            const string grantId = "grant_owned_e2e";
            string token = "e2e-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(checkoutRoot);
                Directory.CreateDirectory(state);
                Directory.CreateDirectory(work);
                await InitializeHarborOwnedRepositoryAsync(root, repository, seed, primary).ConfigureAwait(false);

                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, await harness.EnrollAsync(harness.Administrator, "hbr_a", harness.OwnerA).ConfigureAwait(false), "runner is enrolled for the repository owner");
                    Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("Owned Harbor fixture", repository)
                    {
                        TenantId = harness.OwnerA.TenantId,
                        UserId = harness.OwnerA.UserId,
                        DefaultBranch = "main",
                        LandingMode = LandingModeEnum.LocalMerge
                    }).ConfigureAwait(false);
                    HarborRepositoryGrant grant = new HarborRepositoryGrant
                    {
                        Id = grantId, VesselId = vessel.Id, TenantId = harness.OwnerA.TenantId, UserId = harness.OwnerA.UserId,
                        RepositoryPath = repository, CheckoutRoot = checkoutRoot, WorkingDirectory = primary, RepositoryUrl = repository,
                        VerificationCommands = new Dictionary<string, string> { ["head"] = "git rev-parse HEAD" }
                    };
                    async Task ConfigureConsole(ShippingHarborConsole console)
                    {
                        AssertTrue(await WaitUntilAsync(() => console.HasExited || harness.Registry.SessionCount == 1).ConfigureAwait(false), "shipping console connects");
                        AssertFalse(console.HasExited, console.Diagnostics());
                    }

                    HarborRepositoryGrant configuredGrant = grant;
                    await using (ShippingHarborConsole console = new ShippingHarborConsole(root,
                        "ws://127.0.0.1:" + harness.Primary.Port + "/harbor/link", harness.OwnerA.BearerToken,
                        configure: settings =>
                        {
                            ConfigureOwnedRunnerSettings(settings, checkoutRoot, configuredGrant);
                        }))
                    {
                        await ConfigureConsole(console).ConfigureAwait(false);
                        OwnedMissionRun run = await StartOwnedMissionRunAsync(harness, vessel, grantId, checkoutRoot, release, counter, token).ConfigureAwait(false);
                        AssertTrue(await WaitUntilAsync(() => run.Observer.Output.Contains("before offline", StringComparison.Ordinal)).ConfigureAwait(false), "child reads the generated brief and reports readiness");
                        AssertContains("brief-present=True", run.Observer.Output, "the child sees the owner-generated brief");
                        AssertContains("mission-token-present=True", run.Observer.Output, "the supervisor injects the scoped mission token");
                        AssertContains("scoped-home=True", run.Observer.Output, "the supervisor applies the contained mission home");
                        AssertContains("link-secret-present=False", run.Observer.Output, "the link credential is scrubbed from the child");
                        string localRecord = File.ReadAllText(Path.Combine(state, run.JobId, "job.json"));
                        AssertFalse(localRecord.Contains(token, StringComparison.Ordinal), "the private runner journal does not retain the scoped mission token");
                        AssertFalse(localRecord.Contains("private Harbor-owned prompt", StringComparison.Ordinal), "the private runner journal does not retain the launch prompt");
                        AssertFalse(File.ReadAllText(Path.Combine(root, "runner.json")).Contains(token, StringComparison.Ordinal), "the shared runner configuration does not retain the scoped mission token");
                        await console.StopConsoleOnlyAsync().ConfigureAwait(false);
                        await CompleteOwnedMissionAfterRestartAsync(harness, run, configuredGrant, checkoutRoot, release, counter, root,
                            grantId, vessel, token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                DeleteShippingRunnerFixture(root);
            }
        }

        private async Task CompleteOwnedMissionAfterRestartAsync(Harness harness, OwnedMissionRun run, HarborRepositoryGrant grant,
            string checkoutRoot, string release, string counter, string root, string grantId, Vessel vessel, string token)
        {
            HarborOwnedRecovery recovery = new HarborOwnedRecovery(run.Observer);
            AdmiralInstance restarted = await harness.RestartAdmiralAsync(recovery, _ => { }).ConfigureAwait(false);
            await using (ShippingHarborConsole reconnected = new ShippingHarborConsole(root,
                "ws://127.0.0.1:" + restarted.Port + "/harbor/link", harness.OwnerA.BearerToken,
                configure: runnerSettings => ConfigureOwnedRunnerSettings(runnerSettings, checkoutRoot, grant)))
            {
                await WaitForOwnedRunnerAsync(reconnected, restarted).ConfigureAwait(false);
                AssertTrue(File.Exists(counter) && File.ReadAllLines(counter).Length == 1, "restart adopts the original child without a second writer");
                File.WriteAllText(release, "continue");
                await VerifyOriginalChildFinishedAsync(run.Observer, restarted, run.JobId).ConfigureAwait(false);

                HarborCheckoutService restartedCheckouts = new HarborCheckoutService(harness.Driver, run.Settings);
                restartedCheckouts.SetChannel(restarted.Workspace);
                HarborWorkspaceOperationResult head = await restartedCheckouts.ExecuteAsync(run.Dock,
                    HarborCheckoutService.CreateRequest(run.Dock, HarborWorkspaceOperationEnum.InspectCheckout)).ConfigureAwait(false);
                run.Mission.CommitHash = head.HeadCommit;
                run.Mission.Status = MissionStatusEnum.WorkProduced;
                await harness.Driver.Missions.UpdateAsync(run.Mission).ConfigureAwait(false);
                await VerifyOwnedCheckoutAsync(run, restartedCheckouts).ConfigureAwait(false);
                await VerifyOwnedDiffAndLandingAsync(harness, restarted, run, restartedCheckouts, vessel, grantId, token,
                    root, release, counter).ConfigureAwait(false);
            }
        }

        private async Task WaitForOwnedRunnerAsync(ShippingHarborConsole console, AdmiralInstance restarted)
        {
            AssertTrue(await WaitUntilAsync(() => console.HasExited || restarted.Registry.SessionCount == 1).ConfigureAwait(false), "runner reconnects to the restarted Admiral");
            AssertFalse(console.HasExited, console.Diagnostics());
        }

        private async Task VerifyOriginalChildFinishedAsync(HarborOwnedObserver observer, AdmiralInstance restarted, string jobId)
        {
            AssertTrue(await WaitUntilAsync(() => observer.Output.Contains("owned-change-committed", StringComparison.Ordinal)).ConfigureAwait(false), "the original child commits after the console restart");
            AssertTrue(await WaitUntilAsync(() => restarted.Coordinator.TryGetJob(jobId, out HarborJobSnapshot? current)
                && current.State == HarborJobStateEnum.Exited && current.CompletionDelivered).ConfigureAwait(false), "the restarted Admiral records the confirmed exit");
            AssertContains("offline output", observer.Output, "output produced while the original console was down reaches the restarted Admiral");
        }

        private async Task VerifyOwnedCheckoutAsync(OwnedMissionRun run, HarborCheckoutService checkouts)
        {
            string checkId = "owned-e2e-head-" + Guid.NewGuid().ToString("N");
            const string verificationCommand = "git rev-parse HEAD";
            HarborWorkspaceOperationRequest verifyRequest = HarborCheckoutService.CreateRequest(run.Dock, HarborWorkspaceOperationEnum.RunVerification);
            verifyRequest.Verification = new HarborVerificationRequest
            {
                CommandKey = "head",
                ExpectedCommandSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(verificationCommand))).ToLowerInvariant(),
                CheckId = checkId,
                TimeoutSeconds = 10,
                OutputLimitBytes = 4096,
                AcceptedExitCodes = new List<int> { 0 }
            };
            HarborWorkspaceOperationResult verified;
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("owned-e2e-verification:" + checkId, run.Mission.CommitHash!))
                verified = await checkouts.ExecuteAsync(run.Dock, verifyRequest).ConfigureAwait(false);
            AssertEqual(0, verified.ExitCode, "the configured verification command succeeds on the owned checkout");
            AssertTrue(!verified.TimedOut && !verified.Cancelled && !verified.Truncated, "the observed zero exit completes verification within the configured cap");

            HarborWorkspaceOperationRequest outputRequest = HarborCheckoutService.CreateRequest(run.Dock, HarborWorkspaceOperationEnum.ReadVerificationOutput);
            outputRequest.ExpectedHead = run.Mission.CommitHash;
            outputRequest.MaxBytes = 4096;
            outputRequest.VerificationOutput = new HarborVerificationOutputRequest
            {
                OutputId = verified.VerificationOutputId!, CheckId = checkId, Offset = 0, MaxBytes = 4096
            };
            HarborWorkspaceOperationResult output = await checkouts.ExecuteAsync(run.Dock, outputRequest).ConfigureAwait(false);
            AssertEqual(run.Mission.CommitHash, Encoding.UTF8.GetString(Convert.FromBase64String(output.BytesBase64!)).Trim(), "remote verification output is bound to the committed checkout head");
            string ownedResult = await checkouts.ReadTextAsync(HarborCheckoutAddress.ForDock(run.Dock.Id), "owned-result.txt").ConfigureAwait(false);
            AssertContains("brief-has-mission=True", ownedResult, "the actual child read and used the generated mission brief before committing");
        }

        private async Task RefuseOutsideMissionConfigurationAsync(Harness harness, AdmiralInstance restarted, OwnedMissionRun run,
            string root, string release, string counter)
        {
            string outsideConfiguration = Path.Combine(root, "outside-mission-config");
            Directory.CreateDirectory(outsideConfiguration);
            HarborLaunchResult launch = await restarted.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a",
                "owned-e2e-outside-config:" + run.Mission.Id,
                new HarborLaunchRequest
                {
                    Runtime = "fixture",
                    WorkingDirectory = run.CheckoutPath,
                    DockId = run.Dock.Id,
                    CheckoutBinding = run.Dock.HostCheckout,
                    MissionConfiguration = new HarborMissionRuntimeConfiguration
                    {
                        Token = "denial-" + Guid.NewGuid().ToString("N"),
                        Directory = outsideConfiguration,
                        ScopedHome = true
                    },
                    Arguments = new List<string>
                    {
                        typeof(global::Armada.Test.Unit.Program).Assembly.Location,
                        "--harbor-runner-child", "owned-commit", release, counter, "CLAUDE.md", run.RuntimeDirectory
                    }
                },
                new HarborJobBinding
                {
                    MissionId = run.Mission.Id,
                    CaptainId = run.Captain.Id,
                    DockId = run.Dock.Id,
                    AdmiralProcessId = 77002,
                    AdmiralProcessStartedUtc = DateTime.UtcNow,
                    Observer = new HarborOwnedObserver(),
                    OnRecorded = async (job, cancellationToken) =>
                    {
                        run.Mission.HarborJobId = job.JobId;
                        await harness.Driver.Missions.UpdateAsync(run.Mission, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                }).ConfigureAwait(false);
            AssertTrue(launch.Accepted, "the server records a launch attempt before the runner rejects its out-of-checkout configuration: " + launch.Reason);
            bool refusalObserved = await WaitUntilAsync(() => restarted.Coordinator.TryGetJob(launch.JobId!, out HarborJobSnapshot? current)
                && current!.State == HarborJobStateEnum.Uncertain
                && current.FailureReason == "harbor_runner_error: working_directory_outside_roots").ConfigureAwait(false);
            string refusalEvidence = String.Empty;
            if (!refusalObserved)
            {
                restarted.Coordinator.TryGetJob(launch.JobId!, out HarborJobSnapshot? snapshot);
                HarborJobRecord? persisted = await harness.Driver.HarborJobs.ReadAsync(launch.JobId!).ConfigureAwait(false);
                string journalPath = Path.Combine(root, "state", launch.JobId!, "job.json");
                HarborLocalJobRecord? journal = File.Exists(journalPath)
                    ? JsonSerializer.Deserialize<HarborLocalJobRecord>(File.ReadAllText(journalPath)) : null;
                refusalEvidence = " snapshot=" + snapshot?.State + "/" + snapshot?.FailureReason
                    + "; persisted=" + persisted?.State + "/" + persisted?.FailureReason
                    + "; journalFailure=" + journal?.FailureReason
                    + "; stopConfirmed=" + journal?.StopConfirmed + "; child=" + journal?.ProcessId;
            }
            AssertTrue(refusalObserved, "the runner's configuration refusal reaches the durable server fence" + refusalEvidence);
            HarborLocalJobRecord refused = JsonSerializer.Deserialize<HarborLocalJobRecord>(
                File.ReadAllText(Path.Combine(root, "state", launch.JobId!, "job.json")))!;
            AssertEqual("working_directory_outside_roots", refused.FailureReason, "outside configuration has a stable refusal reason");
            AssertTrue(refused.StopConfirmed && !refused.ProcessId.HasValue, "the supervisor refused the configuration before creating a child");
            AssertFalse(launch.Completion!.IsCompleted, "a runner error retains the Admiral fence until stop accounting completes");
            AssertEqual(1, File.ReadAllLines(counter).Length, "a refused outside configuration does not start a second process");
        }

        private async Task VerifyOwnedDiffAndLandingAsync(Harness harness, AdmiralInstance restarted, OwnedMissionRun run,
            HarborCheckoutService checkouts, Vessel vessel, string grantId, string token, string root, string release, string counter)
        {
            HostAwareGitService hostGit = new HostAwareGitService(new GitService(run.Logging, null, harness.Driver), checkouts);
            MissionDiffResult diff = await Armada.Server.MissionDiffReader.ReadAsync(harness.Driver, run.Settings.LogDirectory,
                hostGit, run.Mission).ConfigureAwait(false);
            AssertTrue(diff.Available && diff.Diff.Contains("owned-result.txt", StringComparison.Ordinal), "MissionDiffReader reads the runner's committed changes through the Harbor Git facade");

            string privateUntracked = Path.Combine(run.CheckoutPath, "private-live-diff.txt");
            try
            {
                await File.AppendAllTextAsync(Path.Combine(run.CheckoutPath, "README.md"), "HARBOR_STAGED_EDIT\n").ConfigureAwait(false);
                await RunGitFixtureAsync(run.CheckoutPath, "add", "README.md").ConfigureAwait(false);
                await File.AppendAllTextAsync(Path.Combine(run.CheckoutPath, "owned-result.txt"), "HARBOR_UNSTAGED_EDIT\n").ConfigureAwait(false);
                await File.WriteAllTextAsync(privateUntracked, "HARBOR_UNTRACKED_PRIVATE\n").ConfigureAwait(false);
                MissionDiffResult live = await Armada.Server.MissionDiffReader.ReadAsync(harness.Driver, run.Settings.LogDirectory,
                    hostGit, run.Mission).ConfigureAwait(false);
                AssertTrue(live.Available, "the owned Harbor checkout supplies its live tracked diff");
                AssertContains("HARBOR_STAGED_EDIT", live.Diff);
                AssertContains("HARBOR_UNSTAGED_EDIT", live.Diff);
                AssertFalse(live.Diff.Contains("HARBOR_UNTRACKED_PRIVATE", StringComparison.Ordinal), "private untracked content stays outside the Harbor diff");
            }
            finally
            {
                await RunGitFixtureAsync(run.CheckoutPath, "restore", "--source=HEAD", "--staged", "--worktree", "--", "README.md", "owned-result.txt").ConfigureAwait(false);
                if (File.Exists(privateUntracked)) File.Delete(privateUntracked);
            }

            LandingService landing = new LandingService(run.Logging, harness.Driver, run.Settings, hostGit);
            landing.SetHarborCheckouts(checkouts);
            HarborWorkspaceOperationResult beforeGenerated = await checkouts.InspectRepositoryAsync(vessel, "hbr_a", grantId, "refs/heads/main").ConfigureAwait(false);
            vessel.GeneratedArtifacts = new GeneratedArtifactPolicy
            { Paths = new List<string> { "manifest.json" }, RegenerateCommand = "sh tools/regenerate.sh" };
            AssertFalse(await landing.MergeInDedicatedWorktreeAsync(vessel, run.Mission, "main", run.Mission.BranchName).ConfigureAwait(false),
                "A Harbor declaration must not fall through to an ordinary text merge.");
            AssertStartsWith("generated_artifacts_harbor_unsupported", run.Mission.FailureReason ?? String.Empty);
            AssertEqual(beforeGenerated.HeadCommit, (await checkouts.InspectRepositoryAsync(vessel, "hbr_a", grantId,
                "refs/heads/main").ConfigureAwait(false)).HeadCommit);
            await harness.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
            Mission queuedPolicyMission = await harness.Driver.Missions.CreateAsync(new Mission("Harbor declared-artifact queue")
            {
                TenantId = run.Mission.TenantId, UserId = run.Mission.UserId, VesselId = vessel.Id,
                DockId = run.Dock.Id, Persona = "Recorder", Status = MissionStatusEnum.WorkProduced,
                BranchName = run.Mission.BranchName, CommitHash = run.Mission.CommitHash
            }).ConfigureAwait(false);
            MergeEntry policyEntry = await harness.Driver.MergeEntries.CreateAsync(new MergeEntry(run.Mission.BranchName!, "main")
            {
                TenantId = queuedPolicyMission.TenantId, UserId = queuedPolicyMission.UserId,
                VesselId = vessel.Id, MissionId = queuedPolicyMission.Id, Status = MergeStatusEnum.Queued
            }).ConfigureAwait(false);
            MergeQueueService policyQueue = new MergeQueueService(run.Logging, harness.Driver, run.Settings,
                hostGit, new MergeFailureClassifier());
            policyQueue.SetHarborCheckouts(checkouts);
            MergeEntry? refusedPolicy = await policyQueue.ProcessSingleAsync(policyEntry.Id, policyEntry.TenantId).ConfigureAwait(false);
            AssertNotNull(refusedPolicy);
            AssertEqual(MergeStatusEnum.Failed, refusedPolicy!.Status,
                "The Harbor queue refuses a declaration before it provisions or publishes a candidate.");
            AssertContains("generated_artifacts_harbor_unsupported",
                (refusedPolicy.TestOutput ?? String.Empty) + refusedPolicy.MergeFailureSummary);
            AssertEqual(beforeGenerated.HeadCommit, (await checkouts.InspectRepositoryAsync(vessel, "hbr_a", grantId,
                "refs/heads/main").ConfigureAwait(false)).HeadCommit);
            vessel.GeneratedArtifacts = null;
            await harness.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
            string? originalDependency = run.Mission.DependsOnMissionId;
            string candidate = (await hostGit.GetHeadCommitHashAsync(HarborCheckoutAddress.ForDock(run.Dock.Id)).ConfigureAwait(false))!;
            Mission boundJudge = await harness.Driver.Missions.CreateAsync(new Mission("Harbor candidate review")
            {
                TenantId = run.Mission.TenantId, UserId = run.Mission.UserId, VesselId = vessel.Id,
                VoyageId = run.Mission.VoyageId, Status = MissionStatusEnum.WorkProduced, Persona = "Judge",
                CommitHash = beforeGenerated.HeadCommit, BranchName = run.Mission.BranchName,
                AgentOutput = "[ARMADA:VERDICT] PASS\nEarlier source review"
            }).ConfigureAwait(false);
            boundJudge = (await harness.Driver.Missions.ReadAsync(boundJudge.Id).ConfigureAwait(false))!;
            GeneratedArtifactReviewBinding pendingReview = GeneratedArtifactReviewRule.NextBinding(boundJudge,
                new GeneratedArtifactLandingResult { Outcome = "ReviewRequired", SourceCommit = beforeGenerated.HeadCommit,
                    TargetBaseCommit = beforeGenerated.HeadCommit, CandidateCommit = candidate }, 2, DateTime.UtcNow);
            AssertTrue(await harness.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(boundJudge, pendingReview, true).ConfigureAwait(false));
            run.Mission.DependsOnMissionId = boundJudge.Id;
            await harness.Driver.Missions.UpdateAsync(run.Mission).ConfigureAwait(false);
            AssertFalse(await landing.MergeInDedicatedWorktreeAsync(vessel, run.Mission, "main", run.Mission.BranchName).ConfigureAwait(false),
                "A Harbor publication must honor the same-record Judge binding.");
            AssertEqual("generated_candidate_review_pending", run.Mission.FailureReason);
            AssertEqual(beforeGenerated.HeadCommit, (await checkouts.InspectRepositoryAsync(vessel, "hbr_a", grantId,
                "refs/heads/main").ConfigureAwait(false)).HeadCommit);
            run.Mission.DependsOnMissionId = originalDependency;
            run.Mission.FailureReason = null;
            await harness.Driver.Missions.UpdateAsync(run.Mission).ConfigureAwait(false);
            AssertTrue(await landing.MergeInDedicatedWorktreeAsync(vessel, run.Mission, "main", run.Mission.BranchName).ConfigureAwait(false), "landing merges the remote-owned branch through a dedicated runner checkout: " + run.Mission.FailureReason);
            HarborWorkspaceOperationResult published = await checkouts.InspectRepositoryAsync(vessel, "hbr_a", grantId, "refs/heads/main").ConfigureAwait(false);
            AssertFalse(String.Equals(run.Dock.HostCheckout!.BaseCommit, published.HeadCommit, StringComparison.OrdinalIgnoreCase), "the target branch advances from its original commit after landing");
            string primaryHead = await hostGit.GetHeadCommitHashAsync(HarborCheckoutAddress.ForDock(run.Dock.Id) + "/working").ConfigureAwait(false) ?? String.Empty;
            AssertEqual(published.HeadCommit, primaryHead, "the configured primary working checkout matches the published target head");
            AssertFalse(run.Observer.Output.Contains(token, StringComparison.Ordinal), "mission token values never appear in streamed child output");
            await VerifyInterruptedReclaimDeleteEscalationAsync(harness, run, checkouts, vessel, grantId, root).ConfigureAwait(false);
            await VerifyCancelledHarborLandingRefusedAsync(harness, landing, checkouts, run, vessel, root).ConfigureAwait(false);
            await RefuseOutsideMissionConfigurationAsync(harness, restarted, run, root, release, counter).ConfigureAwait(false);
        }

        private async Task VerifyInterruptedReclaimDeleteEscalationAsync(Harness harness, OwnedMissionRun run,
            HarborCheckoutService checkouts, Vessel vessel, string grantId, string root)
        {
            string branch = "armada/interrupted-reclaim-" + Guid.NewGuid().ToString("N");
            Dock dock = await checkouts.ProvisionAsync(vessel, run.Captain, branch, "hbr_a", grantId,
                "refs/heads/main", false, "cleanup:" + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
            string address = HarborCheckoutAddress.ForDock(dock.Id) + "/working";
            string head = await new HostAwareGitService(new GitService(run.Logging, null, harness.Driver), checkouts)
                .GetHeadCommitHashAsync(address).ConfigureAwait(false) ?? String.Empty;
            AssertTrue(DockGitAnchorPersistence.IsCommit(head), "the actual runner supplies the interrupted reclaim's pinned head");

            dock.Active = false;
            dock.CaptainId = null;
            dock.GitAnchorsSnapshot = new DockGitAnchorSnapshot
            {
                DockId = dock.Id,
                MissionId = run.Mission.Id,
                VesselId = vessel.Id,
                ProvisionedCommit = head,
                ProvisionedUtc = DateTime.UtcNow,
                Anchors = new GitAnchors { BaseCommit = head }
            };
            await harness.Driver.Docks.UpdateAsync(dock).ConfigureAwait(false);
            vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
            await harness.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

            HarborCheckoutLifecycleIntent interrupted = new HarborCheckoutLifecycleIntent
            {
                IntentId = "hcl_" + Guid.NewGuid().ToString("N"),
                BindingRevision = dock.HostCheckout!.Revision,
                DockId = dock.Id,
                BranchName = branch,
                Purpose = HarborCheckoutLifecyclePurposeEnum.Reclaim,
                InitialHead = head,
                ExpectedHead = head,
                PreserveSnapshot = true,
                PreserveReferences = true,
                PreserveBranch = true,
                Phase = HarborCheckoutLifecyclePhaseEnum.Prepared
            };
            AssertTrue(await harness.Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id,
                JsonSerializer.Serialize(dock.HostCheckout), null, interrupted).ConfigureAwait(false),
                "the database stores the prepared Reclaim receipt before remote cleanup starts");

            DockService docks = new DockService(run.Logging, harness.Driver, run.Settings,
                new HostAwareGitService(new GitService(run.Logging, null, harness.Driver), checkouts), checkouts);
            await VerifyUnconfirmedWriterRetainsBindingAsync(harness, dock, docks).ConfigureAwait(false);
            AssertTrue(await docks.DeleteAsync(dock.Id, dock.TenantId).ConfigureAwait(false),
                "Delete resumes Reclaim and persists its post-removal cleanup before deleting the dock");
            AssertEqual(null, await harness.Driver.Docks.ReadAsync(dock.TenantId!, dock.Id).ConfigureAwait(false),
                "the dock row is deleted only after both durable lifecycle intents finish");
            AssertFalse(await GitRefExistsAsync(Path.Combine(root, "repository.git"), "refs/heads/" + branch).ConfigureAwait(false),
                "the post-reclaim compare-and-delete removes the branch using the stored anchored head");
        }

        private async Task VerifyUnconfirmedWriterRetainsBindingAsync(Harness harness, Dock dock, DockService docks)
        {
            DateTime nowUtc = DateTime.UtcNow;
            HarborJobRecord uncertain = new HarborJobRecord
            {
                JobId = "hjob_cleanup_" + Guid.NewGuid().ToString("N"),
                RunnerId = dock.HostCheckout!.RunnerId,
                LaunchKey = "cleanup-fence:" + dock.Id,
                TenantId = dock.TenantId!,
                UserId = dock.UserId!,
                EnrollmentGeneration = dock.HostCheckout.EnrollmentGeneration,
                SessionGeneration = 1,
                State = HarborJobStateEnum.Uncertain,
                ProcessId = 9077,
                DockId = dock.Id,
                AdmiralProcessId = ProcessSupervisor.AllocateSyntheticProcessId(),
                AdmiralProcessStartedUtc = nowUtc,
                RunnerProcessStartedUtc = nowUtc.AddSeconds(-5),
                FailureReason = HarborJobCoordinator.ReasonResumeUnconfirmed,
                Revision = 1,
                CreatedUtc = nowUtc,
                LastUpdateUtc = nowUtc
            };
            await harness.Driver.HarborJobs.CreateAsync(uncertain).ConfigureAwait(false);
            bool held = false;
            try
            {
                await docks.DeleteAsync(dock.Id, dock.TenantId).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("dock_worktree_held", StringComparison.Ordinal))
            {
                held = true;
            }
            AssertTrue(held, "an unconfirmed Harbor writer blocks cleanup even when a Reclaim intent exists");
            Dock? retained = await harness.Driver.Docks.ReadAsync(dock.TenantId!, dock.Id).ConfigureAwait(false);
            AssertEqual(dock.HostCheckout.Revision, retained!.HostCheckout!.Revision,
                "a held cleanup preserves the exact durable checkout binding");

            AdmiralInstance confirmationHost = await harness.RestartAdmiralAsync(new NoMissionRecoveryHost(), _ => { }).ConfigureAwait(false);
            Principal globalAdministrator = await harness.AddPrincipalAsync(harness.OwnerA.TenantId,
                "owned-cleanup-stop-admin", false, true).ConfigureAwait(false);
            HarborJobConfirmStoppedRequest confirmation = new HarborJobConfirmStoppedRequest
            {
                ExpectedRevision = uncertain.Revision,
                EnrollmentGeneration = uncertain.EnrollmentGeneration,
                RunnerProcessId = uncertain.ProcessId,
                RunnerProcessStartedUtc = uncertain.RunnerProcessStartedUtc,
                Evidence = "host_confirmed_process_absent"
            };
            HttpReply reply = await harness.RequestAsync(globalAdministrator, System.Net.Http.HttpMethod.Post,
                "api/v1/harbor-runners/jobs/" + uncertain.JobId + "/confirm-stopped", confirmation, confirmationHost).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, reply.StatusCode, "the exact global-admin stop proof releases the test fence: " + reply.Body);
        }

        private static async Task<bool> GitRefExistsAsync(string repository, string reference)
        {
            ProcessStartInfo start = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(repository)!
            };
            start.ArgumentList.Add("--git-dir");
            start.ArgumentList.Add(repository);
            start.ArgumentList.Add("show-ref");
            start.ArgumentList.Add("--verify");
            start.ArgumentList.Add("--quiet");
            start.ArgumentList.Add(reference);
            using (Process process = Process.Start(start) ?? throw new InvalidOperationException("The fixture Git process did not start."))
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode == 0;
            }
        }

        private async Task VerifyCancelledHarborLandingRefusedAsync(Harness harness, LandingService landing,
            HarborCheckoutService checkouts, OwnedMissionRun run, Vessel vessel, string root)
        {
            string seed = Path.Combine(root, "seed");
            string branch = "harbor-cancel-" + Guid.NewGuid().ToString("N");
            await RunGitFixtureAsync(seed, "fetch", "origin").ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "checkout", "-B", branch, "origin/main").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(seed, "cancelled-harbor-work.txt"), "must not land\n").ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "add", "cancelled-harbor-work.txt").ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "commit", "-m", "Prepare cancelled Harbor landing").ConfigureAwait(false);
            string sourceTip = (await RunGitFixtureOutputAsync(seed, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
            await RunGitFixtureAsync(seed, "push", "-u", "origin", branch).ConfigureAwait(false);
            HarborCheckoutBinding binding = run.Dock.HostCheckout!;
            HarborWorkspaceOperationResult before = await checkouts.InspectRepositoryAsync(vessel,
                binding.RunnerId, binding.RepositoryGrantId,
                "refs/heads/main").ConfigureAwait(false);

            Mission cancelled = new Mission("Cancelled Harbor publication")
            {
                TenantId = run.Mission.TenantId,
                UserId = run.Mission.UserId,
                VesselId = vessel.Id,
                DockId = run.Dock.Id,
                Status = MissionStatusEnum.WorkProduced,
                BranchName = branch,
                CommitHash = sourceTip
            };
            cancelled = await harness.Driver.Missions.CreateAsync(cancelled).ConfigureAwait(false);
            await MissionCancellation.CancelAsync(harness.Driver, cancelled, "cancel before Harbor publication", null).ConfigureAwait(false);
            bool landed = await landing.MergeInDedicatedWorktreeAsync(vessel, cancelled, "main", branch).ConfigureAwait(false);

            HarborWorkspaceOperationResult after = await checkouts.InspectRepositoryAsync(vessel,
                binding.RunnerId, binding.RepositoryGrantId,
                "refs/heads/main").ConfigureAwait(false);
            Mission stored = (await harness.Driver.Missions.ReadAsync(cancelled.Id).ConfigureAwait(false))!;
            AssertFalse(landed, "cancelled Harbor work is refused at the publication boundary");
            AssertEqual(MissionStatusEnum.Cancelled, stored.Status, "Harbor landing does not replace the operator cancellation state");
            AssertEqual(before.HeadCommit, after.HeadCommit, "cancelled Harbor landing does not advance the remote target");

            await VerifyCancelledHarborQueuePublicationRefusedAsync(harness, checkouts, run, vessel, cancelled, branch, before.HeadCommit!)
                .ConfigureAwait(false);
        }

        private async Task VerifyCancelledHarborQueuePublicationRefusedAsync(Harness harness, HarborCheckoutService checkouts,
            OwnedMissionRun run, Vessel vessel, Mission cancelled, string branch, string targetHead)
        {
            HarborCheckoutBinding binding = run.Dock.HostCheckout!;
            MergeEntry entry = new MergeEntry(branch, "main")
            {
                TenantId = cancelled.TenantId,
                UserId = cancelled.UserId,
                MissionId = cancelled.Id,
                VesselId = vessel.Id,
                Status = MergeStatusEnum.Pushing
            };
            entry = await harness.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
            string attemptId = "mq_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Id + "\n0"))).ToLowerInvariant();
            Dock integration = await checkouts.ProvisionAuxiliaryAsync(vessel, "armada/merge-queue/" + entry.Id,
                binding.RunnerId, binding.RepositoryGrantId, "refs/heads/main", true,
                attemptId, run.Dock.CaptainId).ConfigureAwait(false);
            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            HostAwareGitService hostGit = new HostAwareGitService(new GitService(run.Logging, null, harness.Driver), checkouts);
            string? baseHead = await hostGit.GetHeadCommitHashAsync(checkout).ConfigureAwait(false);
            AssertNotNull(baseHead, "queue integration checkout starts at its pinned target");
            using (new HarborCheckoutOperationScope("mq-merge:" + attemptId + ":" + baseHead, baseHead!))
                await hostGit.MergeBranchLocalAsync(checkout, checkout + "/repository", branch).ConfigureAwait(false);

            MergeQueueService queue = new MergeQueueService(run.Logging, harness.Driver, run.Settings, hostGit, new MergeFailureClassifier());
            queue.SetHarborCheckouts(checkouts);
            MergeEntry? result = await queue.ProcessSingleAsync(entry.Id, entry.TenantId).ConfigureAwait(false);
            HarborWorkspaceOperationResult after = await checkouts.InspectRepositoryAsync(vessel,
                binding.RunnerId, binding.RepositoryGrantId, "refs/heads/main").ConfigureAwait(false);
            Mission storedMission = (await harness.Driver.Missions.ReadAsync(cancelled.Id).ConfigureAwait(false))!;
            AssertNotNull(result, "the cancelled queue entry remains inspectable");
            AssertEqual(MergeStatusEnum.Cancelled, result!.Status, "queue admission records cancellation before publication");
            AssertEqual(MissionStatusEnum.Cancelled, storedMission.Status, "queue admission preserves cancelled mission state");
            AssertEqual(targetHead, after.HeadCommit, "cancelled queue publication does not advance the target");
        }

        private static async Task RunGitFixtureAsync(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using (Process process = Process.Start(start) ?? throw new InvalidOperationException("The fixture Git process did not start."))
            {
                string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("Fixture Git failed: " + String.Join(" ", arguments) + "; " + output + error);
            }
        }

        private static async Task<string> RunGitFixtureOutputAsync(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using (Process process = Process.Start(start) ?? throw new InvalidOperationException("Fixture Git did not start."))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                string stdout = await output.ConfigureAwait(false);
                string stderr = await error.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("Fixture Git failed: " + String.Join(" ", arguments) + "; " + stdout + stderr);
                return stdout;
            }
        }

        private static async Task InitializeHarborOwnedRepositoryAsync(string root, string repository, string seed, string primary)
        {
            Directory.CreateDirectory(seed);
            await RunGitFixtureAsync(root, "init", "--bare", repository).ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "init", "-b", "main").ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "config", "user.name", "Harbor fixture").ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "config", "user.email", "harbor-fixture@example.invalid").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "Harbor owned checkout fixture\n").ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "add", "README.md").ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "commit", "-m", "Initialize Harbor fixture").ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "remote", "add", "origin", repository).ConfigureAwait(false);
            await RunGitFixtureAsync(seed, "push", "-u", "origin", "main").ConfigureAwait(false);
            await RunGitFixtureAsync(root, "--git-dir", repository, "symbolic-ref", "HEAD", "refs/heads/main").ConfigureAwait(false);
            await RunGitFixtureAsync(root, "--git-dir", repository, "remote", "add", "origin", repository).ConfigureAwait(false);
            await RunGitFixtureAsync(root, "--git-dir", repository, "config", "user.name", "Harbor fixture").ConfigureAwait(false);
            await RunGitFixtureAsync(root, "--git-dir", repository, "config", "user.email", "harbor-fixture@example.invalid").ConfigureAwait(false);
            await RunGitFixtureAsync(root, "clone", repository, primary).ConfigureAwait(false);
            await RunGitFixtureAsync(primary, "config", "user.name", "Harbor fixture").ConfigureAwait(false);
            await RunGitFixtureAsync(primary, "config", "user.email", "harbor-fixture@example.invalid").ConfigureAwait(false);
        }

        private async Task HarborDiscardUncommittedDock_RetainsBindingWhenReclaimRpcFails()
        {
            await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
            ArmadaSettings settings = new ArmadaSettings();
            settings.Harbor.Enabled = true;
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_harbor_discard_hold_" + Guid.NewGuid().ToString("N"));
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("discard-hold", "https://example.invalid/repository.git")
            {
                TenantId = harness.OwnerA.TenantId, UserId = harness.OwnerA.UserId, DefaultBranch = "main"
            }).ConfigureAwait(false);
            Captain captain = await harness.Driver.Captains.CreateAsync(new Captain("discard-hold-captain", AgentRuntimeEnum.ClaudeCode)
            {
                TenantId = harness.OwnerA.TenantId, UserId = harness.OwnerA.UserId, State = CaptainStateEnum.Idle
            }).ConfigureAwait(false);
            Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("discard-hold")
            {
                TenantId = harness.OwnerA.TenantId, UserId = harness.OwnerA.UserId, VesselId = vessel.Id,
                Status = MissionStatusEnum.Pending, AssignmentState = MissionAssignmentStateEnum.Pending
            }).ConfigureAwait(false);
            settings.Harbor.MissionRoutes.Add(new HarborMissionRoute
            {
                CaptainId = captain.Id, RunnerId = "hbr_discard", CheckoutMode = HarborCheckoutModeEnum.HarborOwned,
                RepositoryGrantId = "grant_discard"
            });
            AssertTrue(await harness.Driver.HarborRunnerEnrollments.TryEnrollAsync(new HarborRunnerEnrollment
            {
                RunnerId = "hbr_discard", TenantId = harness.OwnerA.TenantId, UserId = harness.OwnerA.UserId,
                AuthMethod = "unit-test", Generation = 1, Active = true
            }, 0).ConfigureAwait(false), "the exact owner enrollment exists");

            HarborCheckoutService checkouts = new HarborCheckoutService(harness.Driver, settings);
            checkouts.SetChannel(new ProvisionPreemptionAndReclaimFailureChannel(harness.Driver, captain.Id));
            DockService docks = new DockService(logging, harness.Driver, settings, new GitService(logging, null, harness.Driver), checkouts);
            CaptainService captains = new CaptainService(logging, harness.Driver, settings, new GitService(logging, null, harness.Driver), docks);
            MissionService missions = new MissionService(logging, harness.Driver, settings, docks, captains,
                resourcePressureAdmission: global::Test.Shared.Infrastructure.TestResourcePressure.Unconstrained(settings));

            AssertFalse(await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false),
                "a concurrent captain claim prevents the assignment commit");
            Mission storedMission = (await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
            Dock? retainedRow = await harness.Driver.Docks.ReadByProvisionAttemptAsync(storedMission.DockProvisionAttemptId!).ConfigureAwait(false);
            AssertNotNull(retainedRow, "unconfirmed remote reclaim retains the durable dock row and its provision attempt");
            Dock retained = retainedRow!;
            AssertNotNull(retained.HostCheckout, "failed remote reclaim retains the durable checkout binding");
            AssertTrue(retained.Active, "an unconfirmed reclaim does not mark the dock inactive");
            AssertTrue(retained.HostCheckoutLifecycle != null
                && retained.HostCheckoutLifecycle.Phase == HarborCheckoutLifecyclePhaseEnum.Prepared,
                "the incomplete lifecycle receipt remains attached for exact recovery");
            AssertEqual(MissionStatusEnum.Pending, storedMission.Status, "the mission can retry only after the checkout cleanup is resolved");
        }

        private sealed class ProvisionPreemptionAndReclaimFailureChannel : IHarborWorkspaceOperations
        {
            private readonly DatabaseDriver _Database;
            private readonly string _CaptainId;

            public ProvisionPreemptionAndReclaimFailureChannel(DatabaseDriver database, string captainId)
            {
                _Database = database;
                _CaptainId = captainId;
            }

            public async Task<HarborWorkspaceOperationResult> ExecuteAsync(AuthContext auth,
                HarborWorkspaceOperationRequest request, CancellationToken token = default)
            {
                HarborWorkspaceOperationResult result = new HarborWorkspaceOperationResult
                {
                    Accepted = true, RequestId = request.RequestId, Operation = request.Operation,
                    Binding = request.Binding, RepositoryFingerprint = request.ExpectedRepositoryFingerprint
                };
                if (request.Operation == HarborWorkspaceOperationEnum.InspectRepository)
                {
                    result.HeadCommit = new string('a', 40);
                    result.BranchName = request.RequestedRevision;
                    return result;
                }
                if (request.Operation == HarborWorkspaceOperationEnum.ProvisionCheckout)
                {
                    Captain? captain = await _Database.Captains.ReadAsync(_CaptainId, token).ConfigureAwait(false);
                    captain!.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = "another-mission";
                    captain.CurrentDockId = "another-dock";
                    await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                    result.HeadCommit = request.Binding!.BaseCommit;
                    result.BranchName = request.Detached ? "(detached)" : request.Binding.BranchName;
                    return result;
                }
                if (request.Operation == HarborWorkspaceOperationEnum.Git
                    && request.Git?.Operation == HarborGitOperationEnum.GetHeadCommitHash)
                {
                    result.Git = new HarborGitOperationResult { Text = request.Binding?.BaseCommit };
                    return result;
                }
                if (request.Operation == HarborWorkspaceOperationEnum.Git
                    && request.Git?.Operation == HarborGitOperationEnum.SnapshotDockWork)
                    throw new InvalidOperationException("simulated_workspace_reclaim_disconnect");
                throw new InvalidOperationException("unexpected_discard_test_workspace_operation");
            }
        }

        private static void ConfigureOwnedRunnerSettings(HarborRunnerSettings settings, string checkoutRoot, HarborRepositoryGrant grant)
        {
            settings.Supervisor.WorkingRoots.Add(checkoutRoot);
            settings.Supervisor.MaxRetainedJobs = 48;
            settings.Supervisor.MaxVerificationSpoolBytes = 256L * 1024L * 1024L;
            settings.RepositoryGrants.Add(grant);
        }

        private static async Task<OwnedMissionRun> StartOwnedMissionRunAsync(Harness harness, Vessel vessel, string grantId,
            string checkoutRoot, string release, string counter, string token)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.Harbor.Enabled = true;
            settings.LogDirectory = Path.Combine(Path.GetDirectoryName(checkoutRoot)!, "logs");
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            HarborCheckoutService checkouts = new HarborCheckoutService(harness.Driver, settings);
            checkouts.SetChannel(harness.Workspace);
            HostAwareGitService hostGit = new HostAwareGitService(new GitService(logging, null, harness.Driver), checkouts);
            Captain captain = await harness.Driver.Captains.CreateAsync(new Captain("owned-e2e-captain", AgentRuntimeEnum.ClaudeCode)
            {
                TenantId = harness.OwnerA.TenantId,
                UserId = harness.OwnerA.UserId,
                State = CaptainStateEnum.Working
            }).ConfigureAwait(false);
            Dock dock = await checkouts.ProvisionAsync(vessel, captain, "feature/owned-e2e", "hbr_a", grantId,
                "main", false, "mission:owned-e2e-attempt").ConfigureAwait(false);
            Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("Harbor owned acceptance", "Read the brief, make a small change, and commit it.")
            {
                TenantId = harness.OwnerA.TenantId,
                UserId = harness.OwnerA.UserId,
                VesselId = vessel.Id,
                CaptainId = captain.Id,
                DockId = dock.Id,
                BranchName = dock.BranchName,
                CommitHash = dock.HostCheckout!.BaseCommit,
                Status = MissionStatusEnum.Assigned,
                AssignmentState = MissionAssignmentStateEnum.Assigned
            }).ConfigureAwait(false);
            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            await harness.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

            DockService docks = new DockService(logging, harness.Driver, settings, hostGit, checkouts);
            CaptainService captains = new CaptainService(logging, harness.Driver, settings, hostGit, docks);
            MissionService missions = new MissionService(logging, harness.Driver, settings, docks, captains,
                git: hostGit,
                resourcePressureAdmission: global::Test.Shared.Infrastructure.TestResourcePressure.Unconstrained(settings));
            missions.SetHarborCheckouts(checkouts);
            await missions.GenerateClaudeMdAsync(dock.WorktreePath!, mission, vessel, captain).ConfigureAwait(false);
            string brief = await checkouts.ReadTextAsync(HarborCheckoutAddress.ForDock(dock.Id), "CLAUDE.md").ConfigureAwait(false);
            if (!brief.Contains("Harbor owned acceptance", StringComparison.Ordinal))
                throw new InvalidOperationException("The Harbor-owned mission brief was not written through the opaque address.");

            string checkoutPath = Path.Combine(checkoutRoot, dock.HostCheckout.CheckoutId);
            string runtimeDirectory = Path.Combine(checkoutPath, ".armada", "runtime", captain.Id);
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("owned-e2e-runtime-config", dock.HostCheckout.BaseCommit))
                await checkouts.WriteTextAsync(HarborCheckoutAddress.ForDock(dock.Id), ".armada/runtime/" + captain.Id + "/.scope", mission.Id, true).ConfigureAwait(false);

            HarborOwnedObserver observer = new HarborOwnedObserver();
            HarborLaunchRequest launchRequest = new HarborLaunchRequest
            {
                Runtime = "fixture",
                WorkingDirectory = checkoutPath,
                DockId = dock.Id,
                CheckoutBinding = dock.HostCheckout,
                MissionConfiguration = new HarborMissionRuntimeConfiguration { Token = token, Directory = runtimeDirectory, ScopedHome = true },
                Prompt = "private Harbor-owned prompt",
                PromptViaStdin = true,
                Arguments = new List<string>
                {
                    typeof(global::Armada.Test.Unit.Program).Assembly.Location,
                    "--harbor-runner-child", "owned-commit", release, counter, "CLAUDE.md", runtimeDirectory
                }
            };
            HarborLaunchResult launch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a",
                "owned-e2e:" + mission.Id, launchRequest, new HarborJobBinding
                {
                    MissionId = mission.Id,
                    CaptainId = captain.Id,
                    DockId = dock.Id,
                    AdmiralProcessId = 77001,
                    AdmiralProcessStartedUtc = DateTime.UtcNow,
                    Observer = observer,
                    OnRecorded = async (job, cancellationToken) =>
                    {
                        mission.HarborJobId = job.JobId;
                        await harness.Driver.Missions.UpdateAsync(mission, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                }).ConfigureAwait(false);
            if (!launch.Accepted || String.IsNullOrWhiteSpace(launch.JobId))
                throw new InvalidOperationException("The Harbor-owned child launch failed: " + launch.Reason);
            return new OwnedMissionRun(settings, logging, checkouts, captain, dock, mission, observer, launch.JobId, checkoutPath, runtimeDirectory);
        }

        private static string DescribeShippingProcess(int processId, DateTime? recorded)
        {
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId))
                    return "pid=" + processId + " recorded=" + recorded?.ToString("O") + " actual=" + process.StartTime.ToUniversalTime().ToString("O") + " exited=" + process.HasExited;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception)
            {
                return "pid=" + processId + " identity unavailable=" + exception.GetType().Name;
            }
        }

        private static string ShippingRunnerDiagnostics(ShippingHarborConsole console, HarborJobCoordinator coordinator, string jobId)
        {
            string server = coordinator.TryGetJob(jobId, out HarborJobSnapshot? current)
                ? "server=" + current!.State + " reason=" + current.FailureReason
                : "server job missing";
            try
            {
                HarborLocalJobRecord record = console.Journal.Read(jobId);
                string output = String.Concat(console.Journal.ReadOutput(jobId, 0).ConvertAll(item => item.Data));
                return server + " local=" + JsonSerializer.Serialize(record) + " output=" + output + " console=" + console.Diagnostics();
            }
            catch (HarborJobJournalException exception)
            {
                return server + " journal=" + exception.ReasonCode + " console=" + console.Diagnostics();
            }
        }

        private static void DeleteShippingRunnerFixture(string root)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException exception)
            {
                Console.WriteLine("Shipping Harbor fixture cleanup failed: " + exception.Message);
            }
        }

        private sealed class HarborOwnedObserver : IHarborJobCompletionObserver
        {
            private readonly object _Gate = new object();
            private readonly StringBuilder _Output = new StringBuilder();
            private readonly TaskCompletionSource<HarborJobSnapshot> _Finished = new TaskCompletionSource<HarborJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

            public string Output
            {
                get { lock (_Gate) return _Output.ToString(); }
            }

            public Task<HarborJobSnapshot> Finished => _Finished.Task;

            public void OnStarted(HarborJobSnapshot job)
            {
            }

            public void OnOutput(string jobId, HarborOutput output)
            {
                lock (_Gate) _Output.Append(output.Data);
            }

            public void OnFinished(HarborJobSnapshot job)
            {
                _Finished.TrySetResult(job);
            }

            public Task OnFinishedAsync(HarborJobSnapshot job)
            {
                OnFinished(job);
                return Task.CompletedTask;
            }
        }

        private sealed class OwnedMissionRun
        {
            public ArmadaSettings Settings { get; }
            public LoggingModule Logging { get; }
            public HarborCheckoutService Checkouts { get; }
            public Captain Captain { get; }
            public Dock Dock { get; }
            public Mission Mission { get; }
            public HarborOwnedObserver Observer { get; }
            public string JobId { get; }

            public string CheckoutPath { get; }
            public string RuntimeDirectory { get; }

            public OwnedMissionRun(ArmadaSettings settings, LoggingModule logging, HarborCheckoutService checkouts,
                Captain captain, Dock dock, Mission mission, HarborOwnedObserver observer, string jobId, string checkoutPath, string runtimeDirectory)
            {
                Settings = settings;
                Logging = logging;
                Checkouts = checkouts;
                Captain = captain;
                Dock = dock;
                Mission = mission;
                Observer = observer;
                JobId = jobId;
                CheckoutPath = checkoutPath;
                RuntimeDirectory = runtimeDirectory;
            }
        }

        private sealed class HarborOwnedRecovery : IHarborJobRecoveryHost
        {
            private readonly IHarborJobObserver _Observer;

            public HarborOwnedRecovery(IHarborJobObserver observer)
            {
                _Observer = observer ?? throw new ArgumentNullException(nameof(observer));
            }

            public Task<HarborJobRecoveryResult> RestoreAsync(HarborJobSnapshot job, CancellationToken token = default)
            {
                return Task.FromResult(new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Adopted, _Observer));
            }

            public Task<HarborJobRecoveryResult> TryAdoptAsync(HarborJobSnapshot job, HarborRunnerSession session, CancellationToken token = default)
            {
                return Task.FromResult(new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Adopted, _Observer));
            }

            public Task<bool> DeliverCompletionAsync(HarborJobSnapshot job, CancellationToken token = default)
            {
                return Task.FromResult(true);
            }
        }

        private sealed class NoMissionRecoveryHost : IHarborJobRecoveryHost
        {
            public Task<HarborJobRecoveryResult> RestoreAsync(HarborJobSnapshot job, CancellationToken token = default)
                => throw new InvalidOperationException("An operator job has no mission to restore.");
            public Task<HarborJobRecoveryResult> TryAdoptAsync(HarborJobSnapshot job, HarborRunnerSession session, CancellationToken token = default)
                => throw new InvalidOperationException("An operator job has no mission lifecycle to adopt.");
            public Task<bool> DeliverCompletionAsync(HarborJobSnapshot job, CancellationToken token = default)
                => throw new InvalidOperationException("The running operator job has no mission completion to restore.");
        }

        private async Task HarborWorkspace_ChannelAuthorityGenerationAndTypedResults()
        {
            await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, await harness.EnrollAsync(harness.Administrator, "hbr_a", harness.OwnerA).ConfigureAwait(false), "workspace runner enrolled");
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("workspace-vessel", "https://example.invalid/workspace.git")
            {
                TenantId = harness.OwnerA.TenantId,
                UserId = harness.OwnerA.UserId
            }).ConfigureAwait(false);
            string fingerprint = new string('a', 64);
            HarborCheckoutBinding binding = new HarborCheckoutBinding
            {
                RunnerId = "hbr_a", EnrollmentGeneration = 1, TenantId = harness.OwnerA.TenantId, UserId = harness.OwnerA.UserId,
                RepositoryGrantId = "grant_a", RepositoryFingerprint = fingerprint, CheckoutId = "hco_0123456789abcdef0123456789abcdef",
                BaseCommit = new string('b', 40), BranchName = "main", Revision = "revision_a"
            };
            Dock dock = await harness.Driver.Docks.CreateAsync(new Dock(vessel.Id)
            {
                TenantId = harness.OwnerA.TenantId, UserId = harness.OwnerA.UserId, HostCheckout = binding
            }).ConfigureAwait(false);
            await using FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
            HarborHandshakeAck ack = await runner.HandshakeAsync("hbr_a", workspaceCapability: true).ConfigureAwait(false);
            AssertTrue(ack.Accepted, "workspace-capable owner connects");
            AssertEqual(harness.OwnerA.TenantId, ack.TenantId, "ack carries authenticated tenant");
            AssertEqual(harness.OwnerA.UserId, ack.UserId, "ack carries authenticated user");
            AssertEqual(1L, ack.EnrollmentGeneration, "ack carries durable enrollment generation");
            AssertTrue(ack.ConnectionGeneration > 0, "ack carries server connection generation");

            HarborWorkspaceOperationRequest inspect = RepositoryInspectRequest("hbr_a", vessel.Id, "grant_a", fingerprint);
            HarborWorkspaceOperationResult denied = await harness.Workspace.ExecuteAsync(harness.OwnerB.Auth, inspect).ConfigureAwait(false);
            AssertEqual("harbor_command_unauthorized", denied.Reason, "another owner cannot command the workspace link");
            AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "cross-owner request sends no workspace command");

            Task<HarborWorkspaceOperationResult> inspectTask = harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, inspect);
            HarborWorkspaceOperationRequest inspectWire = await runner.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            AssertTrue(!String.IsNullOrWhiteSpace(inspectWire.RequestId), "server assigns request identity");
            AssertEqual(inspectWire.RequestId, inspectWire.CorrelationId, "wire correlation is server-issued request identity");
            AssertEqual(ack.TenantId, inspectWire.TenantId, "wire tenant comes from the verified connection");
            AssertEqual(ack.UserId, inspectWire.UserId, "wire user comes from the verified connection");
            AssertEqual("hbr_a", inspectWire.RunnerId, "wire runner is the current session");
            AssertEqual(ack.EnrollmentGeneration, inspectWire.EnrollmentGeneration, "wire enrollment generation matches handshake");
            AssertEqual(ack.ConnectionGeneration, inspectWire.ConnectionGeneration, "wire connection generation matches handshake");
            await runner.SendAsync(new HarborWorkspaceOperationResult
            {
                RequestId = "forged-request", CorrelationId = "forged-request", Operation = inspectWire.Operation,
                Accepted = true, RepositoryFingerprint = fingerprint
            }).ConfigureAwait(false);
            AssertEqual("workspace_response_rejected", (await runner.NextAsync<HarborError>().ConfigureAwait(false)).Message,
                "a result outside the pending request correlation is rejected");
            await runner.SendAsync(new HarborWorkspaceOperationResult
            {
                RequestId = inspectWire.RequestId, CorrelationId = inspectWire.RequestId, Operation = inspectWire.Operation,
                Accepted = true, RepositoryFingerprint = fingerprint
            }).ConfigureAwait(false);
            AssertTrue((await inspectTask.ConfigureAwait(false)).Accepted, "typed repository result completes the exact pending request");

            HarborCheckoutBinding wrongBinding = new HarborCheckoutBinding
            {
                RunnerId = binding.RunnerId, EnrollmentGeneration = binding.EnrollmentGeneration, TenantId = binding.TenantId,
                UserId = binding.UserId, RepositoryGrantId = binding.RepositoryGrantId, RepositoryFingerprint = binding.RepositoryFingerprint,
                CheckoutId = binding.CheckoutId, BaseCommit = binding.BaseCommit, BranchName = binding.BranchName, Revision = "stale_revision"
            };
            HarborWorkspaceOperationRequest staleCheckout = BoundWorkspaceRequest(wrongBinding, dock.Id, vessel.Id, fingerprint);
            AssertEqual("workspace_checkout_binding_mismatch",
                (await harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, staleCheckout).ConfigureAwait(false)).Reason,
                "a stale pinned checkout revision is refused");
            AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "a mismatched checkout identity is not dispatched");

            HarborWorkspaceOperationRequest git = BoundWorkspaceRequest(binding, dock.Id, vessel.Id, fingerprint);
            git.Operation = HarborWorkspaceOperationEnum.Git;
            git.ExpectedHead = binding.BaseCommit;
            git.Git = new HarborGitOperationRequest { Operation = HarborGitOperationEnum.GetCurrentBranch };
            Task<HarborWorkspaceOperationResult> gitTask = harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, git);
            HarborWorkspaceOperationRequest gitWire = await runner.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            AssertEqual(HarborGitOperationEnum.GetCurrentBranch, gitWire.Git!.Operation, "typed Git request crosses the channel");
            await runner.SendAsync(new HarborWorkspaceOperationResult
            {
                RequestId = gitWire.RequestId, CorrelationId = gitWire.RequestId, Operation = gitWire.Operation,
                Binding = gitWire.Binding, RepositoryFingerprint = fingerprint, Accepted = true,
                Git = new HarborGitOperationResult { Text = "main" }
            }).ConfigureAwait(false);
            HarborWorkspaceOperationResult gitResult = await gitTask.ConfigureAwait(false);
            AssertEqual("main", gitResult.Git!.Text, "typed Git result survives wire serialization");

            HarborWorkspaceOperationRequest mutation = BoundWorkspaceRequest(binding, dock.Id, vessel.Id, fingerprint);
            mutation.Operation = HarborWorkspaceOperationEnum.WriteText;
            mutation.ExpectedHead = binding.BaseCommit;
            mutation.RelativePath = "notes.txt";
            mutation.Text = "safe workspace write";
            mutation.Overwrite = true;
            mutation.OperationKey = "write-intent-001";
            Task<HarborWorkspaceOperationResult> firstWrite = harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, mutation);
            HarborWorkspaceOperationRequest firstWire = await runner.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            await runner.SendAsync(WorkspaceAccepted(firstWire, fingerprint)).ConfigureAwait(false);
            AssertTrue((await firstWrite.ConfigureAwait(false)).Accepted, "first durable mutation intent is accepted");
            Task<HarborWorkspaceOperationResult> retryWrite = harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, mutation);
            HarborWorkspaceOperationRequest retryWire = await runner.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            AssertEqual("write-intent-001", retryWire.OperationKey, "retry retains stable mutation key");
            AssertTrue(firstWire.RequestId != retryWire.RequestId, "each attempt receives a fresh request correlation");
            await runner.SendAsync(WorkspaceAccepted(retryWire, fingerprint)).ConfigureAwait(false);
            AssertTrue((await retryWrite.ConfigureAwait(false)).Accepted, "same mutation identity can complete a later attempt");

            HarborWorkspaceOperationRequest verification = BoundWorkspaceRequest(binding, dock.Id, vessel.Id, fingerprint);
            verification.Operation = HarborWorkspaceOperationEnum.RunVerification;
            verification.ExpectedHead = binding.BaseCommit;
            verification.TimeoutSeconds = 1;
            verification.OperationKey = "verification-inner-timeout-001";
            const string verificationCheckId = "unit-check-001";
            verification.Verification = new HarborVerificationRequest
            {
                CommandKey = "unit-check",
                ExpectedCommandSha256 = new string('c', 64),
                CheckId = verificationCheckId,
                TimeoutSeconds = 2
            };
            Task<HarborWorkspaceOperationResult> verificationTask = harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, verification);
            HarborWorkspaceOperationRequest verificationWire = await runner.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
            await runner.SendAsync(new HarborWorkspaceOperationResult
            {
                RequestId = verificationWire.RequestId,
                CorrelationId = verificationWire.RequestId,
                Operation = verificationWire.Operation,
                Binding = verificationWire.Binding,
                RepositoryFingerprint = fingerprint,
                VerificationCheckId = verificationCheckId,
                VerificationOutputId = "verification-output-001",
                Accepted = true
            }).ConfigureAwait(false);
            AssertTrue((await verificationTask.ConfigureAwait(false)).Accepted,
                "the server waits for the configured verification timeout, not the shorter outer request timeout");
        }

        private async Task HarborWorkspace_RejectsInvalidShapesAndUnavailableRunner()
        {
            await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
            await harness.EnrollAsync(harness.Administrator, "hbr_a", harness.OwnerA).ConfigureAwait(false);
            await harness.EnrollAsync(harness.Administrator, "hbr_plain", harness.OwnerA).ConfigureAwait(false);
            await using FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
            AssertTrue((await runner.HandshakeAsync("hbr_a", workspaceCapability: true).ConfigureAwait(false)).Accepted, "workspace runner connected");

            HarborWorkspaceOperationRequest nestedMismatch = RepositoryInspectRequest("hbr_a", "vessel_a", "grant_a", new string('a', 64));
            nestedMismatch.Git = new HarborGitOperationRequest { Operation = HarborGitOperationEnum.GetCurrentBranch };
            HarborWorkspaceOperationResult mismatch = await harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, nestedMismatch).ConfigureAwait(false);
            AssertEqual("workspace_request_invalid", mismatch.Reason, "Git payload cannot accompany a non-Git operation");
            nestedMismatch = RepositoryInspectRequest("hbr_a", "vessel_a", "grant_a", new string('a', 64));
            nestedMismatch.Operation = HarborWorkspaceOperationEnum.Git;
            AssertEqual("workspace_request_invalid", (await harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, nestedMismatch).ConfigureAwait(false)).Reason,
                "a Git operation requires its typed payload");
            nestedMismatch = RepositoryInspectRequest("hbr_a", "vessel_a", "grant_a", new string('a', 64));
            nestedMismatch.Verification = new HarborVerificationRequest { CommandKey = "verify", ExpectedCommandSha256 = new string('a', 64), CheckId = "check" };
            AssertEqual("workspace_request_invalid", (await harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, nestedMismatch).ConfigureAwait(false)).Reason,
                "verification payload cannot accompany another operation");
            nestedMismatch = RepositoryInspectRequest("hbr_a", "vessel_a", "grant_a", new string('a', 64));
            nestedMismatch.Version++;
            AssertEqual("workspace_protocol_unsupported", (await harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, nestedMismatch).ConfigureAwait(false)).Reason,
                "unsupported workspace protocol is refused before dispatch");
            nestedMismatch = RepositoryInspectRequest("hbr_a", "vessel_a", "grant_a", new string('a', 64));
            nestedMismatch.Operation = (HarborWorkspaceOperationEnum)999;
            AssertEqual("workspace_operation_invalid", (await harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, nestedMismatch).ConfigureAwait(false)).Reason,
                "unknown workspace operations are refused before dispatch");
            AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "invalid request shapes never reach the runner");

            await using FakeRunner noWorkspace = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
            AssertTrue((await noWorkspace.HandshakeAsync("hbr_plain").ConfigureAwait(false)).Accepted, "legacy runner connects without workspace capability");
            HarborWorkspaceOperationRequest unavailable = RepositoryInspectRequest("hbr_plain", "vessel_a", "grant_a", new string('a', 64));
            HarborWorkspaceOperationResult refused = await harness.Workspace.ExecuteAsync(harness.OwnerA.Auth, unavailable).ConfigureAwait(false);
            AssertEqual("workspace_runner_unavailable", refused.Reason, "missing negotiated capability refuses the operation");
            AssertTrue(await noWorkspace.NoMessageAsync().ConfigureAwait(false), "unavailable owner causes no local or remote fallback");
        }

        private async Task HarborMissionLaunch_UsesBoundRunnerAndVerifiedCheckoutAddress()
        {
            await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
            await harness.EnrollBothAsync().ConfigureAwait(false);
            await using FakeRunner runnerA = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false);
            await using FakeRunner runnerB = await harness.ConnectAsync(harness.OwnerB).ConfigureAwait(false);
            AssertTrue((await runnerA.HandshakeAsync("hbr_a", workspaceCapability: true).ConfigureAwait(false)).Accepted, "bound runner accepts workspace capability");
            AssertTrue((await runnerB.HandshakeAsync("hbr_b").ConfigureAwait(false)).Accepted, "configured-route runner connects");
            using MissionScope scope = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_b", prepare: (root, settings, mission) =>
            {
                settings.SeedDockRuntimeMcpConfig = true;
                settings.Harbor.CaptainMcpBaseUrl = "https://admiral.example/mcp";
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            HarborCheckoutBinding binding = await SetMissionDockBindingAsync(harness, scope, harness.OwnerA, "hbr_a").ConfigureAwait(false);
            ArmadaSettings checkoutSettings = new ArmadaSettings();
            checkoutSettings.Harbor.Enabled = true;
            HarborCheckoutService checkouts = new HarborCheckoutService(harness.Driver, checkoutSettings);
            checkouts.SetChannel(harness.Workspace);
            scope.Handler.SetHarborCheckouts(checkouts);

            Task<int> launchTask = scope.Handler.HandleLaunchAgentAsync(scope.Captain, scope.Mission, scope.Dock);
            HarborWorkspaceOperationRequest inspect = await runnerA.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            AssertEqual(HarborWorkspaceOperationEnum.InspectCheckout, inspect.Operation, "launch first verifies the durable checkout on its owner");
            AssertEqual("hbr_a", inspect.RunnerId, "checkout binding takes precedence over the configured route");
            AssertEqual(scope.Dock.Id, inspect.DockId, "inspection carries the durable dock identity");
            AssertEqual(binding.CheckoutId, inspect.Binding!.CheckoutId, "inspection carries the complete persisted binding");
            string runnerDirectory = "/runner/checkouts/primary";
            await runnerA.SendAsync(new HarborWorkspaceOperationResult
            {
                RequestId = inspect.RequestId, CorrelationId = inspect.RequestId, Operation = inspect.Operation,
                Binding = inspect.Binding, RepositoryFingerprint = binding.RepositoryFingerprint,
                CanonicalWorkingDirectory = runnerDirectory, Accepted = true
            }).ConfigureAwait(false);

            HarborWorkspaceOperationRequest scopeFile = await runnerA.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            AssertEqual(HarborWorkspaceOperationEnum.WriteText, scopeFile.Operation, "mission scope marker is written through the owner");
            AssertEqual(".armada/runtime/" + scope.Captain.Id + "/.scope", scopeFile.RelativePath!, "scope marker stays inside the captain's runtime folder");
            AssertEqual(scope.Mission.Id, scopeFile.Text!, "scope marker names the exact launched mission");
            await runnerA.SendAsync(WorkspaceAccepted(scopeFile, binding.RepositoryFingerprint)).ConfigureAwait(false);

            HarborWorkspaceOperationRequest mcpConfig = await runnerA.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            AssertEqual(HarborWorkspaceOperationEnum.WriteText, mcpConfig.Operation, "MCP config is written through the owner");
            AssertEqual(".armada/runtime/" + scope.Captain.Id + "/armada-mcp.json", mcpConfig.RelativePath!, "MCP config stays inside the captain's runtime folder");
            AssertContains("https://admiral.example/mcp", mcpConfig.Text ?? String.Empty, "MCP config uses the configured endpoint");
            AssertContains("${" + McpLaunchCredential.EnvironmentVariable + "}", mcpConfig.Text ?? String.Empty,
                "MCP config refers to the scoped token by environment name");
            await runnerA.SendAsync(WorkspaceAccepted(mcpConfig, binding.RepositoryFingerprint)).ConfigureAwait(false);

            HarborWorkspaceOperationRequest gitExclude = await runnerA.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            AssertEqual(HarborWorkspaceOperationEnum.AddGitExclude, gitExclude.Operation, "generated runtime files are excluded through the owner");
            AssertEqual(".armada/runtime/", gitExclude.RelativePath!, "exclude covers only generated runtime configuration");
            await runnerA.SendAsync(WorkspaceAccepted(gitExclude, binding.RepositoryFingerprint)).ConfigureAwait(false);

            int processId = await launchTask.ConfigureAwait(false);
            HarborLaunchRequest launch = await runnerA.NextAsync<HarborLaunchRequest>().ConfigureAwait(false);
            AssertEqual(scope.Dock.Id, launch.DockId, "launch preserves dock identity");
            AssertEqual(binding.CheckoutId, launch.CheckoutBinding!.CheckoutId, "launch preserves the pinned checkout identity");
            AssertEqual(runnerDirectory, launch.WorkingDirectory, "launch uses only the authenticated runner's canonical path");
            AssertNotNull(launch.MissionConfiguration, "launch carries private mission runtime configuration");
            AuthContext? missionToken = harness.SessionTokens.ValidateToken(launch.MissionConfiguration!.Token);
            AssertNotNull(missionToken, "launch token authenticates with the same service as the MCP endpoint");
            AssertEqual(scope.Mission.Id, missionToken!.MissionId, "scoped token is tied to this exact mission");
            AssertEqual(harness.OwnerA.TenantId, missionToken.TenantId, "scoped token keeps the mission owner's tenant");
            AssertEqual(harness.OwnerA.UserId, missionToken.UserId, "scoped token keeps the mission owner's user");
            AssertFalse(missionToken.IsAdmin, "mission token does not grant global administrator access");
            AssertFalse((scopeFile.Text ?? String.Empty).Contains(launch.MissionConfiguration.Token, StringComparison.Ordinal), "scope file does not contain the token value");
            AssertFalse((mcpConfig.Text ?? String.Empty).Contains(launch.MissionConfiguration.Token, StringComparison.Ordinal), "MCP config does not contain the token value");
            AssertFalse((launch.Prompt ?? String.Empty).Contains(launch.MissionConfiguration.Token, StringComparison.Ordinal), "launch prompt does not contain the token value");
            AssertFalse(String.Join("\n", launch.Arguments).Contains(launch.MissionConfiguration.Token, StringComparison.Ordinal), "CLI arguments do not contain the token value");
            AssertTrue(launch.Arguments.Contains("--mcp-config"), "Claude receives its scoped MCP config path");
            int configArgument = launch.Arguments.IndexOf("--mcp-config");
            AssertEqual(runnerDirectory + "/.armada/runtime/" + scope.Captain.Id + "/armada-mcp.json",
                launch.Arguments[configArgument + 1], "CLI arguments point to the host-scoped config written before launch");
            AssertTrue(await runnerB.NoMessageAsync().ConfigureAwait(false), "the configured route receives no bound-checkout launch");

            HarborWorkspaceOperationRequest exactDockRequest = HarborCheckoutService.CreateRequest(scope.Dock, HarborWorkspaceOperationEnum.InspectCheckout);
            Task<HarborWorkspaceOperationResult> exactDockTask = harness.Workspace.ExecuteAsync(missionToken!, exactDockRequest);
            HarborWorkspaceOperationRequest exactDockWire = await runnerA.NextAsync<HarborWorkspaceOperationRequest>().ConfigureAwait(false);
            AssertEqual(scope.Dock.Id, exactDockWire.DockId, "the mission token may inspect its exact assigned dock");
            await runnerA.SendAsync(WorkspaceAccepted(exactDockWire, binding.RepositoryFingerprint)).ConfigureAwait(false);
            AssertTrue((await exactDockTask.ConfigureAwait(false)).Accepted, "the matching dock passes mission-scoped authorization");

            Dock siblingDock = await harness.Driver.Docks.CreateAsync(new Dock(scope.Dock.VesselId)
            {
                TenantId = scope.Dock.TenantId,
                UserId = scope.Dock.UserId,
                CaptainId = scope.Captain.Id,
                BranchName = "feature/sibling-dock",
                WorktreePath = HarborCheckoutAddress.ForDock("sibling-dock"),
                HostCheckout = binding
            }).ConfigureAwait(false);
            HarborWorkspaceOperationRequest siblingDockRequest = HarborCheckoutService.CreateRequest(siblingDock, HarborWorkspaceOperationEnum.InspectCheckout);
            siblingDockRequest.TimeoutSeconds = 1;
            HarborWorkspaceOperationResult siblingDockResult = await harness.Workspace.ExecuteAsync(missionToken!, siblingDockRequest).ConfigureAwait(false);
            AssertFalse(siblingDockResult.Accepted, "a mission token cannot inspect another dock owned by the same captain");
            AssertEqual("workspace_mission_dock_mismatch", siblingDockResult.Reason, "the refusal names the mission-to-dock scope failure");
            AssertTrue(await runnerA.NoMessageAsync().ConfigureAwait(false), "the sibling-dock request never reaches the runner");

            string finalMessagePath = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(scope.LogFilePath)!)!, "final-messages", scope.Mission.Id + ".txt");
            bool finalArtifactWasPresent = false;
            TaskCompletionSource<string?> consumedFinalMessage = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            scope.Admiral.ExitHandler = _ =>
            {
                finalArtifactWasPresent = File.Exists(finalMessagePath);
                consumedFinalMessage.TrySetResult(scope.Handler.GetAndClearMissionOutput(scope.Mission.Id));
                return Task.CompletedTask;
            };

            await runnerA.SendAsync(new HarborStarted { JobId = launch.JobId, ProcessId = 9101 }).ConfigureAwait(false);
            await runnerA.SendAsync(Output(launch.JobId, 0,
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"The Harbor-owned mission is ready.\"}]}}\n")).ConfigureAwait(false);
            await runnerA.SendAsync(Output(launch.JobId, 1,
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"The Harbor-owned mission is ready.\"}\n")).ConfigureAwait(false);
            await runnerA.SendAsync(new HarborExited { JobId = launch.JobId, ExitCode = 0 }).ConfigureAwait(false);
            RecordedProcessExit exit = await scope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
            AssertEqual(processId, exit.ProcessId, "remote exit returns through the mission lifecycle");
            AssertEqual(0, exit.ExitCode, "runner exit status is retained");
            string? finalMessage = await consumedFinalMessage.Task.WaitAsync(Wait).ConfigureAwait(false);
            AssertTrue(finalArtifactWasPresent, "the Admiral-local final-message artifact exists before the exit consumer reads it");
            AssertEqual("The Harbor-owned mission is ready.", finalMessage,
                "the Admiral-side exit consumer reads the final reply captured from runner output");
            AssertFalse(File.Exists(finalMessagePath), "the Admiral-side consumer removes the consumed final-message artifact");
        }

        private async Task HarborMissionLaunch_BoundCheckoutFailsClosedWhenDisabledOrOffline()
        {
            await using Harness harness = await Harness.StartAsync().ConfigureAwait(false);
            using MissionScope disabled = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a", harborEnabled: false).ConfigureAwait(false);
            await SetMissionDockBindingAsync(harness, disabled, harness.OwnerA, "hbr_a").ConfigureAwait(false);
            AssertEqual("harbor_checkout_disabled", await LaunchRefusalAsync(disabled).ConfigureAwait(false),
                "a durable remote dock does not fall back to a local process when Harbor is disabled");

            using MissionScope offline = await MissionScope.CreateAsync(harness, harness.OwnerA, "hbr_a").ConfigureAwait(false);
            await SetMissionDockBindingAsync(harness, offline, harness.OwnerA, "hbr_a").ConfigureAwait(false);
            AssertEqual("harbor_workspace_unavailable", await LaunchRefusalAsync(offline).ConfigureAwait(false),
                "a durable remote dock does not fall back when its authenticated workspace channel is unavailable");
        }

        private static async Task<HarborCheckoutBinding> SetMissionDockBindingAsync(Harness harness, MissionScope scope, Principal owner, string runnerId)
        {
            HarborCheckoutBinding binding = new HarborCheckoutBinding
            {
                RunnerId = runnerId, EnrollmentGeneration = 1, TenantId = owner.TenantId, UserId = owner.UserId,
                RepositoryGrantId = "grant_" + runnerId, RepositoryFingerprint = new string('c', 64),
                CheckoutId = "hco_" + Guid.NewGuid().ToString("N"), BaseCommit = new string('d', 40), BranchName = scope.Dock.BranchName!,
                Revision = "revision_" + runnerId
            };
            scope.Dock.HostCheckout = binding;
            scope.Dock.WorktreePath = HarborCheckoutAddress.ForDock(scope.Dock.Id);
            await harness.Driver.Docks.DeleteAsync(scope.Dock.Id).ConfigureAwait(false);
            await harness.Driver.Docks.CreateAsync(scope.Dock).ConfigureAwait(false);
            return binding;
        }

        private static HarborWorkspaceOperationRequest RepositoryInspectRequest(string runnerId, string vesselId, string grantId, string fingerprint)
        {
            return new HarborWorkspaceOperationRequest
            {
                Operation = HarborWorkspaceOperationEnum.InspectRepository, RunnerId = runnerId, VesselId = vesselId,
                RepositoryGrantId = grantId, ExpectedRepositoryFingerprint = fingerprint
            };
        }

        private static HarborWorkspaceOperationRequest BoundWorkspaceRequest(HarborCheckoutBinding binding, string dockId, string vesselId, string fingerprint)
        {
            return new HarborWorkspaceOperationRequest
            {
                Operation = HarborWorkspaceOperationEnum.InspectCheckout, RunnerId = binding.RunnerId, Binding = binding,
                DockId = dockId, VesselId = vesselId, RepositoryGrantId = binding.RepositoryGrantId,
                ExpectedRepositoryFingerprint = fingerprint
            };
        }

        private static HarborWorkspaceOperationResult WorkspaceAccepted(HarborWorkspaceOperationRequest request, string fingerprint)
        {
            return new HarborWorkspaceOperationResult
            {
                RequestId = request.RequestId, CorrelationId = request.RequestId, Operation = request.Operation,
                Binding = request.Binding, RepositoryFingerprint = fingerprint, Accepted = true
            };
        }

        private static HarborLaunchRequest Plan()
        {
            return new HarborLaunchRequest { Runtime = "claude", WorkingDirectory = "/work/dock", Prompt = "do the work" };
        }

        private static HarborOutput Output(string jobId, long sequence, string data)
        {
            return new HarborOutput { JobId = jobId, Sequence = sequence, Stream = HarborOutputStreamEnum.Stdout, Data = data };
        }

        private static List<string> Data(HarborJobSnapshot snapshot)
        {
            List<string> data = new List<string>();
            foreach (HarborOutput output in snapshot.Output) data.Add(output.Data);
            return data;
        }

        private async Task VerifyCancelledOwnershipRestoreAsync(AgentLifecycleHandler handler, HarborJobSnapshot job)
        {
            int processId = job.AdmiralProcessId!.Value;
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (IDisposable launchBoundary = await MissionExecutionBoundary.LaunchWrites.AcquireAsync(job.MissionId!).ConfigureAwait(false))
            {
                Task<HarborJobRecoveryResult> restoration = handler.RestoreAsync(job, cancellation.Token);
                bool cancelled = false;
                try
                {
                    AssertTrue(await WaitUntilAsync(() => ProcessSupervisor.IsSyntheticProcessAlive(processId)).ConfigureAwait(false),
                        "restore reserves the synthetic process before waiting for launch ownership");
                    cancellation.Cancel();
                    try { await restoration.WaitAsync(Wait).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    AssertTrue(cancelled, "cancellation interrupts the blocked ownership wait");
                    AssertFalse(ProcessSupervisor.IsSyntheticProcessAlive(processId), "cancelled restoration releases its process reservation for the retry");
                }
                finally
                {
                    cancellation.Cancel();
                    ProcessSupervisor.UnregisterSyntheticProcess(processId);
                }
            }
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(25).ConfigureAwait(false);
            }
            return condition();
        }

        private sealed class Principal
        {
            public string TenantId { get; }
            public string UserId { get; }
            public string CredentialId { get; }
            public string BearerToken { get; }
            public AuthContext Auth { get; }

            public Principal(string tenantId, string userId, string credentialId, string bearerToken, bool isTenantAdmin, bool isAdmin = false)
            {
                TenantId = tenantId;
                UserId = userId;
                CredentialId = credentialId;
                BearerToken = bearerToken;
                Auth = AuthContext.Authenticated(tenantId, userId, isAdmin, isAdmin || isTenantAdmin, "Bearer", credentialId, userId);
            }
        }

        private sealed class Harness : IAsyncDisposable
        {
            private readonly TestDatabase _Database;
            private readonly List<AdmiralInstance> _Admirals = new List<AdmiralInstance>();
            private readonly ArmadaSettings _Settings;
            private readonly AuthenticationService _Authentication;
            private readonly LoggingModule _Logging;
            private readonly HttpClient _Http;

            public Armada.Core.Database.DatabaseDriver Driver => _Database.Driver;
            public AdmiralInstance Primary => _Admirals[0];
            public HarborRunnerSessionRegistry Registry => Primary.Registry;
            public HarborJobCoordinator Coordinator => Primary.Coordinator;
            public HarborJobService Jobs => Primary.Jobs;
            public HarborWorkspaceCoordinator Workspace => Primary.Workspace;
            public Armada.Core.Services.Interfaces.ISessionTokenService SessionTokens { get; }
            public Principal Administrator { get; }
            public Principal OwnerA { get; }
            public Principal OwnerB { get; }

            private Harness(TestDatabase database, ArmadaSettings settings, AuthenticationService authentication, LoggingModule logging,
                AdmiralInstance primary, Principal administrator, Principal ownerA, Principal ownerB,
                Armada.Core.Services.Interfaces.ISessionTokenService sessionTokens)
            {
                _Database = database;
                _Settings = settings;
                _Authentication = authentication;
                _Logging = logging;
                _Admirals.Add(primary);
                _Http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + primary.Port + "/"), Timeout = Wait };
                SessionTokens = sessionTokens;
                Administrator = administrator;
                OwnerA = ownerA;
                OwnerB = ownerB;
            }

            /// <summary>
            /// Start a second Admiral over the same database, as a restarted process would: the restart
            /// reconciliation runs first, then a fresh registry, coordinator and link are served.
            /// </summary>
            public async Task<AdmiralInstance> RestartAdmiralAsync(IHarborJobRecoveryHost recoveryHost, Action<HarborJobCoordinator> configureRecovery)
            {
                AdmiralInstance restarted = await AdmiralInstance.StartAsync(_Database, _Settings, _Authentication, _Logging, recoveryHost, configureRecovery).ConfigureAwait(false);
                _Admirals.Add(restarted);
                return restarted;
            }

            public async Task<HttpReply> RequestAsync(Principal? caller, System.Net.Http.HttpMethod method, string path, object? body = null,
                AdmiralInstance? target = null)
            {
                Uri? targetUri = target == null ? null : new Uri("http://127.0.0.1:" + target.Port + "/" + path);
                using (HttpRequestMessage request = new HttpRequestMessage(method, targetUri == null ? path : targetUri.ToString()))
                {
                    if (caller != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.BearerToken);
                    if (body != null)
                        request.Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = await _Http.SendAsync(request).ConfigureAwait(false))
                    {
                        return new HttpReply(response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    }
                }
            }

            public async Task<List<string>> ListLaunchKeysAsync(Principal caller)
            {
                HttpReply reply = await RequestAsync(caller, System.Net.Http.HttpMethod.Get, "api/v1/harbor-runners/jobs").ConfigureAwait(false);
                if (reply.StatusCode != HttpStatusCode.OK) throw new Exception("Job list failed with " + reply.StatusCode + ": " + reply.Body);
                HarborJobListResponse? list = JsonSerializer.Deserialize<HarborJobListResponse>(reply.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                List<string> keys = new List<string>();
                foreach (HarborJobRecord job in list?.Jobs ?? new List<HarborJobRecord>()) keys.Add(job.LaunchKey);
                keys.Sort(StringComparer.Ordinal);
                return keys;
            }

            public static async Task<Harness> StartAsync(TimeSpan? stopConfirmationTimeout = null)
            {
                TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TenantMetadata tenant = await database.Driver.Tenants.CreateAsync(new TenantMetadata("harbor-transport-" + Guid.NewGuid().ToString("N"))).ConfigureAwait(false);
                Principal administrator = await CreatePrincipalAsync(database, tenant.Id, "admin", true).ConfigureAwait(false);
                Principal ownerA = await CreatePrincipalAsync(database, tenant.Id, "owner-a", false).ConfigureAwait(false);
                Principal ownerB = await CreatePrincipalAsync(database, tenant.Id, "owner-b", false).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArmadaSettings settings = new ArmadaSettings();
                settings.Harbor.Enabled = true;
                Armada.Core.Services.Interfaces.ISessionTokenService sessionTokens = new SessionTokenService();
                AuthenticationService authentication = new AuthenticationService(database.Driver, sessionTokens, settings, logging);
                AdmiralInstance primary = await AdmiralInstance.StartAsync(database, settings, authentication, logging, stopConfirmationTimeout: stopConfirmationTimeout).ConfigureAwait(false);
                return new Harness(database, settings, authentication, logging, primary, administrator, ownerA, ownerB, sessionTokens);
            }

            public async Task EnrollBothAsync()
            {
                if (await EnrollAsync(Administrator, "hbr_a", OwnerA).ConfigureAwait(false) != HttpStatusCode.OK) throw new Exception("Enrollment of hbr_a failed.");
                if (await EnrollAsync(Administrator, "hbr_b", OwnerB).ConfigureAwait(false) != HttpStatusCode.OK) throw new Exception("Enrollment of hbr_b failed.");
            }

            public async Task<HttpStatusCode> EnrollAsync(Principal caller, string runnerId, Principal owner)
            {
                using HttpRequestMessage request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, "api/v1/harbor-runners/enrollments");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.BearerToken);
                request.Content = new StringContent(JsonSerializer.Serialize(new { runnerId = runnerId, credentialId = owner.CredentialId }), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _Http.SendAsync(request).ConfigureAwait(false);
                return response.StatusCode;
            }

            public async Task<HttpStatusCode> RevokeAsync(Principal caller, string runnerId)
            {
                using HttpRequestMessage request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, "api/v1/harbor-runners/enrollments/" + runnerId + "/revoke");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.BearerToken);
                using HttpResponseMessage response = await _Http.SendAsync(request).ConfigureAwait(false);
                return response.StatusCode;
            }

            public Task<FakeRunner> ConnectAsync(Principal principal, IDictionary<string, string>? extraHeaders = null)
            {
                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer " + principal.BearerToken
                };
                if (extraHeaders != null) foreach (KeyValuePair<string, string> header in extraHeaders) headers[header.Key] = header.Value;
                return ConnectAsync(headers);
            }

            public Task<FakeRunner> ConnectAsync(IDictionary<string, string> headers)
            {
                return FakeRunner.ConnectAsync(new Uri("ws://127.0.0.1:" + Primary.Port + _Settings.Harbor.LinkPath), headers);
            }

            public Task<FakeRunner> ConnectAsync(AdmiralInstance admiral, Principal principal)
            {
                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer " + principal.BearerToken
                };
                return FakeRunner.ConnectAsync(new Uri("ws://127.0.0.1:" + admiral.Port + _Settings.Harbor.LinkPath), headers);
            }

            public async ValueTask DisposeAsync()
            {
                _Http.Dispose();
                for (int index = _Admirals.Count - 1; index >= 0; index--) _Admirals[index].Dispose();
                await Task.Yield();
                _Database.Dispose();
            }

            public Task<Principal> AddPrincipalAsync(string tenantId, string name, bool isTenantAdmin, bool isAdmin)
            {
                return CreatePrincipalAsync(_Database, tenantId, name, isTenantAdmin, isAdmin);
            }

            public async Task<string> AddTenantAsync()
            {
                TenantMetadata tenant = await _Database.Driver.Tenants.CreateAsync(new TenantMetadata("harbor-transport-" + Guid.NewGuid().ToString("N"))).ConfigureAwait(false);
                return tenant.Id;
            }

            private static async Task<Principal> CreatePrincipalAsync(TestDatabase database, string tenantId, string name, bool isTenantAdmin, bool isAdmin = false)
            {
                UserMaster user = new UserMaster(tenantId, name + "-" + Guid.NewGuid().ToString("N") + "@example.invalid", "harbor-test-password");
                user.IsTenantAdmin = isTenantAdmin;
                user.IsAdmin = isAdmin;
                user = await database.Driver.Users.CreateAsync(user).ConfigureAwait(false);
                Credential credential = await database.Driver.Credentials.CreateAsync(new Credential(tenantId, user.Id)).ConfigureAwait(false);
                return new Principal(tenantId, user.Id, credential.Id, credential.BearerToken, isTenantAdmin, isAdmin);
            }

            private static int ReservePort()
            {
                using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        /// <summary>One in-process Admiral: registry, store-backed coordinator, link, enrollment and job routes.</summary>
        private sealed class AdmiralInstance : IDisposable
        {
            private readonly Webserver _Server;
            private readonly CancellationTokenSource _Cancellation;

            public int Port { get; }
            public HarborRunnerSessionRegistry Registry { get; }
            public HarborJobCoordinator Coordinator { get; }
            public HarborJobService Jobs { get; }
            public HarborWorkspaceCoordinator Workspace { get; }

            private AdmiralInstance(Webserver server, CancellationTokenSource cancellation, int port, HarborRunnerSessionRegistry registry, HarborJobCoordinator coordinator, HarborJobService jobs, HarborWorkspaceCoordinator workspace)
            {
                _Server = server;
                _Cancellation = cancellation;
                Port = port;
                Registry = registry;
                Coordinator = coordinator;
                Jobs = jobs;
                Workspace = workspace;
            }

            public static async Task<AdmiralInstance> StartAsync(TestDatabase database, ArmadaSettings settings, AuthenticationService authentication, LoggingModule logging,
                IHarborJobRecoveryHost? recoveryHost = null, Action<HarborJobCoordinator>? configureRecovery = null, TimeSpan? stopConfirmationTimeout = null)
            {
                HarborRunnerEnrollmentService enrollments = new HarborRunnerEnrollmentService(database.Driver);
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, enrollments);
                HarborJobCoordinator coordinator = new HarborJobCoordinator(registry, enrollments, database.Driver.HarborJobs, logging, stopConfirmationTimeout);
                HarborJobService jobs = new HarborJobService(coordinator, database.Driver.HarborJobs, enrollments);
                HarborWorkspaceCoordinator workspace = new HarborWorkspaceCoordinator(registry, enrollments, new DatabaseHarborWorkspaceBindingVerifier(database.Driver), logging);
                HarborLinkEndpoint endpoint = new HarborLinkEndpoint(settings.Harbor, authentication, registry, coordinator, logging, workspace);

                int port = ReservePort();
                WebserverSettings webserverSettings = new WebserverSettings();
                webserverSettings.Hostname = "127.0.0.1";
                webserverSettings.Port = port;
                webserverSettings.WebSockets.Enable = true;
                Webserver server = new Webserver(webserverSettings, async (HttpContextBase ctx) =>
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.Send().ConfigureAwait(false);
                });
                server.WebSocket(settings.Harbor.LinkPath, endpoint.HandleWebSocketAsync);
                Func<HttpContextBase, Task<AuthContext>> authenticate = ctx => authentication.AuthenticateAsync(ctx.Request.Headers.Get("Authorization"), ctx.Request.Headers.Get("X-Token"), ctx.Request.Headers.Get("X-Api-Key"));
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                new HarborRunnerEnrollmentRoutes(enrollments, jsonOptions).Register(server, authenticate, new AuthorizationService());
                new HarborJobRoutes(jobs, jsonOptions).Register(server, authenticate, new AuthorizationService());
                CancellationTokenSource cancellation = new CancellationTokenSource();
                if (recoveryHost != null) configureRecovery?.Invoke(coordinator);
                if (recoveryHost != null && !await coordinator.InitializeRecoveryAsync(recoveryHost, cancellation.Token).ConfigureAwait(false))
                {
                    cancellation.Dispose();
                    server.Dispose();
                    throw new InvalidOperationException("Harbor job recovery did not complete before the test link started.");
                }
                server.Start(cancellation.Token);
                return new AdmiralInstance(server, cancellation, port, registry, coordinator, jobs, workspace);
            }

            public void Dispose()
            {
                _Cancellation.Cancel();
                try
                {
                    _Server.Stop();
                }
                catch (Exception exception)
                {
                    Console.WriteLine("Harbor transport test server stop failed: " + exception.Message);
                }
                _Server.Dispose();
                _Cancellation.Dispose();
            }

            private static int ReservePort()
            {
                using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
                {
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    listener.Stop();
                    return port;
                }
            }
        }

        /// <summary>An HTTP status and body.</summary>
        private sealed class HttpReply
        {
            public HttpStatusCode StatusCode { get; }
            public string Body { get; }

            public HttpReply(HttpStatusCode statusCode, string body)
            {
                StatusCode = statusCode;
                Body = body;
            }
        }

        /// <summary>
        /// A routed mission on the real agent lifecycle: a captain, mission and dock in the harness database, a
        /// Harbor route for the captain, and an Admiral that records the exits the lifecycle hands over.
        /// </summary>
        private sealed class MissionScope : IDisposable
        {
            private readonly string _Root;
            private readonly ConcurrentQueue<string> _LifecycleEvents;

            public AgentLifecycleHandler Handler { get; }
            public RecordingAdmiral Admiral { get; }
            public Captain Captain { get; }
            public Mission Mission { get; }
            public Dock Dock { get; }
            public string LogFilePath { get; }

            private MissionScope(string root, AgentLifecycleHandler handler, RecordingAdmiral admiral, Captain captain, Mission mission, Dock dock,
                string logFilePath, ConcurrentQueue<string> lifecycleEvents)
            {
                _Root = root;
                _LifecycleEvents = lifecycleEvents;
                Handler = handler;
                Admiral = admiral;
                Captain = captain;
                Mission = mission;
                Dock = dock;
                LogFilePath = logFilePath;
            }

            public static async Task<MissionScope> CreateAsync(Harness harness, Principal missionOwner, string runnerId, Action<Captain>? configureCaptain = null,
                Func<LoggingModule, ArmadaSettings, AgentRuntimeFactory>? runtimeFactory = null, Func<string, ArmadaSettings, Mission, Task>? prepare = null, bool harborEnabled = true)
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_harbor_mission_" + Guid.NewGuid().ToString("N"));
                string worktree = Path.Combine(root, "dock");
                Directory.CreateDirectory(worktree);

                ArmadaSettings settings = new ArmadaSettings();
                settings.LogDirectory = Path.Combine(root, "logs");
                settings.Harbor.Enabled = harborEnabled;

                Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("Harbor mission vessel " + Guid.NewGuid().ToString("N"), "https://example.invalid/harbor-mission")
                {
                    TenantId = missionOwner.TenantId,
                    UserId = missionOwner.UserId
                }).ConfigureAwait(false);
                Mission mission = new Mission("Harbor mission")
                {
                    Persona = "Worker",
                    BranchName = "feature/harbor-mission",
                    Status = MissionStatusEnum.Assigned,
                    AssignmentState = MissionAssignmentStateEnum.Assigned,
                    TenantId = missionOwner.TenantId,
                    UserId = missionOwner.UserId,
                    VesselId = vessel.Id
                };
                Captain captain = new Captain("harbor-captain-" + Guid.NewGuid().ToString("N").Substring(0, 8), AgentRuntimeEnum.ClaudeCode)
                {
                    State = CaptainStateEnum.Working,
                    TenantId = missionOwner.TenantId,
                    UserId = missionOwner.UserId
                };
                configureCaptain?.Invoke(captain);
                mission.CaptainId = captain.Id;
                Dock dock = new Dock(vessel.Id)
                {
                    TenantId = missionOwner.TenantId,
                    UserId = missionOwner.UserId,
                    CaptainId = captain.Id,
                    BranchName = mission.BranchName,
                    WorktreePath = worktree
                };
                mission.DockId = dock.Id;
                settings.Harbor.MissionRoutes.Add(new HarborMissionRoute { RunnerId = runnerId, CaptainId = captain.Id });
                if (prepare != null) await prepare(root, settings, mission).ConfigureAwait(false);
                await harness.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                await harness.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                await harness.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                captain.CurrentMissionId = mission.Id;
                captain.CurrentDockId = dock.Id;
                await harness.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                RecordingAdmiral admiral = RecordingAdmiral.Create();
                ConcurrentQueue<string> lifecycleEvents = new ConcurrentQueue<string>();
                AgentLifecycleHandler handler = new AgentLifecycleHandler(
                    logging,
                    harness.Driver,
                    settings,
                    runtimeFactory != null ? runtimeFactory(logging, settings) : new AgentRuntimeFactory(logging),
                    admiral.Service,
                    new MessageTemplateService(logging),
                    null,
                    null,
                    (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) =>
                    {
                        lifecycleEvents.Enqueue(eventType);
                        return Task.CompletedTask;
                    }, sessionTokens: harness.SessionTokens);
                handler.SetHarborHost(new HarborMissionExecutor(harness.Coordinator, logging));
                return new MissionScope(root, handler, admiral, captain, mission, dock,
                    Path.Combine(settings.LogDirectory, "missions", mission.Id + ".log"), lifecycleEvents);
            }

            public static MissionScope CreateRecovery(Harness harness, string runnerId, MissionScope previous)
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.LogDirectory = Path.Combine(previous._Root, "logs");
                settings.Harbor.Enabled = true;
                settings.Harbor.MissionRoutes.Add(new HarborMissionRoute { RunnerId = runnerId, CaptainId = previous.Captain.Id });

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                RecordingAdmiral admiral = RecordingAdmiral.Create();
                ConcurrentQueue<string> lifecycleEvents = new ConcurrentQueue<string>();
                AgentLifecycleHandler handler = new AgentLifecycleHandler(
                    logging,
                    harness.Driver,
                    settings,
                    new AgentRuntimeFactory(logging),
                    admiral.Service,
                    new MessageTemplateService(logging),
                    null,
                    null,
                    (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) =>
                    {
                        lifecycleEvents.Enqueue(eventType);
                        return Task.CompletedTask;
                    }, sessionTokens: harness.SessionTokens);
                handler.SetHarborHost(new HarborMissionExecutor(harness.Coordinator, logging));
                return new MissionScope(previous._Root, handler, admiral, previous.Captain, previous.Mission, previous.Dock, previous.LogFilePath, lifecycleEvents);
            }

            public void UseCoordinator(HarborJobCoordinator coordinator)
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                Handler.SetHarborHost(new HarborMissionExecutor(coordinator, logging));
            }

            public bool HasLifecycleEvent(string eventType)
            {
                return _LifecycleEvents.Contains(eventType);
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(_Root)) Directory.Delete(_Root, true);
                }
                catch (IOException exception)
                {
                    Console.WriteLine("Harbor mission test directory cleanup failed: " + exception.Message);
                }
            }
        }

        private sealed class FakeRunner : IAsyncDisposable
        {
            private readonly ClientWebSocket _Socket;
            private readonly Channel<HarborMessage> _Inbound = Channel.CreateUnbounded<HarborMessage>();
            private readonly TaskCompletionSource<bool> _Closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Task _ReceiveLoop;
            private HarborHandshake? _AcceptedHandshake;

            private FakeRunner(ClientWebSocket socket)
            {
                _Socket = socket;
                _ReceiveLoop = Task.Run(ReceiveLoopAsync);
            }

            public static async Task<FakeRunner> ConnectAsync(Uri uri, IDictionary<string, string> headers)
            {
                ClientWebSocket socket = new ClientWebSocket();
                foreach (KeyValuePair<string, string> header in headers) socket.Options.SetRequestHeader(header.Key, header.Value);
                using CancellationTokenSource timeout = new CancellationTokenSource(Wait);
                await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
                return new FakeRunner(socket);
            }

            public async Task<HarborHandshakeAck> HandshakeAsync(string runnerId, int maxConcurrentJobs = 4, bool workspaceCapability = false)
            {
                List<HarborCapability> capabilities = new List<HarborCapability> { new HarborCapability { Name = "claude" } };
                if (workspaceCapability) capabilities.Add(new HarborCapability { Name = HarborWorkspaceProtocol.CapabilityName, Available = true });
                HarborHandshake handshake = new HarborHandshake
                {
                    CorrelationId = "handshake-" + runnerId,
                    HarborId = runnerId,
                    Name = "fake " + runnerId,
                    ProtocolVersion = HarborProtocol.Version,
                    MaxConcurrentJobs = maxConcurrentJobs,
                    Capabilities = capabilities
                };
                await SendAsync(handshake).ConfigureAwait(false);
                HarborHandshakeAck ack = await NextAsync<HarborHandshakeAck>().ConfigureAwait(false);
                if (ack.Accepted) _AcceptedHandshake = handshake;
                return ack;
            }

            public async Task SendAsync(HarborMessage message)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(HarborProtocol.Serialize(message));
                using CancellationTokenSource timeout = new CancellationTokenSource(Wait);
                await _Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            }

            public async Task<T> NextAsync<T>() where T : HarborMessage
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(Wait);
                HarborMessage message;
                try
                {
                    message = await _Inbound.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    throw new Exception("Expected " + typeof(T).Name + " but the link closed.");
                }
                catch (OperationCanceledException)
                {
                    throw new Exception("Expected " + typeof(T).Name + " but none arrived.");
                }
                if (message is T typed) return typed;
                throw new Exception("Expected " + typeof(T).Name + " but received " + HarborProtocol.Serialize(message));
            }

            /// <summary>
            /// Proves that the Admiral has sent this runner nothing, without waiting out a quiet window. The link
            /// handles its frames in order through one send lock, and a handshaken link answers a repeated handshake
            /// with one harbor_handshake_duplicate error and no other effect. So anything the Admiral sent before it
            /// read the probe arrives ahead of that answer: a command it sent before the caller's call returned, and
            /// any refusal of a frame this runner sent earlier.
            /// </summary>
            public async Task<bool> NoMessageAsync()
            {
                if (_AcceptedHandshake == null) throw new InvalidOperationException("The quiet probe needs a handshaken link.");
                string correlationId = "quiet-probe-" + Guid.NewGuid().ToString("N");
                await SendAsync(new HarborHandshake
                {
                    CorrelationId = correlationId,
                    HarborId = _AcceptedHandshake.HarborId,
                    Name = _AcceptedHandshake.Name,
                    ProtocolVersion = _AcceptedHandshake.ProtocolVersion,
                    MaxConcurrentJobs = _AcceptedHandshake.MaxConcurrentJobs,
                    Capabilities = _AcceptedHandshake.Capabilities
                }).ConfigureAwait(false);
                HarborMessage first = await NextAsync<HarborMessage>().ConfigureAwait(false);
                if (first is HarborError answer && answer.CorrelationId == correlationId && answer.Message == "harbor_handshake_duplicate")
                    return true;
                throw new Exception("Expected no message but received " + HarborProtocol.Serialize(first));
            }

            public async Task<bool> WaitClosedAsync()
            {
                Task finished = await Task.WhenAny(_Closed.Task, Task.Delay(Wait)).ConfigureAwait(false);
                return finished == _Closed.Task;
            }

            public async Task CloseAsync()
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(Wait);
                if (_Socket.State == WebSocketState.Open)
                    await _Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "runner closing", timeout.Token).ConfigureAwait(false);
                await WaitClosedAsync().ConfigureAwait(false);
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (_Socket.State == WebSocketState.Open || _Socket.State == WebSocketState.CloseReceived)
                    {
                        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await _Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is WebSocketException || exception is OperationCanceledException || exception is ObjectDisposedException)
                {
                    _Socket.Abort();
                }
                await Task.WhenAny(_ReceiveLoop, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                _Socket.Dispose();
            }

            private async Task ReceiveLoopAsync()
            {
                byte[] buffer = new byte[65536];
                StringBuilder text = new StringBuilder();
                try
                {
                    while (_Socket.State == WebSocketState.Open || _Socket.State == WebSocketState.CloseSent)
                    {
                        WebSocketReceiveResult result = await _Socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        if (!result.EndOfMessage) continue;
                        _Inbound.Writer.TryWrite(HarborProtocol.Deserialize(text.ToString()));
                        text.Clear();
                    }
                }
                catch (Exception exception) when (exception is WebSocketException || exception is ObjectDisposedException || exception is FormatException)
                {
                    Console.WriteLine("Fake Harbor runner receive ended: " + exception.Message);
                }
                finally
                {
                    _Inbound.Writer.TryComplete();
                    _Closed.TrySetResult(true);
                }
            }
        }
    }
}
