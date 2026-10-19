namespace Armada.Server.Mcp
{
    using Armada.Core.Models;

    /// <summary>Source and edited configuration for secure endpoint duplication.</summary>
    public class ModelEndpointDuplicateArgs
    {
        /// <summary>Editable source endpoint ID.</summary>
        public string EndpointId { get; set; } = string.Empty;
        /// <summary>Complete edited endpoint configuration; omitted credentials inherit server-side.</summary>
        public ModelEndpoint Configuration { get; set; } = new ModelEndpoint();
    }
}
