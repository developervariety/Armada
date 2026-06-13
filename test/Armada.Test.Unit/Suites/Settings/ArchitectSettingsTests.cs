namespace Armada.Test.Unit.Suites.Settings
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>Tests for ArchitectSettings clamping plus the ArmadaSettings.Architect nested-settings property.</summary>
    public class ArchitectSettingsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "ArchitectSettings";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Defaults_MaxMissionsPerVoyage_Is8", () =>
            {
                ArchitectSettings s = new ArchitectSettings();
                AssertEqual(8, s.MaxMissionsPerVoyage, "Default cap should be 8");
                return Task.CompletedTask;
            });

            await RunTest("MaxMissionsPerVoyage_Zero_ClampsTo1", () =>
            {
                ArchitectSettings s = new ArchitectSettings
                {
                    MaxMissionsPerVoyage = 0
                };
                AssertEqual(1, s.MaxMissionsPerVoyage, "0 should clamp up to 1");
                return Task.CompletedTask;
            });

            await RunTest("MaxMissionsPerVoyage_Negative_ClampsTo1", () =>
            {
                ArchitectSettings s = new ArchitectSettings
                {
                    MaxMissionsPerVoyage = -999
                };
                AssertEqual(1, s.MaxMissionsPerVoyage, "Negative should clamp up to 1");
                return Task.CompletedTask;
            });

            await RunTest("MaxMissionsPerVoyage_OverMax_ClampsTo50", () =>
            {
                ArchitectSettings s = new ArchitectSettings
                {
                    MaxMissionsPerVoyage = 9999
                };
                AssertEqual(50, s.MaxMissionsPerVoyage, "Over-cap value should clamp down to 50");
                return Task.CompletedTask;
            });

            await RunTest("MaxMissionsPerVoyage_AtMin_Preserves1", () =>
            {
                ArchitectSettings s = new ArchitectSettings
                {
                    MaxMissionsPerVoyage = 1
                };
                AssertEqual(1, s.MaxMissionsPerVoyage, "Lower bound 1 should be preserved");
                return Task.CompletedTask;
            });

            await RunTest("MaxMissionsPerVoyage_AtMax_Preserves50", () =>
            {
                ArchitectSettings s = new ArchitectSettings
                {
                    MaxMissionsPerVoyage = 50
                };
                AssertEqual(50, s.MaxMissionsPerVoyage, "Upper bound 50 should be preserved");
                return Task.CompletedTask;
            });

            await RunTest("MaxMissionsPerVoyage_InRange_PreservesValue", () =>
            {
                ArchitectSettings s = new ArchitectSettings
                {
                    MaxMissionsPerVoyage = 12
                };
                AssertEqual(12, s.MaxMissionsPerVoyage, "In-range value should be preserved unchanged");
                return Task.CompletedTask;
            });

            await RunTest("ArmadaSettings_Architect_NonNullByDefault", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertNotNull(settings.Architect, "Architect should be lazily non-null");
                AssertEqual(8, settings.Architect.MaxMissionsPerVoyage, "Lazy default should carry the default cap");
                return Task.CompletedTask;
            });

            await RunTest("ArmadaSettings_Architect_CustomInstance_Preserved", () =>
            {
                ArchitectSettings custom = new ArchitectSettings
                {
                    MaxMissionsPerVoyage = 20
                };
                ArmadaSettings settings = new ArmadaSettings
                {
                    Architect = custom
                };
                AssertEqual(custom, settings.Architect, "Custom instance should be preserved by reference");
                AssertEqual(20, settings.Architect.MaxMissionsPerVoyage, "Custom cap should be preserved");
                return Task.CompletedTask;
            });

            await RunTest("ArmadaSettings_Architect_NullAssign_Throws", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertThrows<ArgumentNullException>(() => settings.Architect = null!, "Assigning null to Architect should throw");
                return Task.CompletedTask;
            });
        }
    }
}
