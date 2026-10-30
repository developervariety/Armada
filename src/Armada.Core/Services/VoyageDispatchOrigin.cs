namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>Stores the prepared dispatch input on the voyage event stream.</summary>
    public sealed class VoyageDispatchOrigin
    {
        /// <summary>The event type for a dispatch origin.</summary>
        public const string EventType = "voyage.dispatch_origin";

        /// <summary>Prepared input hash that binds a future retry to changed input.</summary>
        public string? InputHash { get; set; }

        /// <summary>The exact resolved source commit used at dispatch.</summary>
        public string? SourceCommit { get; set; }

        /// <summary>Record the dispatch origin before the dispatch returns.</summary>
        public static async Task RecordAsync(DatabaseDriver database, Voyage voyage, CancellationToken token = default, string? inputHash = null, string? sourceCommit = null)
        {
            if (inputHash == null) return;
            ArmadaEvent evt = new ArmadaEvent(EventType, "Voyage dispatch origin recorded")
            {
                TenantId = voyage.TenantId,
                UserId = voyage.UserId,
                EntityType = "voyage",
                EntityId = voyage.Id,
                VoyageId = voyage.Id,
                Payload = JsonSerializer.Serialize(new VoyageDispatchOrigin { InputHash = inputHash, SourceCommit = sourceCommit })
            };
            await database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }
    }
}
