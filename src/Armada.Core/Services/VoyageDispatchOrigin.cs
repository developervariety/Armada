namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>Stores dispatch ownership on the existing voyage event stream.</summary>
    public sealed class VoyageDispatchOrigin
    {
        /// <summary>The event type for a dispatching participant.</summary>
        public const string EventType = "voyage.dispatch_origin";

        /// <summary>The authenticated dispatching session, when known.</summary>
        public string? ParticipantKey { get; set; }

        /// <summary>Prepared input hash that binds a future retry to changed input.</summary>
        public string? InputHash { get; set; }

        /// <summary>The exact resolved source commit used at dispatch.</summary>
        public string? SourceCommit { get; set; }

        /// <summary>Record the dispatching participant before the dispatch returns.</summary>
        public static async Task RecordAsync(DatabaseDriver database, Voyage voyage, string? participantKey, CancellationToken token = default, string? inputHash = null, string? sourceCommit = null)
        {
            if (String.IsNullOrWhiteSpace(participantKey) && inputHash == null) return;
            ArmadaEvent evt = new ArmadaEvent(EventType, "Voyage dispatching participant recorded")
            {
                TenantId = voyage.TenantId,
                UserId = voyage.UserId,
                EntityType = "voyage",
                EntityId = voyage.Id,
                VoyageId = voyage.Id,
                Payload = JsonSerializer.Serialize(new VoyageDispatchOrigin { ParticipantKey = participantKey?.Trim(), InputHash = inputHash, SourceCommit = sourceCommit })
            };
            await database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        /// <summary>Resolve a participant only within the mission owner's scope.</summary>
        public static async Task<string?> ReadAsync(DatabaseDriver database, Mission mission, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(mission.VoyageId)) return null;
            EnumerationResult<ArmadaEvent> page = await database.Events.EnumerateAsync(new EnumerationQuery
            {
                VoyageId = mission.VoyageId,
                EventType = EventType,
                PageSize = 1
            }, token).ConfigureAwait(false);
            foreach (ArmadaEvent evt in page.Objects)
            {
                if ((evt.TenantId ?? Constants.DefaultTenantId) != (mission.TenantId ?? Constants.DefaultTenantId) || (evt.UserId ?? Constants.DefaultUserId) != (mission.UserId ?? Constants.DefaultUserId)) continue;
                VoyageDispatchOrigin? origin = JsonSerializer.Deserialize<VoyageDispatchOrigin>(evt.Payload ?? "{}");
                return String.IsNullOrWhiteSpace(origin?.ParticipantKey) ? null : origin.ParticipantKey;
            }
            return null;
        }
    }
}
