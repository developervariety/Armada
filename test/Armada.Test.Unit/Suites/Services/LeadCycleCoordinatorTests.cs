namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests the shared durable lease, operating mode, handoff, and audit contract used by
    /// the Grok Bot and legacy unattended leads.
    /// </summary>
    public class LeadCycleCoordinatorTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "LeadCycleCoordinator";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Legacy mode accepts legacy and refuses Grok", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    GrokLeadSettings settings = new GrokLeadSettings();
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, settings);

                    LeadCycleStartResult grok = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Grok, settings.ParticipantKey);
                    AssertFalse(grok.Acquired, "Grok must not start while legacy is primary.");

                    LeadCycleStartResult legacy = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Legacy, "armada-lead");
                    AssertTrue(legacy.Acquired, "Legacy must start in its default primary mode.");
                    AssertNotNull(legacy.CycleId, "An acquired cycle must have an identifier.");
                }
            });

            await RunTest("Grok mode enforces identity and one shared lease", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    GrokLeadSettings settings = new GrokLeadSettings();
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, settings);
                    await coordinator.SetModeAsync(LeadOperatingModeEnum.GrokPrimary, "owner");

                    LeadCycleStartResult wrongIdentity = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Grok, "other-bot");
                    AssertFalse(wrongIdentity.Acquired, "A different participant must not become the Grok lead.");

                    LeadCycleStartResult grok = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Grok, settings.ParticipantKey);
                    AssertTrue(grok.Acquired, "The configured Grok participant must acquire the lease.");

                    await AssertThrowsAsync<InvalidOperationException>(async () =>
                    {
                        await coordinator.RequireActiveCycleAsync(
                            grok.CycleId!, LeadRunnerTypeEnum.Legacy, settings.ParticipantKey);
                    });
                    await coordinator.RequireActiveCycleAsync(
                        grok.CycleId!, LeadRunnerTypeEnum.Grok, settings.ParticipantKey);

                    LeadCycleStartResult duplicate = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Grok, settings.ParticipantKey);
                    AssertFalse(duplicate.Acquired, "A second Grok cycle must not overlap.");

                    LeadCycleStartResult legacy = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Legacy, "armada-lead", true);
                    AssertFalse(legacy.Acquired, "Standby fallback must not preempt a live Grok lease.");
                }
            });

            await RunTest("Completion requires a handoff and releases the lease", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    GrokLeadSettings settings = new GrokLeadSettings();
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, settings);
                    LeadCycleStartResult started = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Legacy, "armada-lead");
                    AssertNotNull(started.CycleId);

                    await AssertThrowsAsync<ArgumentNullException>(async () =>
                    {
                        await coordinator.CompleteAsync(started.CycleId!, " ");
                    });

                    bool completed = await coordinator.CompleteAsync(
                        started.CycleId!, "Cycle complete. No open claims.");
                    AssertTrue(completed, "The lease owner must complete its cycle.");

                    LeadCycleStatus status = await coordinator.GetStatusAsync();
                    AssertFalse(status.Active, "Completion must release the shared lease.");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByEntityAsync(
                        "lead_cycle", started.CycleId!, 50);
                    AssertTrue(events.Exists(item => item.EventType == LeadCycleCoordinator.CycleStartedEventType),
                        "The start event must be durable.");
                    ArmadaEvent? completion = events.Find(item => item.EventType == LeadCycleCoordinator.CycleCompletedEventType);
                    AssertNotNull(completion, "The completion event must be durable.");
                    AssertContains("No open claims", completion!.Payload ?? String.Empty);
                }
            });

            await RunTest("Heartbeat renews only the active owner", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    GrokLeadSettings settings = new GrokLeadSettings();
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, settings);
                    LeadCycleStartResult started = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Legacy, "armada-lead");

                    AssertFalse(await coordinator.HeartbeatAsync("lcy_not-the-owner"),
                        "A different cycle must not renew the lease.");
                    AssertTrue(await coordinator.HeartbeatAsync(started.CycleId!),
                        "The active cycle must renew the lease.");
                }
            });

            await RunTest("Maintenance refuses every unattended lead", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    GrokLeadSettings settings = new GrokLeadSettings();
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, settings);
                    await coordinator.SetModeAsync(LeadOperatingModeEnum.Maintenance, "owner");

                    LeadCycleStartResult legacy = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Legacy, "armada-lead");
                    LeadCycleStartResult grok = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Grok, settings.ParticipantKey);
                    AssertFalse(legacy.Acquired);
                    AssertFalse(grok.Acquired);
                }
            });

            await RunTest("Standby fallback requires elapsed Grok inactivity", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    GrokLeadSettings settings = new GrokLeadSettings
                    {
                        StandbyFallbackAfterMinutes = 60
                    };
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, settings);
                    LeadModeEventPayload modePayload = new LeadModeEventPayload
                    {
                        Mode = LeadOperatingModeEnum.GrokPrimary,
                        Actor = "owner"
                    };
                    ArmadaEvent oldMode = new ArmadaEvent(
                        LeadCycleCoordinator.ModeChangedEventType,
                        "Old Grok-primary selection")
                    {
                        EntityType = "lead_mode",
                        EntityId = "unattended-lead",
                        Payload = JsonSerializer.Serialize(modePayload),
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-61)
                    };
                    await testDb.Driver.Events.CreateAsync(oldMode);

                    LeadCycleStartResult fallback = await coordinator.TryBeginAsync(
                        LeadRunnerTypeEnum.Legacy, "armada-lead", true);
                    AssertTrue(fallback.Acquired, "Legacy standby must start after the configured inactivity threshold.");
                }
            });
        }
    }
}
