namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Net.Sockets;
    using System.Net.WebSockets;
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
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
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
        private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

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
                    HarborJobSnapshot lost = await launch.Completion!.WaitAsync(Wait).ConfigureAwait(false);
                    AssertEqual(HarborJobStateEnum.Lost, lost.State, "the revoked runner's job is lost, not completed");
                    AssertEqual("runner_enrollment_revoked", lost.FailureReason, "lost job carries the refusal");
                    AssertEqual(0, lost.Output.Count, "no output accepted after revocation");
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
                HarborJobSnapshot lost = await launch.Completion!.WaitAsync(Wait).ConfigureAwait(false);
                AssertEqual(HarborJobStateEnum.Lost, lost.State, "stale generation's job is lost");
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
                await original.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 700 }).ConfigureAwait(false);
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

                HarborJobSnapshot lost = await launch.Completion!.WaitAsync(Wait).ConfigureAwait(false);
                AssertEqual(HarborJobStateEnum.Lost, lost.State, "revoked runner's job is lost");
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
                AssertEqual(HarborJobStateEnum.Lost, after!.State, "old job stays lost");
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
                        AssertFalse(String.Join(" ", launch.Arguments).Contains(McpLaunchCredential.Token), "the launch credential is not in the arguments");
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
                        await scope.Handler.HandleStopAgentAsync(captain!).ConfigureAwait(false);
                        HarborKillRequest kill = await runner.NextAsync<HarborKillRequest>().ConfigureAwait(false);
                        AssertEqual(jobId, kill.JobId, "the captain stop reaches the job on its runner");

                        await runner.SendAsync(new HarborExited { JobId = jobId, ExitCode = 137 }).ConfigureAwait(false);
                        RecordedProcessExit exit = await scope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                        AssertEqual(processId, exit.ProcessId, "stopped job exits through the lifecycle");
                        AssertEqual(137, exit.ExitCode, "stopped exit code");
                    }
                }
            });

            await RunTest("MissionStop_WhileRunnerDisconnected_ReleasesTheJobByName", async () =>
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
                        RecordedProcessExit exit = await scope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                        AssertEqual(processId, exit.ProcessId, "released job exits through the lifecycle");
                        AssertNull(exit.ExitCode, "a released job has no exit code");
                        HarborJobRecord? released = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Lost, released!.State, "released job is lost");
                        AssertEqual(HarborJobCoordinator.ReasonReleasedRunnerUnavailable, released.FailureReason, "release reason");
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

            await RunTest("MissionRevocationMidJob_LosesTheJobAndRunsTheSharedExitPath", async () =>
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

                        RecordedProcessExit exit = await scope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                        AssertEqual(processId, exit.ProcessId, "the lost job enters the shared exit and recovery path");
                        AssertNull(exit.ExitCode, "a lost job has no exit code");
                        HarborJobRecord? lost = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobStateEnum.Lost, lost!.State, "durable lost state");
                        AssertEqual("runner_enrollment_revoked", lost.FailureReason, "durable refusal reason");
                        string log = await ReadWhenContainsAsync(scope.LogFilePath, "runner_enrollment_revoked").ConfigureAwait(false);
                        AssertFalse(log.Contains("after revocation"), "no output accepted after revocation");
                    }
                }
            });

            await RunTest("DisconnectedRunner_JobsExpireAsLostAfterTheGrace", async () =>
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

                        AssertEqual(1, await harness.Coordinator.ExpireDetachedRunnersAsync(TimeSpan.FromMinutes(5), DateTime.UtcNow.AddMinutes(6)).ConfigureAwait(false), "a job past the grace period is lost");
                        RecordedProcessExit exit = await scope.Admiral.NextExitAsync(Wait).ConfigureAwait(false);
                        AssertEqual(processId, exit.ProcessId, "the expired job enters the shared exit path");
                        HarborJobRecord? lost = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                        AssertEqual(HarborJobCoordinator.ReasonRunnerDisconnected, lost!.FailureReason, "expiry reason");
                    }
                }
            });

            await RunTest("AdmiralRestart_FailsUnfinishedJobsByNameAndRefusesTheirReports", async () =>
            {
                await using (Harness harness = await Harness.StartAsync().ConfigureAwait(false))
                {
                    await harness.EnrollBothAsync().ConfigureAwait(false);
                    string jobId;
                    await using (FakeRunner runner = await harness.ConnectAsync(harness.OwnerA).ConfigureAwait(false))
                    {
                        AssertTrue((await runner.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner connected");
                        HarborLaunchResult launch = await harness.Coordinator.LaunchAsync(harness.OwnerA.Auth, "hbr_a", "mission-restart", Plan()).ConfigureAwait(false);
                        AssertTrue(launch.Accepted, launch.Reason);
                        jobId = (await runner.NextAsync<HarborLaunchRequest>().ConfigureAwait(false)).JobId;
                        await runner.SendAsync(new HarborStarted { JobId = jobId, ProcessId = 9005 }).ConfigureAwait(false);
                        AssertTrue(await runner.NoMessageAsync().ConfigureAwait(false), "start accepted");
                    }

                    AdmiralInstance restarted = await harness.RestartAdmiralAsync().ConfigureAwait(false);
                    AssertEqual(1, harness.LastReconciledJobCount, "the restart fails the one unfinished job");
                    HarborJobRecord? failed = await harness.Driver.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                    AssertEqual(HarborJobStateEnum.Lost, failed!.State, "unfinished job is lost after the restart");
                    AssertEqual(HarborJobCoordinator.ReasonAdmiralRestarted, failed.FailureReason, "restart reason");
                    AssertNotNull(failed.CompletedUtc, "restart records the completion time");
                    AssertEqual(0, await HarborJobCoordinator.ReconcileAfterRestartAsync(harness.Driver.HarborJobs, DateTime.UtcNow).ConfigureAwait(false), "reconciliation is idempotent");

                    await using (FakeRunner reconnected = await harness.ConnectAsync(restarted, harness.OwnerA).ConfigureAwait(false))
                    {
                        AssertTrue((await reconnected.HandshakeAsync("hbr_a").ConfigureAwait(false)).Accepted, "runner reconnects to the restarted Admiral");
                        await reconnected.SendAsync(new HarborHeartbeat { LiveJobIds = new List<string> { jobId } }).ConfigureAwait(false);
                        HarborError refused = await reconnected.NextAsync<HarborError>().ConfigureAwait(false);
                        AssertEqual(jobId, refused.JobId, "refusal names the job");
                        AssertEqual("harbor_job_not_rebindable", refused.Message, "a job from an earlier Admiral process is not rebound");
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
            public int LastReconciledJobCount { get; private set; }
            public Principal Administrator { get; }
            public Principal OwnerA { get; }
            public Principal OwnerB { get; }

            private Harness(TestDatabase database, ArmadaSettings settings, AuthenticationService authentication, LoggingModule logging,
                AdmiralInstance primary, Principal administrator, Principal ownerA, Principal ownerB)
            {
                _Database = database;
                _Settings = settings;
                _Authentication = authentication;
                _Logging = logging;
                _Admirals.Add(primary);
                _Http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + primary.Port + "/"), Timeout = Wait };
                Administrator = administrator;
                OwnerA = ownerA;
                OwnerB = ownerB;
            }

            /// <summary>
            /// Start a second Admiral over the same database, as a restarted process would: the restart
            /// reconciliation runs first, then a fresh registry, coordinator and link are served.
            /// </summary>
            public async Task<AdmiralInstance> RestartAdmiralAsync()
            {
                LastReconciledJobCount = await HarborJobCoordinator.ReconcileAfterRestartAsync(Driver.HarborJobs, DateTime.UtcNow).ConfigureAwait(false);
                AdmiralInstance restarted = AdmiralInstance.Start(_Database, _Settings, _Authentication, _Logging);
                _Admirals.Add(restarted);
                return restarted;
            }

            public async Task<HttpReply> RequestAsync(Principal? caller, System.Net.Http.HttpMethod method, string path)
            {
                using (HttpRequestMessage request = new HttpRequestMessage(method, path))
                {
                    if (caller != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.BearerToken);
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

            public static async Task<Harness> StartAsync()
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
                AuthenticationService authentication = new AuthenticationService(database.Driver, new SessionTokenService(), settings, logging);
                AdmiralInstance primary = AdmiralInstance.Start(database, settings, authentication, logging);
                return new Harness(database, settings, authentication, logging, primary, administrator, ownerA, ownerB);
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

            private AdmiralInstance(Webserver server, CancellationTokenSource cancellation, int port, HarborRunnerSessionRegistry registry, HarborJobCoordinator coordinator, HarborJobService jobs)
            {
                _Server = server;
                _Cancellation = cancellation;
                Port = port;
                Registry = registry;
                Coordinator = coordinator;
                Jobs = jobs;
            }

            public static AdmiralInstance Start(TestDatabase database, ArmadaSettings settings, AuthenticationService authentication, LoggingModule logging)
            {
                HarborRunnerEnrollmentService enrollments = new HarborRunnerEnrollmentService(database.Driver);
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, enrollments);
                HarborJobCoordinator coordinator = new HarborJobCoordinator(registry, enrollments, database.Driver.HarborJobs, logging);
                HarborJobService jobs = new HarborJobService(coordinator, database.Driver.HarborJobs, enrollments);
                HarborLinkEndpoint endpoint = new HarborLinkEndpoint(settings.Harbor, authentication, registry, coordinator, logging);

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
                server.Start(cancellation.Token);
                return new AdmiralInstance(server, cancellation, port, registry, coordinator, jobs);
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

            public AgentLifecycleHandler Handler { get; }
            public RecordingAdmiral Admiral { get; }
            public Captain Captain { get; }
            public Mission Mission { get; }
            public Dock Dock { get; }
            public string LogFilePath { get; }

            private MissionScope(string root, AgentLifecycleHandler handler, RecordingAdmiral admiral, Captain captain, Mission mission, Dock dock, string logFilePath)
            {
                _Root = root;
                Handler = handler;
                Admiral = admiral;
                Captain = captain;
                Mission = mission;
                Dock = dock;
                LogFilePath = logFilePath;
            }

            public static async Task<MissionScope> CreateAsync(Harness harness, Principal missionOwner, string runnerId, Action<Captain>? configureCaptain = null, Func<LoggingModule, ArmadaSettings, AgentRuntimeFactory>? runtimeFactory = null)
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_harbor_mission_" + Guid.NewGuid().ToString("N"));
                string worktree = Path.Combine(root, "dock");
                Directory.CreateDirectory(worktree);

                ArmadaSettings settings = new ArmadaSettings();
                settings.LogDirectory = Path.Combine(root, "logs");
                settings.Harbor.Enabled = true;

                Mission mission = new Mission("Harbor mission")
                {
                    Persona = "Worker",
                    BranchName = "feature/harbor-mission",
                    Status = MissionStatusEnum.Assigned,
                    AssignmentState = MissionAssignmentStateEnum.Assigned,
                    TenantId = missionOwner.TenantId,
                    UserId = missionOwner.UserId
                };
                Captain captain = new Captain("harbor-captain-" + Guid.NewGuid().ToString("N").Substring(0, 8), AgentRuntimeEnum.ClaudeCode)
                {
                    State = CaptainStateEnum.Working,
                    CurrentMissionId = mission.Id,
                    TenantId = missionOwner.TenantId
                };
                configureCaptain?.Invoke(captain);
                mission.CaptainId = captain.Id;
                settings.Harbor.MissionRoutes.Add(new HarborMissionRoute { RunnerId = runnerId, CaptainId = captain.Id });
                await harness.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                await harness.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                Dock dock = new Dock { BranchName = mission.BranchName, WorktreePath = worktree };

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                RecordingAdmiral admiral = RecordingAdmiral.Create();
                AgentLifecycleHandler handler = new AgentLifecycleHandler(
                    logging,
                    harness.Driver,
                    settings,
                    runtimeFactory != null ? runtimeFactory(logging, settings) : new AgentRuntimeFactory(logging),
                    admiral.Service,
                    new MessageTemplateService(logging),
                    null,
                    null,
                    (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) => Task.CompletedTask);
                handler.SetHarborHost(new HarborMissionExecutor(harness.Coordinator, logging));
                return new MissionScope(root, handler, admiral, captain, mission, dock, Path.Combine(settings.LogDirectory, "missions", mission.Id + ".log"));
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

            public async Task<HarborHandshakeAck> HandshakeAsync(string runnerId, int maxConcurrentJobs = 4)
            {
                await SendAsync(new HarborHandshake
                {
                    CorrelationId = "handshake-" + runnerId,
                    HarborId = runnerId,
                    Name = "fake " + runnerId,
                    ProtocolVersion = HarborProtocol.Version,
                    MaxConcurrentJobs = maxConcurrentJobs,
                    Capabilities = new List<HarborCapability> { new HarborCapability { Name = "claude" } }
                }).ConfigureAwait(false);
                return await NextAsync<HarborHandshakeAck>().ConfigureAwait(false);
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

            public async Task<bool> NoMessageAsync()
            {
                await Task.Delay(Quiet).ConfigureAwait(false);
                if (_Inbound.Reader.TryRead(out HarborMessage? unexpected))
                    throw new Exception("Expected no message but received " + HarborProtocol.Serialize(unexpected));
                return true;
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
