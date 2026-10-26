namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>Operator-declared source paths whose changes can affect one required gate input.</summary>
    public class ObjectiveGateInputSources
    {
        /// <summary>Exact repository-relative gate input path or glob declared by the workflow profile.</summary>
        public string InputPath { get; set; } = "";

        /// <summary>Repository-relative source paths or globs that can affect this input.</summary>
        public List<string> SourcePaths { get; set; } = new List<string>();
    }
}
