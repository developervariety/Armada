namespace Test.Smoke
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Touchstone.XunitAdapter;
    using global::Xunit;

    /// <summary>
    /// xUnit host for the Armada smoke subset. Runs the suites listed in <see cref="SmokeSuites"/> through
    /// <c>dotnet test</c> for a fast pre-landing check; Test.Xunit remains the full run.
    /// </summary>
    public sealed class SmokeXunitTests : TouchstoneFactBase
    {
        /// <inheritdoc />
        protected override IReadOnlyList<TestSuiteDescriptor> Suites
        {
            get { return SmokeSuites.All; }
        }

        /// <summary>
        /// Execute the smoke descriptors as a single aggregated fact.
        /// </summary>
        /// <returns>Task.</returns>
        [Fact]
        public async Task RunAll()
        {
            await RunAllAsync();
        }
    }
}
