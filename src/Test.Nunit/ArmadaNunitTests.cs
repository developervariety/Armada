namespace Test.Nunit
{
    using System;
    using System.Collections;
    using System.Threading;
    using System.Threading.Tasks;
    using Test.Shared;
    using Touchstone.Core;
    using Touchstone.NunitAdapter;
    using global::NUnit.Framework;

    /// <summary>
    /// NUnit host for the shared Armada test descriptors. Uses <c>TestCaseSource</c> so each
    /// descriptor surfaces as its own NUnit test for granular reporting under <c>dotnet test</c>.
    /// </summary>
    [TestFixture]
    public sealed class ArmadaNunitTests
    {
        private static IEnumerable TestCases()
        {
            return new TouchstoneTestCaseSource(ArmadaTestSuites.All);
        }

        /// <summary>
        /// Execute a single shared descriptor.
        /// </summary>
        /// <param name="testCase">The descriptor to run.</param>
        /// <returns>Task.</returns>
        [Test]
        [TestCaseSource(nameof(TestCases))]
        public async Task RunTest(TestCaseDescriptor testCase)
        {
            // Hard per-case timeout so a single hung case (e.g. a stuck socket) fails that case
            // instead of stalling the entire run. The token is passed through for cooperative
            // cancellation; the Task.Delay race is the wall-clock backstop.
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Task work = testCase.ExecuteAsync(cts.Token);
            Task finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
            if (finished != work)
            {
                try { cts.Cancel(); } catch { }
                throw new TimeoutException("Test case '" + testCase.TestId + "' exceeded the 60s timeout.");
            }

            await work.ConfigureAwait(false);
        }
    }
}
