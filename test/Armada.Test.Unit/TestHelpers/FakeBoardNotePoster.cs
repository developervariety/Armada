namespace Armada.Test.Unit.TestHelpers
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// A fake broadcast board-note poster for adapter tests. It records every note it was asked to
    /// post, so a test can prove a condition posts exactly one note and no other case posts any.
    /// </summary>
    public sealed class FakeBoardNotePoster : IBoardNotePoster
    {
        /// <summary>The content of every note posted, in order.</summary>
        public List<string> Posts { get; } = new List<string>();

        /// <summary>The mission id carried by each posted note, in order.</summary>
        public List<string?> MissionIds { get; } = new List<string?>();

        /// <inheritdoc />
        public Task PostBroadcastAsync(string content, string? vesselId, string? missionId, CancellationToken token)
        {
            Posts.Add(content);
            MissionIds.Add(missionId);
            return Task.CompletedTask;
        }
    }
}
