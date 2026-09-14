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
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Harbor;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

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
            private readonly Webserver _Server;
            private readonly CancellationTokenSource _Cancellation;
            private readonly HttpClient _Http;
            private readonly int _Port;
            private readonly string _LinkPath;

            public HarborRunnerSessionRegistry Registry { get; }
            public HarborJobCoordinator Coordinator { get; }
            public Principal Administrator { get; }
            public Principal OwnerA { get; }
            public Principal OwnerB { get; }

            private Harness(TestDatabase database, Webserver server, CancellationTokenSource cancellation, int port, string linkPath,
                HarborRunnerSessionRegistry registry, HarborJobCoordinator coordinator, Principal administrator, Principal ownerA, Principal ownerB)
            {
                _Database = database;
                _Server = server;
                _Cancellation = cancellation;
                _Port = port;
                _LinkPath = linkPath;
                _Http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/"), Timeout = Wait };
                Registry = registry;
                Coordinator = coordinator;
                Administrator = administrator;
                OwnerA = ownerA;
                OwnerB = ownerB;
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
                HarborRunnerEnrollmentService enrollments = new HarborRunnerEnrollmentService(database.Driver);
                HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, enrollments);
                HarborJobCoordinator coordinator = new HarborJobCoordinator(registry, enrollments);
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
                new HarborRunnerEnrollmentRoutes(enrollments, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Register(
                    server,
                    ctx => authentication.AuthenticateAsync(ctx.Request.Headers.Get("Authorization"), ctx.Request.Headers.Get("X-Token"), ctx.Request.Headers.Get("X-Api-Key")),
                    new AuthorizationService());
                CancellationTokenSource cancellation = new CancellationTokenSource();
                server.Start(cancellation.Token);
                return new Harness(database, server, cancellation, port, settings.Harbor.LinkPath, registry, coordinator, administrator, ownerA, ownerB);
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
                return FakeRunner.ConnectAsync(new Uri("ws://127.0.0.1:" + _Port + _LinkPath), headers);
            }

            public async ValueTask DisposeAsync()
            {
                _Http.Dispose();
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
