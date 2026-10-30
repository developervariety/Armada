namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Inputs and expected refusal at the generated-artifact owner boundary.</summary>
    internal sealed class GeneratedArtifactPolicyCase
    {
        internal string Name { get; }
        internal GeneratedArtifactPolicy Policy { get; }
        internal bool Dirty { get; }
        internal string Reason { get; }

        internal GeneratedArtifactPolicyCase(string name, GeneratedArtifactPolicy policy, bool dirty, string reason)
        {
            Name = name;
            Policy = policy;
            Dirty = dirty;
            Reason = reason;
        }
    }
}
