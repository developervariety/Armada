namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
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
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, LoggingModule? logging = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));

            register(
                ToolName,
                "Acquire or release the Armada suite lease before a full-suite run (for example scripts/check.sh). The lease takes the per-vessel and host-wide full-suite slots. A captain credential acts only for its own mission. Wait if the tool says you are queued; that wait is not a stall. Release when the suite ends. The lease is released when the mission ends.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", description = "acquire or release" }
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
                        bool released = CaptainSuiteLeaseRegistry.Release(missionId);
                        return new { Status = released ? "released" : "idle", MissionId = missionId };
                    }

                    if (!String.Equals(action, "acquire", StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure("invalid_action", "action must be acquire or release.");
                    }

                    Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    if (mission == null) return Failure("not_found", "Mission not found.");

                    if (CaptainSuiteLeaseRegistry.IsActive(missionId) && !CaptainSuiteLeaseRegistry.IsWaiting(missionId))
                    {
                        return new { Status = "held", MissionId = missionId, Position = 0 };
                    }

                    Task<IDisposable> acquire = CaptainSuiteLeaseRegistry.AcquireAsync(
                        missionId,
                        mission.VesselId,
                        McpCallerContext.CallToken);
                    if (!acquire.IsCompleted)
                    {
                        await Task.Delay(25).ConfigureAwait(false);
                        int position = CaptainSuiteLeaseRegistry.ReadWaitPosition(missionId);
                        HostSlotSnapshot snapshot = HostWideCommandLock.Snapshot();
                        try
                        {
                            ArmadaEvent evt = new ArmadaEvent(
                                CaptainSuiteLeaseRegistry.WaitEventType,
                                "Mission " + missionId + " waits for a suite lease at position " + position)
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

                    try
                    {
                        await acquire.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        CaptainSuiteLeaseRegistry.Release(missionId);
                        return Failure("cancelled", "The suite lease acquire was cancelled.");
                    }

                    return new
                    {
                        Status = "granted",
                        MissionId = missionId,
                        Position = 0
                    };
                });
        }

        private static object Failure(string code, string message)
        {
            return new { Error = message, Code = code };
        }

        private sealed class SuiteLeaseArgs
        {
            public string? Action { get; set; }
        }
    }
}
