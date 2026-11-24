namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Captain-callable suite lease: acquire and release the same host command slot and per-vessel suite
    /// slot the definition-of-done gate and armed checks use, so a captain full-suite run counts against
    /// those limits.
    /// </summary>
    public static class McpSuiteLeaseTools
    {
        /// <summary>MCP tool name.</summary>
        public const string ToolName = "armada_suite_lease";

        /// <summary>Seconds an acquire waits for a grant before it answers queued, when the caller names none.</summary>
        public const int DefaultWaitSeconds = 30;

        /// <summary>
        /// Longest wait an acquire accepts. It stays below the 60-second request timeout that common MCP
        /// clients apply, so a queued answer arrives before the client abandons the call.
        /// </summary>
        public const int MaxWaitSeconds = 45;

        // A short floor so a queued request has entered the host lock's queue before its position is read.
        private const int MinimumWaitMilliseconds = 25;

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Register the suite lease tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="timeProvider">Clock used for the acquire deadline and its delay; defaults to system time.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, LoggingModule? logging = null, TimeProvider? timeProvider = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            TimeProvider clock = timeProvider ?? TimeProvider.System;

            register(
                ToolName,
                "Acquire or release the Armada suite lease before a full-suite run (for example scripts/check.sh). The lease takes the per-vessel and host-wide full-suite slots. A captain credential acts only for its own mission. acquire waits at most waitSeconds (default " + DefaultWaitSeconds + ", at most " + MaxWaitSeconds + ") and then answers granted, held, or queued with your position and current host occupancy. Only granted or held with MayRunFullSuite=true permits a full-suite run. Queued is not permission: never run without a grant. No estimated grant time is promised. While queued, call acquire again: the queued request keeps its place between calls, and that wait is not a stall. Release when the suite ends; release while queued withdraws the request. The lease is released when the mission ends.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", description = "acquire or release" },
                        waitSeconds = new { type = "integer", description = "acquire only: seconds to wait for a grant before answering queued (0-" + MaxWaitSeconds + ", default " + DefaultWaitSeconds + ")" }
                    },
                    required = new[] { "action" }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    string? missionId = caller.MissionId;
                    if (String.IsNullOrWhiteSpace(missionId))
                    {
                        return Failure("mission_required", "armada_suite_lease is scoped to a mission credential.");
                    }

                    SuiteLeaseArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<SuiteLeaseArgs>(args.Value, _JsonOptions) ?? new SuiteLeaseArgs()
                        : new SuiteLeaseArgs();
                    string action = (request.Action ?? String.Empty).Trim();
                    if (String.Equals(action, "release", StringComparison.OrdinalIgnoreCase))
                    {
                        bool withdrawn = CaptainSuiteLeaseRegistry.CancelWait(missionId);
                        bool released = CaptainSuiteLeaseRegistry.Release(missionId);
                        string releaseStatus = released ? "released" : withdrawn ? "withdrawn" : "idle";
                        return new { Status = releaseStatus, MissionId = missionId, MayRunFullSuite = false };
                    }

                    if (!String.Equals(action, "acquire", StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure("invalid_action", "action must be acquire or release.");
                    }

                    Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    if (mission == null) return Failure("not_found", "Mission not found.");

                    if (CaptainSuiteLeaseRegistry.IsActive(missionId) && !CaptainSuiteLeaseRegistry.IsWaiting(missionId))
                    {
                        return new { Status = "held", MissionId = missionId, Position = 0, MayRunFullSuite = true, Message = "Your mission already holds the lease; it may run its full suite. Release when the suite ends." };
                    }

                    // The queued request must outlive this call: a transport timeout or a client that stops
                    // waiting cancels the call, and the next acquire joins the same request at its place.
                    // Only release or the end of the mission withdraws it.
                    bool alreadyQueued = CaptainSuiteLeaseRegistry.IsWaiting(missionId);
                    Task<IDisposable> acquire = CaptainSuiteLeaseRegistry.AcquireAsync(
                        missionId,
                        mission.VesselId,
                        CancellationToken.None);

                    int waitSeconds = Math.Clamp(request.WaitSeconds ?? DefaultWaitSeconds, 0, MaxWaitSeconds);
                    DateTimeOffset waitUntilUtc = clock.GetUtcNow().AddMilliseconds(Math.Max(waitSeconds * 1000, MinimumWaitMilliseconds));
                    await WaitForGrantAsync(acquire, TimeSpan.FromMilliseconds(MinimumWaitMilliseconds), clock).ConfigureAwait(false);
                    if (!acquire.IsCompleted && !alreadyQueued)
                    {
                        await RecordWaitAsync(database, logging, mission, CaptainSuiteLeaseRegistry.ReadWaitPosition(missionId)).ConfigureAwait(false);
                    }

                    TimeSpan remaining = waitUntilUtc - clock.GetUtcNow();
                    if (!acquire.IsCompleted && remaining > TimeSpan.Zero)
                    {
                        await WaitForGrantAsync(acquire, remaining, clock).ConfigureAwait(false);
                    }

                    if (!acquire.IsCompleted)
                    {
                        HostSlotSnapshot snapshot = HostWideCommandLock.Snapshot();
                        HostVesselWaiter? vesselWait = snapshot.FindVesselWaiter(missionId);
                        HostSlotWaiter? hostWait = snapshot.FindWaiter(missionId);
                        return new
                        {
                            Status = "queued",
                            MissionId = missionId,
                            Position = vesselWait?.Position ?? hostWait?.Position ?? 0,
                            MayRunFullSuite = false,
                            WaitReason = vesselWait != null ? "vessel_suite_slot" : snapshot.HeldForMemory ? "host_memory" : "host_command_slot",
                            CurrentHolder = snapshot.Holders.Count > 0 ? "another host build or test operation" : null,
                            HolderCount = snapshot.Holders.Count,
                            OldestHolderSinceUtc = snapshot.HolderSinceUtc,
                            HostHeldForMemory = snapshot.HeldForMemory,
                            AvailableMemoryMb = snapshot.AvailableMemoryMb,
                            MemoryFloorMb = snapshot.MemoryFloorMb,
                            Message = "No permission to run. Call acquire again to keep your place, or release to withdraw; never start the full suite while queued. Holder identities remain private; ask an operator if the wait needs investigation."
                        };
                    }

                    try
                    {
                        await acquire.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return Failure("cancelled", "The queued suite lease request was withdrawn or its mission ended.");
                    }

                    return new
                    {
                        Status = "granted",
                        MissionId = missionId,
                        Position = 0,
                        MayRunFullSuite = true,
                        Message = "Your mission holds the lease and may run its full suite. Release when the suite ends."
                    };
                });
        }

        // Waits for the grant, the bound, or the end of the call, whichever comes first. Ending the call
        // stops this wait only; the queued request keeps its place.
        private static async Task WaitForGrantAsync(Task<IDisposable> acquire, TimeSpan bound, TimeProvider clock)
        {
            try
            {
                await Task.WhenAny(acquire, Task.Delay(bound, clock, McpCallerContext.CallToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task RecordWaitAsync(DatabaseDriver database, LoggingModule? logging, Mission mission, int position)
        {
            HostSlotSnapshot snapshot = HostWideCommandLock.Snapshot();
            try
            {
                ArmadaEvent evt = new ArmadaEvent(
                    CaptainSuiteLeaseRegistry.WaitEventType,
                    "Mission " + mission.Id + " waits for a suite lease at position " + position)
                {
                    TenantId = mission.TenantId,
                    UserId = mission.UserId,
                    EntityType = "mission",
                    EntityId = mission.Id,
                    MissionId = mission.Id,
                    CaptainId = mission.CaptainId,
                    VesselId = mission.VesselId,
                    VoyageId = mission.VoyageId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        position,
                        snapshot.SlotCount,
                        snapshot.VesselSlotCount,
                        HolderKeys = snapshot.Holders.ConvertAll(holder => holder.Key),
                        WaitingKeys = snapshot.Waiters.ConvertAll(waiter => waiter.Key),
                        VesselWaitingKeys = snapshot.VesselWaiters.ConvertAll(waiter => waiter.Key)
                    }, _JsonOptions)
                };
                await database.Events.CreateAsync(evt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logging?.Warn("[McpSuiteLeaseTools] could not record " + CaptainSuiteLeaseRegistry.WaitEventType + ": " + ex.Message);
            }
        }

        private static object Failure(string code, string message)
        {
            return new { Error = message, Code = code, MayRunFullSuite = false };
        }

        private sealed class SuiteLeaseArgs
        {
            public string? Action { get; set; }

            public int? WaitSeconds { get; set; }
        }
    }
}
