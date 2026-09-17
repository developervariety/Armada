namespace Armada.Test.Unit.Suites.Settings
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the captain-log screening settings section: its shipped defaults, its clamps, and
    /// that it survives a settings hot reload. A sub-section left out of the in-place reload copy is
    /// silently ignored on every reload, so the feature reads as off however the file is edited.
    /// </summary>
    public sealed class CaptainLogScreeningSettingsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Log Screening Settings";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Defaults_ShipOffWithAnEmptyBoundaryPatternList", () =>
            {
                CaptainLogScreeningSettings settings = new CaptainLogScreeningSettings();
                AssertFalse(settings.Enabled);
                AssertEqual(0, settings.BoundaryPatterns.Count);
                AssertTrue(settings.IntervalSeconds >= 30);
                AssertTrue(settings.TailLines >= 20);
                AssertTrue(settings.CooldownMinutes >= 1);
                return Task.CompletedTask;
            });

            await RunTest("Clamps_KeepEveryBoundedValueInsideItsRange", () =>
            {
                CaptainLogScreeningSettings settings = new CaptainLogScreeningSettings();
                settings.IntervalSeconds = 0;
                settings.TailLines = 100000;
                settings.CooldownMinutes = -5;
                AssertEqual(30, settings.IntervalSeconds);
                AssertEqual(2000, settings.TailLines);
                AssertEqual(1, settings.CooldownMinutes);
                return Task.CompletedTask;
            });

            await RunTest("NullBoundaryPatternList_RestoresTheEmptyDefault", () =>
            {
                CaptainLogScreeningSettings settings = new CaptainLogScreeningSettings();
                settings.BoundaryPatterns = null!;
                AssertNotNull(settings.BoundaryPatterns);
                AssertEqual(0, settings.BoundaryPatterns.Count);
                return Task.CompletedTask;
            });

            await RunTest("HotReload_ReachesTheSectionHeldByReference", () =>
            {
                ArmadaSettings live = new ArmadaSettings();
                // The screen is constructed with this nested object, so a reload must be observed
                // through the reference the running service already holds.
                CaptainLogScreeningSettings held = live.CaptainLogScreening;
                AssertFalse(held.Enabled);

                ArmadaSettings incoming = new ArmadaSettings();
                incoming.CaptainLogScreening.Enabled = true;
                incoming.CaptainLogScreening.IntervalSeconds = 120;
                incoming.CaptainLogScreening.TailLines = 400;
                incoming.CaptainLogScreening.CooldownMinutes = 45;
                incoming.CaptainLogScreening.BoundaryPatterns = new List<string> { "reserved-term" };

                live.ApplyHotReloadableFrom(incoming);

                AssertTrue(ReferenceEquals(held, live.CaptainLogScreening));
                AssertTrue(held.Enabled);
                AssertEqual(120, held.IntervalSeconds);
                AssertEqual(400, held.TailLines);
                AssertEqual(45, held.CooldownMinutes);
                AssertEqual(1, held.BoundaryPatterns.Count);
                AssertEqual("reserved-term", held.BoundaryPatterns[0]);
                return Task.CompletedTask;
            });

            await RunTest("HotReload_CanSwitchTheScreenBackOffAndClearThePatterns", () =>
            {
                ArmadaSettings live = new ArmadaSettings();
                live.CaptainLogScreening.Enabled = true;
                live.CaptainLogScreening.BoundaryPatterns = new List<string> { "reserved-term" };
                CaptainLogScreeningSettings held = live.CaptainLogScreening;

                live.ApplyHotReloadableFrom(new ArmadaSettings());

                AssertFalse(held.Enabled);
                AssertEqual(0, held.BoundaryPatterns.Count);
                return Task.CompletedTask;
            });

            await RunTest("HotReload_CopiedPatternListIsNotSharedWithTheIncomingInstance", () =>
            {
                ArmadaSettings live = new ArmadaSettings();
                ArmadaSettings incoming = new ArmadaSettings();
                incoming.CaptainLogScreening.BoundaryPatterns = new List<string> { "reserved-term" };

                live.ApplyHotReloadableFrom(incoming);
                incoming.CaptainLogScreening.BoundaryPatterns.Add("added-after-the-reload");

                AssertEqual(1, live.CaptainLogScreening.BoundaryPatterns.Count);
                return Task.CompletedTask;
            });
        }
    }
}
