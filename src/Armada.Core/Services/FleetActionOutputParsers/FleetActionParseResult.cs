namespace Armada.Core.Services.FleetActionOutputParsers
{
    /// <summary>Counts and a safe summary from a complete tool response. No source text is retained.</summary>
    public sealed class FleetActionParseResult
    {
        /// <summary>Whether the response was recognized and complete.</summary>
        public bool Parsed { get; set; }
        /// <summary>Total findings, or failed tests for test reports.</summary>
        public int Total { get; set; }
        /// <summary>Critical findings.</summary>
        public int Critical { get; set; }
        /// <summary>High findings.</summary>
        public int High { get; set; }
        /// <summary>Medium findings.</summary>
        public int Medium { get; set; }
        /// <summary>Low findings.</summary>
        public int Low { get; set; }
        /// <summary>Findings whose source did not provide a recognized severity.</summary>
        public int Unrated => System.Math.Max(0, Total - Critical - High - Medium - Low);
        /// <summary>Passed tests.</summary>
        public int Passed { get; set; }
        /// <summary>Failed tests.</summary>
        public int Failed { get; set; }
        /// <summary>Skipped tests.</summary>
        public int Skipped { get; set; }
        /// <summary>Plain text containing counts only, never secret values.</summary>
        public string Summary { get; set; } = "Output could not be parsed.";
    }
    /// <summary>A stateless output parser.</summary>
    public interface IFleetActionOutputParser
    {
        /// <summary>Parse without retaining potentially sensitive output.</summary>
        FleetActionParseResult Parse(string? output, System.Threading.CancellationToken token = default);
    }
}
