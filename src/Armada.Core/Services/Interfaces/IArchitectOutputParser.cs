namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>Pure parser over an Architect captain's AgentOutput markdown.</summary>
    public interface IArchitectOutputParser
    {
        /// <summary>Parses the Architect captain's AgentOutput. Returns a structured result with a verdict.</summary>
        ArchitectParseResult Parse(string agentOutput);

        /// <summary>
        /// Parses the Architect captain's AgentOutput with a mission-count cap.
        /// Returns OverCap when well-formed output exceeds <paramref name="maxMissions"/>.
        /// </summary>
        /// <param name="agentOutput">Raw AgentOutput markdown from the Architect captain.</param>
        /// <param name="maxMissions">Maximum missions allowed before OverCap is returned.</param>
        ArchitectParseResult Parse(string agentOutput, int maxMissions);
    }
}
