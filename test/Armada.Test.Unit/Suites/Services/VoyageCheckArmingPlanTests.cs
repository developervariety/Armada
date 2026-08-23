namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for the dispatch-time Check arming decision. A voyage with no green independent
    /// Check has its Judge PASS rejected, so these cases pin what gets armed, and equally what
    /// must NOT be armed a second time.
    /// </summary>
    public class VoyageCheckArmingPlanTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Voyage Check Arming Plan";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Both Build and UnitTest are armed when the profile defines both", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("dotnet build", "dotnet test"),
                    null);

                AssertEqual(2, planned.Count, "Build and UnitTest are the stated minimum for code work.");
                AssertTrue(planned.Contains(CheckRunTypeEnum.Build));
                AssertTrue(planned.Contains(CheckRunTypeEnum.UnitTest));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A type with no command on the profile is not armed", () =>
            {
                // A Check with no command cannot produce a real signal; it would sit Pending and
                // then fail the Judge, which is worse than not arming it.
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("dotnet build", null),
                    null);

                AssertEqual(1, planned.Count);
                AssertEqual(CheckRunTypeEnum.Build, planned[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A type already attached to the voyage is never armed again", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("dotnet build", "dotnet test"),
                    new List<CheckRun> { new CheckRun { Type = CheckRunTypeEnum.Build } });

                AssertEqual(1, planned.Count, "Only the missing type may be armed.");
                AssertEqual(CheckRunTypeEnum.UnitTest, planned[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A FAILED existing check still blocks re-arming that type", () =>
            {
                // A single failed Check rejects a Judge PASS however many green ones sit beside it.
                // Arming a second Build here would manufacture exactly that condition.
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("dotnet build", "dotnet test"),
                    new List<CheckRun>
                    {
                        new CheckRun { Type = CheckRunTypeEnum.Build, Status = CheckRunStatusEnum.Failed },
                        new CheckRun { Type = CheckRunTypeEnum.UnitTest, Status = CheckRunStatusEnum.Passed }
                    });

                AssertEqual(0, planned.Count, "Neither type may be armed a second time, whatever state the first is in.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Arming disabled yields no plan", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings { Enabled = false },
                    MakeProfile("dotnet build", "dotnet test"),
                    null);

                AssertEqual(0, planned.Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Each type can be disabled independently", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> buildOnly = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings { ArmUnitTest = false },
                    MakeProfile("dotnet build", "dotnet test"),
                    null);
                AssertEqual(1, buildOnly.Count);
                AssertEqual(CheckRunTypeEnum.Build, buildOnly[0]);

                IReadOnlyList<CheckRunTypeEnum> testOnly = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings { ArmBuild = false },
                    MakeProfile("dotnet build", "dotnet test"),
                    null);
                AssertEqual(1, testOnly.Count);
                AssertEqual(CheckRunTypeEnum.UnitTest, testOnly[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("No profile and no settings yield no plan rather than throwing", () =>
            {
                AssertEqual(0, VoyageCheckArmingPlan.Resolve(new VoyageCheckArmingSettings(), null, null).Count);
                AssertEqual(0, VoyageCheckArmingPlan.Resolve(null, MakeProfile("dotnet build", "dotnet test"), null).Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A whitespace-only command counts as no command", () =>
            {
                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(
                    new VoyageCheckArmingSettings(),
                    MakeProfile("   ", "dotnet test"),
                    null);

                AssertEqual(1, planned.Count);
                AssertEqual(CheckRunTypeEnum.UnitTest, planned[0]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private static WorkflowProfile MakeProfile(string? build, string? unitTest)
        {
            return new WorkflowProfile
            {
                Id = "wfp_test",
                Name = "Test profile",
                BuildCommand = build,
                UnitTestCommand = unitTest
            };
        }

        #endregion
    }
}
