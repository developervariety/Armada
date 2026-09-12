namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A sibling repository input that must be declared and provisionable before dispatch.
    /// </summary>
    public class ObjectivePreparationSiblingInput
    {
        /// <summary>Registered vessel ID or name that supplies the sibling repository.</summary>
        public string VesselRef { get; set; } = String.Empty;

        /// <summary>Checkout path, relative to the target dock, that the consumer expects.</summary>
        public string RelativePath { get; set; } = String.Empty;

        /// <summary>Extraction artifact paths that the sibling declaration must provision.</summary>
        public List<string> RequiredArtifactPaths { get; set; } = new List<string>();
    }
}
