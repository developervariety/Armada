namespace Test.Xunit
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Test.Shared;
    using Touchstone.Core;
    using Touchstone.XunitAdapter;
    using global::Xunit;

    /// <summary>
    /// xUnit host for the shared Armada test descriptors. Runs every discovered suite
    /// through the standard <c>dotnet test</c> pipeline so the same cases that run under the
    /// console runner also run in CI.
    /// </summary>
    public sealed class ArmadaXunitTests : TouchstoneFactBase
    {
        /// <inheritdoc />
        protected override IReadOnlyList<TestSuiteDescriptor> Suites
        {
            get { return ArmadaTestSuites.All; }
        }

        /// <summary>
        /// Execute all shared descriptors as a single aggregated fact.
        /// </summary>
        /// <returns>Task.</returns>
        [Fact]
        public async Task RunAll()
        {
            await RunAllAsync();
        }
    }
}
