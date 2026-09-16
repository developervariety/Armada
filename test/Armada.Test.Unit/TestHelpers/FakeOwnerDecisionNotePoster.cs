namespace Armada.Test.Unit.TestHelpers
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// A fake owner-decision note poster for adapter tests. It records every owner-addressed note it
    /// was asked to post, so a test can prove a Q13 owner ruling posts exactly one note and no other
    /// case posts any.
    /// </summary>
    public sealed class FakeOwnerDecisionNotePoster : IOwnerDecisionNotePoster
    {
        /// <summary>The content of every note posted, in order.</summary>
        public List<string> Posts { get; } = new List<string>();

        /// <summary>The vessel id carried by each posted note, in order.</summary>
        public List<string?> VesselIds { get; } = new List<string?>();

        /// <inheritdoc />
        public Task PostOwnerDecisionAsync(string content, string? vesselId, CancellationToken token)
        {
            Posts.Add(content);
            VesselIds.Add(vesselId);
            return Task.CompletedTask;
        }
    }
}
