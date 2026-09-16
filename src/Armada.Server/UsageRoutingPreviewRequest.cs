namespace Armada.Server
{
    using Armada.Core.Settings;

    /// <summary>Read-only Smart Routing preview of a saved or draft policy for one persona and priority.</summary>
    public sealed class UsageRoutingPreviewRequest
    {
        /// <summary>Persona whose candidates are evaluated.</summary>
        public string Persona { get; set; } = "Worker";
        /// <summary>Mission priority; lower values are more important.</summary>
        public int Priority { get; set; } = 100;
        /// <summary>Optional tier or concrete model requirement.</summary>
        public string? PreferredModel { get; set; }
        /// <summary>Optional mission title for the capacity decision.</summary>
        public string? MissionTitle { get; set; }
        /// <summary>Optional mission text for the capacity decision; when title and text are both absent the client is not called and the Default list is reported.</summary>
        public string? MissionText { get; set; }
        /// <summary>Draft policy; omitted uses saved settings.</summary>
        public UsageRoutingSettings? UsageRouting { get; set; }
    }
}
