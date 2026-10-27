namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>Required guidance and bounded recall from one scoped query.</summary>
    public sealed class ContextSearchResponse
    {
        /// <summary>Vessel actually searched, resolved from the authenticated mission when present.</summary>
        public string VesselId { get; set; } = "";

        /// <summary>Complete core and applicable mandatory rule chunks, exempt from recall limits.</summary>
        public List<ContextSearchItem> Required { get; set; } = new List<ContextSearchItem>();

        /// <summary>Optional results ranked across sources.</summary>
        public List<ContextSearchItem> Results { get; set; } = new List<ContextSearchItem>();

        /// <summary>Availability for every requested source; an unavailable source is not an empty match.</summary>
        public List<ContextSearchStatus> Sources { get; set; } = new List<ContextSearchStatus>();

        /// <summary>True when a result count or byte limit omitted a matching optional item.</summary>
        public bool Truncated { get; set; }

        /// <summary>UTF-8 text bytes in optional results only.</summary>
        public int ResultBytes { get; set; }
    }
}
