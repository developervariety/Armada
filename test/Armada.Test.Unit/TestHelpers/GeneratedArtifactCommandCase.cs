namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Inputs and expected refusal at the generated-artifact owner boundary.</summary>
    internal sealed class GeneratedArtifactCommandCase
    {
        internal string Name { get; }
        internal Func<string, string, CancellationToken, Task<int>> Run { get; }
        internal string Reason { get; }

        internal GeneratedArtifactCommandCase(string name, Func<string, string, CancellationToken, Task<int>> run, string reason)
        {
            Name = name;
            Run = run;
            Reason = reason;
        }
    }
}
