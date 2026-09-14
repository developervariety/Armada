namespace Test.Shared.Suites.Services
{
    using System.Threading;

    /// <summary>Serializes tests that temporarily replace the process-wide OpenCode executable setting.</summary>
    internal static class OpenCodeTestEnvironmentGate
    {
        internal static readonly SemaphoreSlim Instance = new SemaphoreSlim(1, 1);
    }
}
