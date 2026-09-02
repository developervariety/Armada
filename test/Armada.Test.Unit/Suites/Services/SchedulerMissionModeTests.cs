namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Covers <see cref="AutonomousObjectiveScheduler.DeriveMissionMode"/>: an autonomously
    /// dispatched Research objective must run its missions read-only so the Judge accepts an
    /// unchanged branch, while every other Kind keeps the Implementation default.
    /// </summary>
    public sealed class SchedulerMissionModeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Scheduler Mission Mode Derivation";

        /// <summary>Run the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Research kind derives read-only Research mode", () =>
            {
                AssertEqual("Research", AutonomousObjectiveScheduler.DeriveMissionMode(ObjectiveKindEnum.Research),
                    "A Research objective must dispatch read-only missions so a no-commit report is not failed for an empty diff.");
            });

            await RunTest("Non-research kinds keep the Implementation default (null)", () =>
            {
                AssertNull(AutonomousObjectiveScheduler.DeriveMissionMode(ObjectiveKindEnum.Feature),
                    "Feature must keep the Implementation default.");
                AssertNull(AutonomousObjectiveScheduler.DeriveMissionMode(ObjectiveKindEnum.Bug),
                    "Bug must keep the Implementation default.");
                AssertNull(AutonomousObjectiveScheduler.DeriveMissionMode(ObjectiveKindEnum.Refactor),
                    "Refactor must keep the Implementation default.");
                AssertNull(AutonomousObjectiveScheduler.DeriveMissionMode(ObjectiveKindEnum.Chore),
                    "Chore must keep the Implementation default.");
                AssertNull(AutonomousObjectiveScheduler.DeriveMissionMode(ObjectiveKindEnum.Initiative),
                    "Initiative must keep the Implementation default.");
            });

            await RunTest("Derived Research mode parses back to the read-only Research enum", () =>
            {
                string? mode = AutonomousObjectiveScheduler.DeriveMissionMode(ObjectiveKindEnum.Research);
                AssertEqual(MissionModeEnum.Research, MissionModes.Parse(mode),
                    "The derived mode string must round-trip through MissionModes.Parse to Research.");
            });
        }
    }
}
