namespace Armada.Server
{
    using Armada.Core.Settings;

    /// <summary>Read-only preview of an unsaved usage policy for one persona and priority.</summary>
    public sealed class UsageRoutingPreviewRequest
    {
        /// <summary>Persona whose approved routes are evaluated.</summary>
        public string Persona { get; set; } = "Worker";
        /// <summary>Mission priority; lower values are more important.</summary>
        public int Priority { get; set; } = 100;
        /// <summary>Optional tier or concrete model requirement.</summary>
        public string? PreferredModel { get; set; }
        /// <summary>Draft policy; omitted uses saved settings.</summary>
        public UsageRoutingSettings? UsageRouting { get; set; }
    }
}
