namespace Armada.Test.Unit.Suites.Models
{
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Test.Common;

    public class ContextPackUsageAggregateTests : TestSuite
    {
        public override string Name => "Context Pack Usage Aggregate";

        protected override async Task RunTestsAsync()
        {
            await RunTest("FromSummaries_EmptyInput_ReturnsZeros", () =>
            {
                ContextPackUsageAggregate aggregate = ContextPackUsageAggregate.FromSummaries(
                    new List<ContextPackUsageSummary>());

                AssertEqual(0, aggregate.MissionsConsidered);
                AssertEqual(0.0, aggregate.PackStagedShare);
                AssertEqual(0.0, aggregate.ReadBeforeSearchShare);
                AssertEqual(0.0, aggregate.AverageSearchToolCalls);
            });

            await RunTest("FromSummaries_MixedComplianceClasses_ComputesExpectedShares", () =>
            {
                List<ContextPackUsageSummary> summaries = new List<ContextPackUsageSummary>
                {
                    new ContextPackUsageSummary
                    {
                        MissionId = "msn_a",
                        ContextPackStaged = true,
                        ContextPackCompliance = "ReadBeforeSearch",
                        SearchToolCallCount = 1
                    },
                    new ContextPackUsageSummary
                    {
                        MissionId = "msn_b",
                        ContextPackStaged = true,
                        ContextPackCompliance = "SearchBeforeRead",
                        SearchToolCallCount = 3
                    },
                    new ContextPackUsageSummary
                    {
                        MissionId = "msn_c",
                        ContextPackStaged = false,
                        ContextPackCompliance = "NoPackStagedNoSearch",
                        SearchToolCallCount = 0
                    },
                    new ContextPackUsageSummary
                    {
                        MissionId = "msn_d",
                        ContextPackStaged = true,
                        ContextPackCompliance = "PackReadNoSearch",
                        SearchToolCallCount = 0
                    }
                };

                ContextPackUsageAggregate aggregate = ContextPackUsageAggregate.FromSummaries(summaries);

                AssertEqual(4, aggregate.MissionsConsidered);
                AssertEqual(0.75, aggregate.PackStagedShare);
                AssertTrue(
                    Math.Abs(aggregate.ReadBeforeSearchShare - (2.0 / 3.0)) < 0.0001,
                    "ReadBeforeSearchShare should count ReadBeforeSearch and PackReadNoSearch over staged missions");
                AssertEqual(1.0, aggregate.AverageSearchToolCalls);
            });

            await RunTest("FromSummaries_ShareSetters_ClampToUnitInterval", () =>
            {
                ContextPackUsageAggregate aggregate = new ContextPackUsageAggregate();
                aggregate.PackStagedShare = 1.5;
                aggregate.ReadBeforeSearchShare = -0.25;

                AssertEqual(1.0, aggregate.PackStagedShare);
                AssertEqual(0.0, aggregate.ReadBeforeSearchShare);
            });
        }
    }
}
