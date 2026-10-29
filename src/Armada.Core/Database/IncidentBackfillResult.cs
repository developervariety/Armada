namespace Armada.Core.Database
{
    using System.Collections.Generic;

    /// <summary>
    /// Outcome of projecting incident snapshot events that have no projection row.
    /// </summary>
    public sealed class IncidentBackfillResult
    {
        /// <summary>Incidents projected.</summary>
        public int Projected { get; set; }

        /// <summary>Identifiers of incidents whose newest snapshot holds no readable incident; none is projected for them.</summary>
        public List<string> Unreadable { get; set; } = new List<string>();
    }
}
