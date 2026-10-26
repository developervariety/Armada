namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private sealed class ArchitectAsyncParseResult
        {
            public List<ParsedArchitectMission> Missions { get; set; } = new List<ParsedArchitectMission>();
            public string? FallbackReason { get; set; }
            public string? InvalidPlanReason { get; set; }
        }
    }
}
