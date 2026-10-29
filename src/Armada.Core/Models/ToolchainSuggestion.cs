namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// A read-only suggestion of build, test and lint commands for one vessel checkout.
    /// Reading a suggestion does not create or change a workflow profile or a vessel command.
    /// </summary>
    public class ToolchainSuggestion
    {
        /// <summary>
        /// Vessel the suggestion was read for. Empty when the caller scanned a directory directly.
        /// </summary>
        public string? VesselId { get; set; }

        /// <summary>
        /// True when the working directory existed and could be listed.
        /// </summary>
        public bool CheckoutReadable { get; set; }

        /// <summary>
        /// True when the scan stopped at its file limit before the tree was finished.
        /// </summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// Why the suggestion is empty, when it is empty. Null when at least one ecosystem was found.
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>
        /// Ecosystems found from marker files, in a stable order.
        /// </summary>
        public List<ToolchainEcosystemSuggestion> Ecosystems { get; set; } = new List<ToolchainEcosystemSuggestion>();
    }

    /// <summary>
    /// Suggested commands for one ecosystem. The commands are catalog defaults for that ecosystem.
    /// They are not read from a workflow profile and they are not executed.
    /// </summary>
    public class ToolchainEcosystemSuggestion
    {
        /// <summary>
        /// Ecosystem name: DotNet, Node, Python, Go or Rust.
        /// </summary>
        public string Ecosystem { get; set; } = string.Empty;

        /// <summary>
        /// Marker files that selected this ecosystem, relative to the checkout, with <c>/</c> separators.
        /// </summary>
        public List<string> Markers { get; set; } = new List<string>();

        /// <summary>
        /// Suggested build command.
        /// </summary>
        public string BuildCommand { get; set; } = string.Empty;

        /// <summary>
        /// Suggested test command.
        /// </summary>
        public string TestCommand { get; set; } = string.Empty;

        /// <summary>
        /// Suggested lint command.
        /// </summary>
        public string LintCommand { get; set; } = string.Empty;
    }
}
