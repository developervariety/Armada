namespace Armada.Test.Unit
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for fully report-only voyage classification: all Audit, all Research, or neither.
    /// </summary>
    public sealed class VoyageReportOnlyClassifierTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "VoyageReportOnlyClassifier";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("AllAuditMissions_AreFullyReportOnly", () =>
            {
                List<Mission> missions = new List<Mission>
                {
                    new Mission("Worker", "report") { Mode = MissionModeEnum.Audit },
                    new Mission("Judge", "review") { Mode = MissionModeEnum.Audit, Persona = "Judge" }
                };
                AssertTrue(VoyageReportOnlyClassifier.IsFullyReportOnly(missions), "every Audit mission is report-only");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("AllResearchMissions_AreFullyReportOnly", () =>
            {
                List<Mission> missions = new List<Mission>
                {
                    new Mission("Worker", "report") { Mode = MissionModeEnum.Research },
                    new Mission("Judge", "review") { Mode = MissionModeEnum.Research, Persona = "Judge" }
                };
                AssertTrue(VoyageReportOnlyClassifier.IsFullyReportOnly(missions), "every Research mission is report-only");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("MixedAuditAndResearch_IsNotFullyReportOnly", () =>
            {
                List<Mission> missions = new List<Mission>
                {
                    new Mission("Worker", "audit") { Mode = MissionModeEnum.Audit },
                    new Mission("Worker", "research") { Mode = MissionModeEnum.Research }
                };
                AssertFalse(VoyageReportOnlyClassifier.IsFullyReportOnly(missions),
                    "Audit and Research on one voyage keeps implementation-style gates");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("AnyImplementationMission_IsNotFullyReportOnly", () =>
            {
                List<Mission> missions = new List<Mission>
                {
                    new Mission("Worker", "code") { Mode = MissionModeEnum.Implementation },
                    new Mission("Judge", "review") { Mode = MissionModeEnum.Audit, Persona = "Judge" }
                };
                AssertFalse(VoyageReportOnlyClassifier.IsFullyReportOnly(missions),
                    "mixed-mode voyages keep the code Check gates");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
