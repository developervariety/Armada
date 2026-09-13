namespace Armada.Server
{
    using System.Text.Json.Serialization;

    /// <summary>Tracks whether a metadata request explicitly supplies immutable mission bindings.</summary>
    public sealed class MissionBindingUpdateRequest
    {
        private string? _VesselId;
        private string? _VoyageId;

        /// <summary>Gets or sets the explicitly supplied vessel binding, including null.</summary>
        public string? VesselId
        {
            get { return _VesselId; }
            set { _VesselId = value; HasVesselId = true; }
        }

        /// <summary>Gets or sets the explicitly supplied voyage binding, including null.</summary>
        public string? VoyageId
        {
            get { return _VoyageId; }
            set { _VoyageId = value; HasVoyageId = true; }
        }

        /// <summary>Gets whether the request supplied a vessel binding.</summary>
        [JsonIgnore]
        public bool HasVesselId { get; private set; }

        /// <summary>Gets whether the request supplied a voyage binding.</summary>
        [JsonIgnore]
        public bool HasVoyageId { get; private set; }
    }
}
