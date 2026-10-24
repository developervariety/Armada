namespace Armada.Core.Harbor
{
    /// <summary>Mission-scoped MCP configuration delivered only through the private launch envelope.</summary>
    public sealed class HarborMissionRuntimeConfiguration
    {
        /// <summary>Mission owner's scoped MCP token. Never persist or log this value.</summary>
        public string Token { get; set; } = string.Empty;
        /// <summary>Verified directory below the owned checkout that contains token-reference config files.</summary>
        public string Directory { get; set; } = string.Empty;
        /// <summary>Apply the fork's scoped home strategy for runtimes that require it.</summary>
        public bool ScopedHome { get; set; }
        /// <summary>Point OpenCode at its explicitly staged MCP configuration.</summary>
        public bool OpenCodeConfig { get; set; }
    }
}
