namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;

    public partial class AdmiralService
    {
        private sealed class HarborCompletionPayload
        {
            public string MissionId { get; set; } = String.Empty;
            public string CaptainId { get; set; } = String.Empty;
            public string? DockId { get; set; }
            public string? VesselId { get; set; }
            public string? VoyageId { get; set; }
            public MissionAttemptFactTypeEnum FactType { get; set; }
            public string EventType { get; set; } = String.Empty;
            public string EventMessage { get; set; } = String.Empty;
            public string? SignalMessage { get; set; }
            public int? RecoveryAttempts { get; set; }
            public DateTime CreatedUtc { get; set; }
        }
    }
}
