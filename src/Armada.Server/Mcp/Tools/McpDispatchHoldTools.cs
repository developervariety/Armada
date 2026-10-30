namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the dispatch hold MCP tool, which stops new voyages while Armada itself is being
    /// worked on.
    /// </summary>
    public static class McpDispatchHoldTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers the dispatch hold tool with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver that receives the hold's state-change events.</param>
        /// <param name="dispatchHold">Optional dispatch hold shared with the admiral's dispatch paths.</param>
        /// <param name="jobs">Optional background job service. When set, hold status and engage list the jobs accepted but not finished, which a restart would record as Lost.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, Armada.Core.Services.DispatchHold? dispatchHold = null, LongRunningJobService? jobs = null)
        {
            {
                register(
                    "armada_dispatch_hold",
                    "Engage, clear, or inspect the fleet-wide dispatch hold. Engage it BEFORE rebuilding or redeploying the admiral so other sessions and the objective scheduler cannot start new voyages against a binary about to change. In-flight voyages continue; only new dispatches are refused. The hold is stored in the database, so it survives an admiral restart and stays engaged until you clear it. Engaging or clearing records a dispatch.hold_engaged or dispatch.hold_cleared event. Status and engage list UnfinishedJobs: background jobs (dispatches included) accepted but not finished; RunningGates: definition-of-done gates running or waiting for the host command slot, each with a SlotWait (Running, WaitingForSlot with its Position in line, or Queued before its first command) so a queued gate is told from a lost one; GateReruns: interrupted gates being re-run; and HostSlot: who holds and who waits for the host command slot, with MemoryWaitReason when free memory holds the next request. A restart records each unfinished job Lost and kills every running gate and check, so wait for UnfinishedJobs, RunningGates and GateReruns to be empty and HostSlot to have no holder or waiter before restarting.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "engage | clear | status" },
                            reason = new { type = "string", description = "Why the hold is engaged. Required for engage." },
                            setBy = new { type = "string", description = "Your session or operator name for engage. Omit it to use the name of the credential you authenticated with. A value you pass wins." }
                        },
                        required = new[] { "action" }
                    },
                    async (args) =>
                    {
                        DispatchHoldArgs request = JsonSerializer.Deserialize<DispatchHoldArgs>(args!.Value, _JsonOptions)!;
                        string action = String.IsNullOrWhiteSpace(request.Action) ? "status" : request.Action!.Trim().ToLowerInvariant();

                        if (String.Equals(action, "status", StringComparison.Ordinal))
                        {
                            if (dispatchHold == null)
                                return McpToolUnavailable.Create("dispatch hold");
                            return (object)new { Active = dispatchHold.Snapshot() != null, Hold = dispatchHold.Snapshot(), UnfinishedJobs = UnfinishedJobs(jobs), RunningGates = RunningGates(), GateReruns = MissionService.GateRerunsInFlight(), HostSlot = HostWideCommandLock.Snapshot() };
                        }

                        if (String.Equals(action, "clear", StringComparison.Ordinal))
                        {
                            if (dispatchHold == null)
                                return McpToolUnavailable.Create("dispatch hold");
                            DispatchHoldSnapshot? previous = dispatchHold.Snapshot();
                            await dispatchHold.ClearAsync().ConfigureAwait(false);
                            if (previous != null)
                                await DispatchHoldEvents.RecordClearedAsync(database, previous, McpCallerContext.CallerIdentity).ConfigureAwait(false);

                            return (object)new { Active = false, Cleared = true };
                        }

                        if (String.Equals(action, "engage", StringComparison.Ordinal))
                        {
                            if (String.IsNullOrWhiteSpace(request.Reason))
                                return (object)new { Error = "reason is required when engaging the hold" };
                            if (dispatchHold == null)
                                return McpToolUnavailable.Create("dispatch hold");
                            string? setBy = String.IsNullOrWhiteSpace(request.SetBy)
                                ? McpCallerContext.CallerIdentity
                                : request.SetBy!.Trim();
                            if (String.IsNullOrWhiteSpace(setBy))
                                return (object)new { Error = "setBy is required when engaging the hold and the call carries no named credential - name your session so others know who to ask" };

                            await dispatchHold.EngageAsync(request.Reason!, setBy).ConfigureAwait(false);
                            DispatchHoldSnapshot? engaged = dispatchHold.Snapshot();
                            if (engaged != null)
                                await DispatchHoldEvents.RecordEngagedAsync(database, engaged).ConfigureAwait(false);
                            return (object)new { Active = true, Hold = dispatchHold.Snapshot(), UnfinishedJobs = UnfinishedJobs(jobs), RunningGates = RunningGates(), GateReruns = MissionService.GateRerunsInFlight(), HostSlot = HostWideCommandLock.Snapshot() };
                        }

                        return (object)new { Error = "action must be engage, clear, or status" };
                    });
            }
        }

        private static List<RunningGateRow> RunningGates()
        {
            HostSlotSnapshot slot = HostWideCommandLock.Snapshot();
            return DefinitionOfDoneGateRuns.Snapshot().Select(entry => new RunningGateRow
            {
                MissionId = entry.Key,
                StartedUtc = entry.Value,
                SlotWait = GateSlotWait.Describe(entry.Key, slot)
            }).ToList();
        }

        /// <summary>
        /// A definition-of-done gate running or waiting for the host command slot, as listed by the dispatch hold.
        /// </summary>
        public sealed class RunningGateRow
        {
            /// <summary>Mission whose gate runs.</summary>
            public string MissionId { get; set; } = String.Empty;

            /// <summary>When the gate started, including its wait for the host command slot.</summary>
            public DateTime StartedUtc { get; set; }

            /// <summary>
            /// Where the gate stands for the host command slot: Running while it holds one, WaitingForSlot with its
            /// Position while it waits in line, or Queued before its first command. This tells a queued gate from a
            /// lost one without reading its dock.
            /// </summary>
            public GateSlotWait? SlotWait { get; set; } = null;
        }

        private static List<UnfinishedJobRow> UnfinishedJobs(LongRunningJobService? jobs)
        {
            if (jobs == null) return new List<UnfinishedJobRow>();
            return jobs.ListUnfinished().Select(job => new UnfinishedJobRow
            {
                JobId = job.JobId,
                Operation = job.Operation,
                Status = job.Status.ToString(),
                SubmittedAtUtc = job.SubmittedAtUtc,
                StartedAtUtc = job.StartedAtUtc,
                ObjectiveId = job.ObjectiveId,
                VesselId = job.VesselId
            }).ToList();
        }

        /// <summary>
        /// A background job accepted but not finished, as listed by the dispatch hold.
        /// </summary>
        public sealed class UnfinishedJobRow
        {
            /// <summary>Job identifier.</summary>
            public string JobId { get; set; } = String.Empty;

            /// <summary>Operation name.</summary>
            public string Operation { get; set; } = String.Empty;

            /// <summary>Accepted or Running.</summary>
            public string Status { get; set; } = String.Empty;

            /// <summary>When the job was accepted.</summary>
            public DateTime SubmittedAtUtc { get; set; }

            /// <summary>When the job started running, if it has.</summary>
            public DateTime? StartedAtUtc { get; set; }

            /// <summary>Objective the job acts for, when known.</summary>
            public string? ObjectiveId { get; set; }

            /// <summary>Vessel the job acts on, when known.</summary>
            public string? VesselId { get; set; }
        }

        private sealed class DispatchHoldArgs
        {
            public string? Action { get; set; } = null;
            public string? Reason { get; set; } = null;
            public string? SetBy { get; set; } = null;
        }
    }
}
