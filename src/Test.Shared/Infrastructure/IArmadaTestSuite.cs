namespace Test.Shared.Infrastructure
{
    using Touchstone.Core;

    /// <summary>
    /// Contract for an Armada test suite. Implementors are discovered by reflection
    /// (see <see cref="ArmadaTestSuites"/>) and contribute a <see cref="TestSuiteDescriptor"/>
    /// to every runner (console, xUnit, NUnit). Implementations must expose a public
    /// parameterless constructor. This replaces the retired manual <c>AddSuite</c> registry,
    /// so a newly added suite can never be silently omitted from a run.
    /// </summary>
    public interface IArmadaTestSuite
    {
        /// <summary>
        /// Build the descriptor for this suite, including its cases and optional
        /// before/after lifecycle hooks.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        TestSuiteDescriptor Build();
    }
}
