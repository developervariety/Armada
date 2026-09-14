namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;

    /// <summary>
    /// End-to-end proof that the shared fixture dispatches missions to the non-launching test runtime.
    /// </summary>
    public sealed class TestHostRuntimeSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            bool claudeOptedIn = TestAgentRuntimeFactory.ParseRuntimeNamesOrEmpty(
                Environment.GetEnvironmentVariable(TestAgentRuntimeFactory.RealRuntimesVariable)).Contains(AgentRuntimeEnum.ClaudeCode);

            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor(
                    suiteId: "E2E.TestHostRuntime",
                    caseId: "dispatched_mission_starts_test_runtime_and_no_agent_process",
                    displayName: "DispatchedMission_StartsTestRuntime_AndNoAgentProcess",
                    executeAsync: async (CancellationToken ct) =>
                    {
                        E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                        await TestHostRuntimeScenario.RunDispatchedMissionAsync(fx.AuthClient).ConfigureAwait(false);
                    },
                    tags: new List<string> { TestTags.Positive, TestTags.EndToEnd },
                    skip: claudeOptedIn,
                    skipReason: claudeOptedIn
                        ? "ClaudeCode is opted in to its real runtime through " + TestAgentRuntimeFactory.RealRuntimesVariable + "."
                        : null)
            };

            return new TestSuiteDescriptor("E2E.TestHostRuntime", "Test Host Runtime", cases);
        }

        #endregion
    }
}
