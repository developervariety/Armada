namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using Armada.Core.Models;

    internal sealed class GeneratedCandidatePublicationCase
    {
        internal string Name { get; set; } = string.Empty;
        internal string Code { get; set; } = string.Empty;
        internal Func<string, string, GeneratedArtifactReviewBinding> Bind { get; set; } = null!;
        internal bool Approve { get; set; }
        internal bool Hold { get; set; }
    }
}
