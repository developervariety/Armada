namespace Armada.Test.Unit.Suites.Settings
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Tests restricted Grok lead defaults, clamps, and settings serialization.
    /// </summary>
    public class GrokLeadSettingsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "GrokLeadSettings";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Restricted listener is disabled and loopback by default", () =>
            {
                GrokLeadSettings settings = new GrokLeadSettings();
                AssertFalse(settings.Enabled);
                AssertTrue(settings.ReadOnly);
                AssertFalse(settings.ControlledDispatchEnabled);
                AssertEqual(3, settings.MaxControlledDispatchMissions);
                AssertEqual("127.0.0.1", settings.Hostname);
                AssertEqual(7892, settings.Port);
                AssertEqual("armada-lead", settings.ParticipantKey);
                AssertEqual(LeadOperatingModeEnum.LegacyPrimary, settings.DefaultMode);
                AssertEqual(40, settings.CycleLeaseMinutes);
                return Task.CompletedTask;
            });

            await RunTest("Lease and fallback settings clamp to safe ranges", () =>
            {
                GrokLeadSettings settings = new GrokLeadSettings
                {
                    CycleLeaseMinutes = 1,
                    StandbyFallbackAfterMinutes = 10
                };
                AssertEqual(5, settings.CycleLeaseMinutes);
                AssertEqual(60, settings.StandbyFallbackAfterMinutes);
                settings.CycleLeaseMinutes = 100;
                settings.StandbyFallbackAfterMinutes = 2000;
                AssertEqual(60, settings.CycleLeaseMinutes);
                AssertEqual(1440, settings.StandbyFallbackAfterMinutes);
                settings.MaxControlledDispatchMissions = 0;
                AssertEqual(1, settings.MaxControlledDispatchMissions);
                settings.MaxControlledDispatchMissions = 100;
                AssertEqual(10, settings.MaxControlledDispatchMissions);
                return Task.CompletedTask;
            });

            await RunTest("Armada settings round-trip Grok lead configuration", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.GrokLead.Enabled = true;
                settings.GrokLead.ReadOnly = false;
                settings.GrokLead.ControlledDispatchEnabled = true;
                settings.GrokLead.MaxControlledDispatchMissions = 5;
                settings.GrokLead.Port = 8792;
                settings.GrokLead.DefaultMode = LeadOperatingModeEnum.GrokPrimary;
                JsonSerializerOptions options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter());
                string json = JsonSerializer.Serialize(settings, options);
                ArmadaSettings? roundTrip = JsonSerializer.Deserialize<ArmadaSettings>(json, options);
                AssertNotNull(roundTrip);
                AssertTrue(roundTrip!.GrokLead.Enabled);
                AssertFalse(roundTrip.GrokLead.ReadOnly);
                AssertTrue(roundTrip.GrokLead.ControlledDispatchEnabled);
                AssertEqual(5, roundTrip.GrokLead.MaxControlledDispatchMissions);
                AssertEqual(8792, roundTrip.GrokLead.Port);
                AssertEqual(LeadOperatingModeEnum.GrokPrimary, roundTrip.GrokLead.DefaultMode);
                return Task.CompletedTask;
            });
        }
    }
}
