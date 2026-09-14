namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Test.Common;
    using global::Test.Shared.Infrastructure;

    /// <summary>
    /// Proves this host dispatches missions to the non-launching test runtime and carries no provider variables.
    /// </summary>
    public class TestHostRuntimeTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Name of this test suite.
        /// </summary>
        public override string Name => "Test Host Runtime";

        #endregion

        #region Private-Members

        private readonly HttpClient _AuthClient;
        private readonly IReadOnlyCollection<AgentRuntimeEnum> _RealRuntimes;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="authClient">Authenticated client.</param>
        /// <param name="realRuntimes">Runtimes the host opted in to a real launch.</param>
        public TestHostRuntimeTests(HttpClient authClient, IReadOnlyCollection<AgentRuntimeEnum> realRuntimes)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _RealRuntimes = realRuntimes ?? throw new ArgumentNullException(nameof(realRuntimes));
        }

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            const string dispatchName = "DispatchedMission_StartsTestRuntime_AndNoAgentProcess";
            if (_RealRuntimes.Contains(AgentRuntimeEnum.ClaudeCode))
            {
                SkipTest(dispatchName, "ClaudeCode is opted in to its real runtime through " + TestAgentRuntimeFactory.RealRuntimesVariable + ".");
            }
            else
            {
                await RunTest(dispatchName, async () =>
                {
                    await TestHostRuntimeScenario.RunDispatchedMissionAsync(_AuthClient).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            const string environmentName = "TestProcess_CarriesNoProviderEnvironment";
            if (TestProcessEnvironment.KeepRequested())
            {
                SkipTest(environmentName, "Provider variables were kept through " + TestProcessEnvironment.KeepVariable + ".");
            }
            else
            {
                await RunTest(environmentName, () =>
                {
                    List<string> present = Environment.GetEnvironmentVariables().Keys.Cast<object>()
                        .Select(k => k.ToString() ?? "")
                        .Where(TestProcessEnvironment.IsProviderVariable)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();
                    AssertTrue(present.Count == 0, "Provider variables present in the test process: " + String.Join(", ", present));
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
        }

        #endregion
    }
}
